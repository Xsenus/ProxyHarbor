using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ProxyHarbor.Api;
using ProxyHarbor.Domain;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Api.Controllers;

/// <summary>Закрытый pull-протокол между центральным сервисом и внешними checker-agent.</summary>
[ApiController, AllowAnonymous, Route("api/v1/checker-agent")]
public sealed class CheckerAgentController(
    CheckerNodeCredentialCache credentials,
    DistributedProxyValidationService validation,
    ILogger<CheckerAgentController> logger) : ControllerBase
{
    private static readonly Action<ILogger, Guid, Guid, string, Exception?> LeaseRejected =
        LoggerMessage.Define<Guid, Guid, string>(LogLevel.Warning,
            new EventId(4109, nameof(LeaseRejected)),
            "Checker node {NodeId} result for lease {LeaseId} was rejected: {Reason}");
    /// <summary>Records liveness and current agent workload.</summary>
    [HttpPost("heartbeat")]
    public async Task<IActionResult> Heartbeat([FromBody] CheckerHeartbeatRequest request, CancellationToken token)
    {
        var node = await AuthenticateAsync(token);
        if (node is null) return Unauthorized();
        await validation.TouchAsync(node.Value, request, HttpContext.Connection.RemoteIpAddress?.ToString(), token);
        return NoContent();
    }

    /// <summary>Atomically reserves a unique due batch for the authenticated agent.</summary>
    [HttpPost("lease")]
    public async Task<ActionResult<CheckerLeaseResponse>> Lease(CancellationToken token)
    {
        var node = await AuthenticateAsync(token);
        if (node is null) return Unauthorized();
        var lease = await validation.ClaimAsync(node.Value, token);
        return lease is null ? NoContent() : Ok(lease);
    }

    /// <summary>Extends an active lease while a batch is being probed.</summary>
    [HttpPost("leases/{leaseId:guid}/heartbeat")]
    public async Task<IActionResult> Renew(
        Guid leaseId, [FromBody] CheckerHeartbeatRequest request, CancellationToken token)
    {
        var node = await AuthenticateAsync(token);
        if (node is null) return Unauthorized();
        return await validation.RenewAsync(node.Value, leaseId, request, token) ? NoContent() : Conflict(new
        {
            error = "lease_lost",
            message = "Аренда истекла или была возвращена в общую очередь. Результаты этой партии принимать нельзя."
        });
    }

    /// <summary>Commits a complete batch or rejects it after ownership/expiry loss.</summary>
    [HttpPost("leases/{leaseId:guid}/results")]
    [RequestSizeLimit(8 * 1024 * 1024)]
    public async Task<ActionResult<CheckerLeaseCompletion>> Complete(
        Guid leaseId, [FromBody] CheckerLeaseResultRequest request, CancellationToken token)
    {
        var node = await AuthenticateAsync(token);
        if (node is null) return Unauthorized();
        if (request.Results.Count is < 1 or > 10_000) return ValidationProblem("Results: 1..10000");
        try
        {
            return Ok(await validation.CompleteAsync(node.Value, leaseId, request, token));
        }
        catch (InvalidOperationException exception)
        {
            LeaseRejected(logger, node.Value, leaseId, exception.Message, exception);
            return Conflict(new { error = "lease_lost", message = exception.Message });
        }
    }

    private async Task<Guid?> AuthenticateAsync(CancellationToken token)
    {
        if (!Request.Headers.TryGetValue("X-Checker-Node", out var nodeHeader) ||
            nodeHeader.Count != 1 ||
            !Guid.TryParse(nodeHeader[0], out var nodeId) ||
            !Request.Headers.TryGetValue("Authorization", out var authorization) ||
            authorization.Count != 1 ||
            authorization[0] is not { } authorizationValue ||
            !authorizationValue.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return null;
        var raw = authorizationValue[7..].Trim();
        if (raw.Length is < 32 or > 256) return null;
        return await credentials.AuthenticateAsync(nodeId, raw, token) ? nodeId : null;
    }
}
