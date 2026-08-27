using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Renci.SshNet;

namespace ProxyHarbor.Api;

/// <summary>Settings used to install checker agents on administrator-supplied VPS hosts.</summary>
public sealed class CheckerAgentDeploymentOptions
{
    /// <summary>Configuration section name.</summary>
    public const string Section = "CheckerAgentDeployment";
    /// <summary>Public HTTPS address agents use to reach the control plane.</summary>
    public string PublicBaseUrl { get; set; } = "https://proxy.blagodaty.ru";
    /// <summary>Container image installed on checker hosts.</summary>
    public string Image { get; set; } = "ghcr.io/xsenus/proxyharbor-checker-agent:latest";
}

/// <summary>Transient SSH credentials and sizing used for checker deployment.</summary>
public sealed record CheckerNodeDeploymentRequest(
    string Name, string Host, int SshPort, string SshUsername, string Password,
    int Concurrency = 200, int BatchSize = 400);

/// <summary>Mutable scheduling settings of a checker node.</summary>
public sealed record CheckerNodeUpdateRequest(bool Enabled, int Concurrency, int BatchSize);

/// <summary>Non-secret result of a successful checker deployment.</summary>
public sealed record CheckerNodeDeploymentResult(string HostKeyFingerprint, string AgentContainerId);

