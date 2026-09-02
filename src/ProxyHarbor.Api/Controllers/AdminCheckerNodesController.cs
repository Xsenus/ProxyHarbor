using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ProxyHarbor.Api;
using ProxyHarbor.Domain;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Api.Controllers;

/// <summary>Administration API for distributed checker nodes.</summary>
[ApiController, Authorize(Roles = UserRoles.Administrator), Route("api/v1/admin/checker-nodes")]
public sealed class AdminCheckerNodesController(
    ProxyHarborDbContext db,
    CheckerNodeProvisioner provisioner,
    IOptions<CheckerAgentDeploymentOptions> deployment,
    CheckerNodeCredentialCache credentials) : ControllerBase
{
    /// <summary>Returns nodes with derived online and lease state.</summary>
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken token)
    {
        var now = DateTimeOffset.UtcNow;
        var nodes = await db.CheckerNodes.AsNoTracking().OrderBy(x => x.Name).Select(x => new
        {
            x.Id,
            x.Name,
            x.Host,
            x.SshPort,
            x.SshUsername,
            x.Enabled,
            x.Concurrency,
            x.BatchSize,
            x.CreatedAt,
            x.UpdatedAt,
            x.LastHeartbeatAt,
            x.LastLeaseAt,
            x.LastCompletedAt,
            x.CurrentLeaseId,
            x.CurrentLeaseUntil,
            x.AgentVersion,
            x.RemoteAddress,
            x.DeploymentStatus,
            x.LastError,
            x.CompletedChecks,
            x.AliveChecks,
            HostKeyFingerprint = x.HostKeyFingerprint,
            Online = x.Enabled && x.LastHeartbeatAt >= now.AddMinutes(-2),
            Busy = x.CurrentLeaseId != null && x.CurrentLeaseUntil >= now
        }).ToListAsync(token);
        return Ok(new
        {
            image = deployment.Value.Image,
            nativeAssetBaseUrl = deployment.Value.NativeAssetBaseUrl,
            items = nodes
        });
    }

    /// <summary>Registers a node and installs its agent through a one-time SSH connection.</summary>
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CheckerNodeDeploymentRequest request, CancellationToken token)
    {
        try { CheckerNodeProvisioner.Validate(request); }
        catch (ArgumentException exception) { return ValidationProblem(exception.Message); }
        if (await db.CheckerNodes.AnyAsync(x => x.Name == request.Name.Trim(), token))
            return Conflict(new { message = "Checker-узел с таким именем уже существует." });

        var secret = NewToken();
        var node = new CheckerNode
        {
            Name = request.Name.Trim(),
            Host = request.Host,
            SshPort = request.SshPort,
            SshUsername = request.SshUsername,
            TokenHash = Hash(secret),
            Concurrency = request.Concurrency,
            BatchSize = request.BatchSize,
            DeploymentStatus = "deploying"
        };
        db.CheckerNodes.Add(node);
        await db.SaveChangesAsync(token);
        credentials.Invalidate();
        try
        {
            var result = await provisioner.DeployAsync(node.Id, request, secret, null, token);
            node.HostKeyFingerprint = result.HostKeyFingerprint;
            node.DeploymentStatus = "starting";
            node.LastError = null;
            node.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(token);
            return Created($"/api/v1/admin/checker-nodes/{node.Id}", new
            {
                node.Id,
                node.Name,
                node.DeploymentStatus,
                result.DeploymentMode
            });
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            node.DeploymentStatus = "failed";
            node.LastError = Bounded(exception.Message);
            node.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(CancellationToken.None);
            return StatusCode(StatusCodes.Status502BadGateway,
                new { node.Id, message = "VPS добавлен, но агент не запущен.", error = node.LastError });
        }
    }

    /// <summary>Updates node availability and work limits.</summary>
    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] CheckerNodeUpdateRequest request, CancellationToken token)
    {
        if (request.Concurrency is < 1 or > 1_000 || request.BatchSize is < 1 or > 10_000)
            return ValidationProblem("Concurrency: 1..1000, BatchSize: 1..10000.");
        var updated = await db.CheckerNodes.Where(x => x.Id == id).ExecuteUpdateAsync(setters => setters
            .SetProperty(x => x.Enabled, request.Enabled)
            .SetProperty(x => x.Concurrency, request.Concurrency)
            .SetProperty(x => x.BatchSize, request.BatchSize)
            .SetProperty(x => x.UpdatedAt, DateTimeOffset.UtcNow), token);
        if (updated != 1) return NotFound();
        credentials.Invalidate();
        return NoContent();
    }

    /// <summary>Rotates the agent token and safely redeploys the checker container.</summary>
    [HttpPost("{id:guid}/deploy")]
    public async Task<IActionResult> Redeploy(Guid id, [FromBody] CheckerNodeSshPasswordRequest request, CancellationToken token)
    {
        var node = await db.CheckerNodes.SingleOrDefaultAsync(x => x.Id == id, token);
        if (node is null) return NotFound();
        var secret = NewToken();
        var deploymentRequest = new CheckerNodeDeploymentRequest(node.Name, node.Host, node.SshPort,
            node.SshUsername, request.Password, node.Concurrency, node.BatchSize);
        try
        {
            node.DeploymentStatus = "deploying";
            node.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(token);
            var result = await provisioner.DeployAsync(node.Id, deploymentRequest, secret,
                node.HostKeyFingerprint, token);
            node.TokenHash = Hash(secret);
            node.DeploymentStatus = "starting";
            node.LastError = null;
            node.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(token);
            credentials.Invalidate();
            return Accepted(new { node.Id, node.DeploymentStatus, result.DeploymentMode });
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            node.DeploymentStatus = "failed";
            node.LastError = Bounded(exception.Message);
            node.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(CancellationToken.None);
            return StatusCode(StatusCodes.Status502BadGateway, new { message = node.LastError });
        }
    }

    /// <summary>Removes the remote agent before deleting its central registration.</summary>
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, [FromBody] CheckerNodeSshPasswordRequest request, CancellationToken token)
    {
        var node = await db.CheckerNodes.SingleOrDefaultAsync(x => x.Id == id, token);
        if (node is null) return NotFound();
        var deploymentRequest = new CheckerNodeDeploymentRequest(node.Name, node.Host, node.SshPort,
            node.SshUsername, request.Password, node.Concurrency, node.BatchSize);
        try { await provisioner.RemoveAsync(deploymentRequest, node.HostKeyFingerprint, token); }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return StatusCode(StatusCodes.Status502BadGateway,
                new { message = "Агент не удалён с VPS; запись сохранена.", error = Bounded(exception.Message) });
        }
        db.CheckerNodes.Remove(node);
        await db.SaveChangesAsync(token);
        credentials.Invalidate();
        return NoContent();
    }

    private static string NewToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(48))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static byte[] Hash(string value) => SHA256.HashData(Encoding.UTF8.GetBytes(value));
    private static string Bounded(string value) => value[..Math.Min(1000, value.Length)];
}

/// <summary>Transient password used only for an explicit deploy or removal request.</summary>
public sealed record CheckerNodeSshPasswordRequest(string Password);