/// <summary>Разворачивает изолированный checker-контейнер, не сохраняя SSH-пароль.</summary>
public sealed class CheckerNodeProvisioner(IOptions<CheckerAgentDeploymentOptions> configured)
{
    /// <summary>Installs or replaces the isolated agent container and verifies the SSH host key.</summary>
    public async Task<CheckerNodeDeploymentResult> DeployAsync(
        Guid nodeId,
        CheckerNodeDeploymentRequest request,
        string agentToken,
        string? expectedHostKeyFingerprint,
        CancellationToken token)
    {
        Validate(request);
        var settings = configured.Value;
        if (!Uri.TryCreate(settings.PublicBaseUrl, UriKind.Absolute, out var control) ||
            control.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(control.UserInfo) ||
            control.AbsolutePath != "/" || !string.IsNullOrEmpty(control.Query) ||
            !string.IsNullOrEmpty(control.Fragment))
            throw new InvalidOperationException(
                "CheckerAgentDeployment:PublicBaseUrl должен быть корневым HTTPS origin без пути, query и fragment.");
        if (!System.Text.RegularExpressions.Regex.IsMatch(settings.Image,
            @"^[a-z0-9][a-z0-9._/-]{2,200}:[A-Za-z0-9._-]{1,80}$"))
            throw new InvalidOperationException("CheckerAgentDeployment:Image содержит небезопасное имя.");

        return await Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            var connection = new PasswordConnectionInfo(request.Host, request.SshPort,
                request.SshUsername, request.Password)
            {
                Timeout = TimeSpan.FromSeconds(20)
            };
            using var client = new SshClient(connection);
            string? fingerprint = null;
            client.HostKeyReceived += (_, args) =>
            {
                fingerprint = "SHA256:" + Convert.ToBase64String(SHA256.HashData(args.HostKey)).TrimEnd('=');
                args.CanTrust = string.IsNullOrEmpty(expectedHostKeyFingerprint) ||
                    CryptographicOperations.FixedTimeEquals(
                        Encoding.ASCII.GetBytes(fingerprint),
                        Encoding.ASCII.GetBytes(expectedHostKeyFingerprint));
            };
            client.Connect();
            token.ThrowIfCancellationRequested();
            if (!client.IsConnected || fingerprint is null)
                throw new InvalidOperationException("SSH-соединение не было установлено.");

            var tokenBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(agentToken));
            var script = BuildInstallScript(nodeId, tokenBase64, control, settings.Image);
            using var command = client.CreateCommand("sh -s");
            command.CommandTimeout = TimeSpan.FromMinutes(12);
            command.Execute(script);
            if (command.ExitStatus != 0)
                throw new InvalidOperationException(BoundedDiagnostic(command.Error));
            var containerId = command.Result.Trim();
            if (containerId.Length < 12)
                throw new InvalidOperationException("Docker не вернул идентификатор запущенного checker-agent.");
            client.Disconnect();
            return new CheckerNodeDeploymentResult(fingerprint, containerId[..Math.Min(64, containerId.Length)]);
        }, token);
    }

    /// <summary>Removes only the ProxyHarbor checker container and its token from the remote host.</summary>
    public async Task RemoveAsync(CheckerNodeDeploymentRequest request, string? expectedFingerprint, CancellationToken token)
    {
        Validate(request);
        await Task.Run(() =>
        {
            var connection = new PasswordConnectionInfo(request.Host, request.SshPort,
                request.SshUsername, request.Password)
            { Timeout = TimeSpan.FromSeconds(20) };
            using var client = new SshClient(connection);
            client.HostKeyReceived += (_, args) =>
            {
                var actual = "SHA256:" + Convert.ToBase64String(SHA256.HashData(args.HostKey)).TrimEnd('=');
                args.CanTrust = !string.IsNullOrEmpty(expectedFingerprint) && actual == expectedFingerprint;
            };
            client.Connect();
            using var command = client.CreateCommand(
                "docker rm --force proxyharbor-checker-agent >/dev/null 2>&1 || true; " +
                "rm -f /opt/proxyharbor-checker/agent_token");
            command.CommandTimeout = TimeSpan.FromMinutes(2);
            command.Execute();
            client.Disconnect();
        }, token);
    }

    private static string BuildInstallScript(Guid nodeId, string tokenBase64, Uri control, string image) => $"""
        set -eu
        if [ "$(id -u)" -ne 0 ]; then
          echo 'Checker deployment requires root or a root SSH account.' >&2
          exit 40
        fi
        if ! command -v docker >/dev/null 2>&1; then
          if ! command -v apt-get >/dev/null 2>&1; then
            echo 'Docker is absent and this Linux distribution is not supported for automatic installation.' >&2
            exit 41
          fi
          export DEBIAN_FRONTEND=noninteractive
          apt-get update -qq
          apt-get install -y -qq docker.io ca-certificates
          systemctl enable --now docker
        fi
        install -d -m 0700 /opt/proxyharbor-checker
        printf '%s' '{tokenBase64}' | base64 -d > /opt/proxyharbor-checker/agent_token.tmp
        chmod 0400 /opt/proxyharbor-checker/agent_token.tmp
        mv /opt/proxyharbor-checker/agent_token.tmp /opt/proxyharbor-checker/agent_token
        docker pull '{image}' >/dev/null 2>&1 || docker image inspect '{image}' >/dev/null
        docker rm --force proxyharbor-checker-agent >/dev/null 2>&1 || true
        docker run --detach --name proxyharbor-checker-agent --restart unless-stopped \
          --read-only --tmpfs /tmp:rw,noexec,nosuid,size=32m \
          --pids-limit 512 --memory 1536m --cpus 2 \
          --cap-drop ALL --security-opt no-new-privileges:true \
          --ulimit nofile=8192:8192 \
          --mount type=bind,src=/opt/proxyharbor-checker/agent_token,dst=/run/secrets/agent_token,readonly \
          --env CheckerAgent__ControlPlaneBaseUrl='{control.GetLeftPart(UriPartial.Authority)}' \
          --env CheckerAgent__NodeId='{nodeId:D}' \
          --env CheckerAgent__TokenFile='/run/secrets/agent_token' \
          '{image}'
        """;

    /// <summary>Validates all administrator-provided deployment fields before opening SSH.</summary>
    public static void Validate(CheckerNodeDeploymentRequest request)
    {
        if (request.Name.Trim().Length is < 2 or > 100) throw new ArgumentException("Имя: 2..100 символов.");
        if (!IPAddress.TryParse(request.Host, out var address) || !Infrastructure.NetworkSafety.IsPublicAddress(address))
            throw new ArgumentException("Host должен быть публичным IP-адресом VPS.");
        if (request.SshPort is < 1 or > 65_535) throw new ArgumentException("SSH port: 1..65535.");
        if (!System.Text.RegularExpressions.Regex.IsMatch(request.SshUsername, @"^[a-z_][a-z0-9_-]{0,31}$"))
            throw new ArgumentException("Некорректное имя SSH-пользователя.");
        if (request.Password.Length is < 8 or > 512 || request.Password.Any(char.IsControl))
            throw new ArgumentException("Некорректный SSH-пароль.");
        if (request.Concurrency is < 1 or > 1_000) throw new ArgumentException("Concurrency: 1..1000.");
        if (request.BatchSize is < 1 or > 10_000) throw new ArgumentException("BatchSize: 1..10000.");
    }

    private static string BoundedDiagnostic(string? value)
    {
        var diagnostic = string.IsNullOrWhiteSpace(value) ? "Удалённая установка завершилась ошибкой." : value.Trim();
        return diagnostic[..Math.Min(1000, diagnostic.Length)];
    }
}
