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
    /// <summary>HTTPS directory containing the framework-dependent agent archive and checksum.</summary>
    public string NativeAssetBaseUrl { get; set; } =
        "https://github.com/Xsenus/ProxyHarbor/releases/latest/download";
    /// <summary>Private .NET runtime version installed for the systemd fallback.</summary>
    public string NativeRuntimeVersion { get; set; } = "10.0.11";
    /// <summary>SHA-512 of the official Microsoft linux-x64 runtime archive.</summary>
    public string NativeRuntimeLinuxX64Sha512 { get; set; } =
        "64c77a5f98d6dfc63393ed5d2fed47c2855c7cdd4322008b3b13e1d0b710aefc1fba7e56612f25f7e8a2b1b6cef5cf77cb3a834ff3685e723ad286703c3396d8";
    /// <summary>SHA-512 of the official Microsoft linux-arm64 runtime archive.</summary>
    public string NativeRuntimeLinuxArm64Sha512 { get; set; } =
        "2a729a4eff6a55e271b3ae7d4f2be98aef16df22e3b9316827558e204f7881bbf0b7b9f8ebcfb1a1419c87fbcb8a00f5be72b6474e02c695c30b2c52bafb65ed";
    /// <summary>Minimum free space required for an atomic native installation.</summary>
    public int NativeMinimumFreeMegabytes { get; set; } = 300;
}

/// <summary>Transient SSH credentials and sizing used for checker deployment.</summary>
public sealed record CheckerNodeDeploymentRequest(
    string Name, string Host, int SshPort, string SshUsername, string Password,
    int Concurrency = 200, int BatchSize = 400);

/// <summary>Mutable scheduling settings of a checker node.</summary>
public sealed record CheckerNodeUpdateRequest(bool Enabled, int Concurrency, int BatchSize);

/// <summary>Non-secret result of a successful checker deployment.</summary>
public sealed record CheckerNodeDeploymentResult(
    string HostKeyFingerprint, string DeploymentMode, string AgentInstanceId);

/// <summary>Разворачивает checker в Docker либо как защищённый systemd-сервис, не сохраняя SSH-пароль.</summary>
public sealed class CheckerNodeProvisioner(IOptions<CheckerAgentDeploymentOptions> configured)
{
    /// <summary>Installs or replaces the isolated agent and verifies the SSH host key.</summary>
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
        ValidateDeploymentOptions(settings);

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
            var script = BuildInstallScript(nodeId, tokenBase64, control, settings);
            using var command = client.CreateCommand("sh -s");
            command.CommandTimeout = TimeSpan.FromMinutes(12);
            command.Execute(script);
            if (command.ExitStatus != 0)
                throw new InvalidOperationException(BoundedDiagnostic(command.Error));
            var result = command.Result.Trim().Split(':', 2, StringSplitOptions.TrimEntries);
            if (result.Length != 2 || result[0] is not ("docker" or "systemd") || result[1].Length < 1)
                throw new InvalidOperationException("VPS не вернул идентификатор запущенного checker-agent.");
            client.Disconnect();
            return new CheckerNodeDeploymentResult(
                fingerprint, result[0], result[1][..Math.Min(64, result[1].Length)]);
        }, token);
    }

    /// <summary>Removes only ProxyHarbor checker units, containers, binaries and tokens from the remote host.</summary>
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
                "set -eu; " +
                "if command -v systemctl >/dev/null 2>&1; then " +
                "systemctl disable --now proxyharbor-checker-agent.service >/dev/null 2>&1 || true; " +
                "rm -f /etc/systemd/system/proxyharbor-checker-agent.service; systemctl daemon-reload; fi; " +
                "if command -v docker >/dev/null 2>&1; then " +
                "docker rm --force proxyharbor-checker-agent proxyharbor-checker-agent-next >/dev/null 2>&1 || true; fi; " +
                "rm -rf /opt/proxyharbor-checker/app /opt/proxyharbor-checker/app.previous " +
                "/opt/proxyharbor-checker/dotnet /opt/proxyharbor-checker/dotnet.previous; " +
                "rm -f /opt/proxyharbor-checker/agent_token /opt/proxyharbor-checker/agent_token.next");
            command.CommandTimeout = TimeSpan.FromMinutes(2);
            command.Execute();
            if (command.ExitStatus != 0)
                throw new InvalidOperationException(BoundedDiagnostic(command.Error));
            client.Disconnect();
        }, token);
    }

    internal static string BuildInstallScript(
        Guid nodeId, string tokenBase64, Uri control, CheckerAgentDeploymentOptions settings)
    {
        var unit = Convert.ToBase64String(Encoding.UTF8.GetBytes(BuildSystemdUnit(nodeId, control)));
        var script = """
        set -eu
        if [ "$(id -u)" -ne 0 ]; then
          echo 'Checker deployment requires root or a root SSH account.' >&2
          exit 40
        fi
        install -d -m 0700 /opt/proxyharbor-checker
        printf '%s' '__TOKEN_BASE64__' | base64 -d > /opt/proxyharbor-checker/agent_token.next
        chmod 0400 /opt/proxyharbor-checker/agent_token.next

        if command -v docker >/dev/null 2>&1 && docker info >/dev/null 2>&1; then
          docker pull '__IMAGE__' >/dev/null 2>&1 || docker image inspect '__IMAGE__' >/dev/null
          docker rm --force proxyharbor-checker-agent-next >/dev/null 2>&1 || true
          if [ -f /opt/proxyharbor-checker/agent_token ]; then
            cp -p /opt/proxyharbor-checker/agent_token /opt/proxyharbor-checker/agent_token.previous
          fi
          mv /opt/proxyharbor-checker/agent_token.next /opt/proxyharbor-checker/agent_token
          next_id=$(docker run --detach --name proxyharbor-checker-agent-next --restart unless-stopped \
            --read-only --tmpfs /tmp:rw,noexec,nosuid,size=32m \
            --pids-limit 512 --memory 1536m --cpus 2 \
            --cap-drop ALL --security-opt no-new-privileges:true \
            --ulimit nofile=8192:8192 \
            --mount type=bind,src=/opt/proxyharbor-checker/agent_token,dst=/run/secrets/agent_token,readonly \
            --env CheckerAgent__ControlPlaneBaseUrl='__CONTROL_ORIGIN__' \
            --env CheckerAgent__NodeId='__NODE_ID__' \
            --env CheckerAgent__TokenFile='/run/secrets/agent_token' \
            '__IMAGE__')
          if [ "$(docker inspect --format '{{.State.Running}}' proxyharbor-checker-agent-next)" != true ]; then
            docker logs --tail 30 proxyharbor-checker-agent-next >&2 || true
            docker rm --force proxyharbor-checker-agent-next >/dev/null 2>&1 || true
            if [ -f /opt/proxyharbor-checker/agent_token.previous ]; then
              mv /opt/proxyharbor-checker/agent_token.previous /opt/proxyharbor-checker/agent_token
            fi
            exit 42
          fi
          if command -v systemctl >/dev/null 2>&1; then
            systemctl disable --now proxyharbor-checker-agent.service >/dev/null 2>&1 || true
          fi
          docker rm --force proxyharbor-checker-agent >/dev/null 2>&1 || true
          docker rename proxyharbor-checker-agent-next proxyharbor-checker-agent
          rm -f /opt/proxyharbor-checker/agent_token.previous
          printf 'docker:%s\n' "$next_id"
          exit 0
        fi

        for tool in systemctl curl tar sha256sum sha512sum useradd; do
          if ! command -v "$tool" >/dev/null 2>&1; then
            echo "Native checker deployment requires $tool when Docker is absent." >&2
            exit 43
          fi
        done
        available_mb=$(df -Pm /opt | awk 'NR==2 {print $4}')
        if [ -z "$available_mb" ] || [ "$available_mb" -lt __MINIMUM_FREE_MB__ ]; then
          echo "Native checker deployment requires at least __MINIMUM_FREE_MB__ MB free under /opt; available: ${available_mb:-unknown} MB." >&2
          exit 44
        fi
        machine=$(uname -m)
        case "$machine" in
          x86_64|amd64)
            runtime_rid=linux-x64
            runtime_sha='__RUNTIME_X64_SHA512__'
            ;;
          aarch64|arm64)
            runtime_rid=linux-arm64
            runtime_sha='__RUNTIME_ARM64_SHA512__'
            ;;
          *) echo "Unsupported native checker architecture: $machine" >&2; exit 45 ;;
        esac

        if ! id proxyharbor-checker >/dev/null 2>&1; then
          useradd --system --user-group --home-dir /nonexistent --shell /usr/sbin/nologin proxyharbor-checker
        fi
        # The directory is initially private because it also serves Docker token staging.
        # Native systemd execution needs traversal permission without exposing the token.
        chown root:proxyharbor-checker /opt/proxyharbor-checker
        chmod 0750 /opt/proxyharbor-checker
        work=$(mktemp -d /opt/proxyharbor-checker/install.XXXXXX)
        cleanup() { rm -rf "$work"; }
        trap cleanup EXIT HUP INT TERM
        asset_base='__ASSET_BASE_URL__'
        curl --fail --location --silent --show-error --retry 3 \
          "$asset_base/proxyharbor-checker-agent.tar.gz" -o "$work/proxyharbor-checker-agent.tar.gz"
        curl --fail --location --silent --show-error --retry 3 \
          "$asset_base/proxyharbor-checker-agent.tar.gz.sha256" -o "$work/proxyharbor-checker-agent.tar.gz.sha256"
        (cd "$work" && sha256sum --check proxyharbor-checker-agent.tar.gz.sha256 >/dev/null)
        mkdir "$work/app"
        tar -xzf "$work/proxyharbor-checker-agent.tar.gz" -C "$work/app"
        test -r "$work/app/ProxyHarbor.CheckerAgent.dll"

        runtime_root=/opt/proxyharbor-checker/dotnet
        runtime_replaced=0
        runtime_existed=0
        if [ ! -x "$runtime_root/dotnet" ] || ! "$runtime_root/dotnet" --list-runtimes 2>/dev/null | grep -q '^Microsoft.NETCore.App __RUNTIME_VERSION__ '; then
          runtime_url="https://builds.dotnet.microsoft.com/dotnet/Runtime/__RUNTIME_VERSION__/dotnet-runtime-__RUNTIME_VERSION__-$runtime_rid.tar.gz"
          curl --fail --location --silent --show-error --retry 3 "$runtime_url" -o "$work/runtime.tar.gz"
          printf '%s  %s\n' "$runtime_sha" "$work/runtime.tar.gz" | sha512sum --check >/dev/null
          mkdir "$work/dotnet"
          tar -xzf "$work/runtime.tar.gz" -C "$work/dotnet"
          "$work/dotnet/dotnet" --list-runtimes | grep -q '^Microsoft.NETCore.App __RUNTIME_VERSION__ '
          rm -rf /opt/proxyharbor-checker/dotnet.previous
          if [ -d "$runtime_root" ]; then
            runtime_existed=1
            mv "$runtime_root" /opt/proxyharbor-checker/dotnet.previous
          fi
          mv "$work/dotnet" "$runtime_root"
          runtime_replaced=1
        fi

        chown proxyharbor-checker:proxyharbor-checker /opt/proxyharbor-checker/agent_token.next
        chmod 0400 /opt/proxyharbor-checker/agent_token.next
        printf '%s' '__SYSTEMD_UNIT_BASE64__' | base64 -d > "$work/proxyharbor-checker-agent.service"
        chmod 0644 "$work/proxyharbor-checker-agent.service"
        rm -rf /opt/proxyharbor-checker/app.previous
        app_existed=0
        if [ -d /opt/proxyharbor-checker/app ]; then
          app_existed=1
          mv /opt/proxyharbor-checker/app /opt/proxyharbor-checker/app.previous
        fi
        mv "$work/app" /opt/proxyharbor-checker/app
        chown -R root:root /opt/proxyharbor-checker/app /opt/proxyharbor-checker/dotnet
        token_existed=0
        if [ -f /opt/proxyharbor-checker/agent_token ]; then
          token_existed=1
          cp -p /opt/proxyharbor-checker/agent_token /opt/proxyharbor-checker/agent_token.previous
        fi
        mv /opt/proxyharbor-checker/agent_token.next /opt/proxyharbor-checker/agent_token
        install -m 0644 "$work/proxyharbor-checker-agent.service" /etc/systemd/system/proxyharbor-checker-agent.service
        systemctl daemon-reload
        systemctl enable proxyharbor-checker-agent.service >/dev/null
        if ! systemctl restart proxyharbor-checker-agent.service || ! systemctl is-active --quiet proxyharbor-checker-agent.service; then
          systemctl stop proxyharbor-checker-agent.service >/dev/null 2>&1 || true
          if [ -d /opt/proxyharbor-checker/app.previous ]; then
            rm -rf /opt/proxyharbor-checker/app
            mv /opt/proxyharbor-checker/app.previous /opt/proxyharbor-checker/app
          elif [ "$app_existed" -eq 0 ]; then
            rm -rf /opt/proxyharbor-checker/app
          fi
          if [ "$runtime_replaced" -eq 1 ]; then
            rm -rf "$runtime_root"
            if [ "$runtime_existed" -eq 1 ] && [ -d /opt/proxyharbor-checker/dotnet.previous ]; then
              mv /opt/proxyharbor-checker/dotnet.previous "$runtime_root"
            fi
          fi
          if [ -f /opt/proxyharbor-checker/agent_token.previous ]; then
            mv /opt/proxyharbor-checker/agent_token.previous /opt/proxyharbor-checker/agent_token
            systemctl start proxyharbor-checker-agent.service >/dev/null 2>&1 || true
          elif [ "$token_existed" -eq 0 ]; then
            rm -f /opt/proxyharbor-checker/agent_token
          fi
          journalctl -u proxyharbor-checker-agent.service -n 30 --no-pager >&2 || true
          exit 46
        fi
        rm -f /opt/proxyharbor-checker/agent_token.previous
        rm -rf /opt/proxyharbor-checker/app.previous /opt/proxyharbor-checker/dotnet.previous
        pid=$(systemctl show proxyharbor-checker-agent.service --property MainPID --value)
        printf 'systemd:%s\n' "$pid"
        """;
        return script
            .Replace("__TOKEN_BASE64__", tokenBase64, StringComparison.Ordinal)
            .Replace("__IMAGE__", settings.Image, StringComparison.Ordinal)
            .Replace("__CONTROL_ORIGIN__", control.GetLeftPart(UriPartial.Authority), StringComparison.Ordinal)
            .Replace("__NODE_ID__", nodeId.ToString("D"), StringComparison.Ordinal)
            .Replace("__MINIMUM_FREE_MB__", settings.NativeMinimumFreeMegabytes.ToString(
                System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("__RUNTIME_X64_SHA512__", settings.NativeRuntimeLinuxX64Sha512, StringComparison.Ordinal)
            .Replace("__RUNTIME_ARM64_SHA512__", settings.NativeRuntimeLinuxArm64Sha512, StringComparison.Ordinal)
            .Replace("__ASSET_BASE_URL__", settings.NativeAssetBaseUrl.TrimEnd('/'), StringComparison.Ordinal)
            .Replace("__RUNTIME_VERSION__", settings.NativeRuntimeVersion, StringComparison.Ordinal)
            .Replace("__SYSTEMD_UNIT_BASE64__", unit, StringComparison.Ordinal);
    }

    private static string BuildSystemdUnit(Guid nodeId, Uri control) => $"""
        [Unit]
        Description=ProxyHarbor distributed proxy checker
        Documentation=https://github.com/Xsenus/ProxyHarbor
        Wants=network-online.target
        After=network-online.target

        [Service]
        Type=simple
        User=proxyharbor-checker
        Group=proxyharbor-checker
        WorkingDirectory=/opt/proxyharbor-checker/app
        ExecStart=/opt/proxyharbor-checker/dotnet/dotnet /opt/proxyharbor-checker/app/ProxyHarbor.CheckerAgent.dll
        Restart=always
        RestartSec=5s
        TimeoutStopSec=30s
        Environment=DOTNET_EnableDiagnostics=0
        Environment=DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=true
        Environment=CheckerAgent__ControlPlaneBaseUrl={control.GetLeftPart(UriPartial.Authority)}
        Environment=CheckerAgent__NodeId={nodeId:D}
        Environment=CheckerAgent__TokenFile=/opt/proxyharbor-checker/agent_token
        NoNewPrivileges=true
        PrivateTmp=true
        ProtectSystem=strict
        ProtectHome=true
        ProtectKernelTunables=true
        ProtectKernelModules=true
        ProtectControlGroups=true
        ProtectClock=true
        ProtectHostname=true
        RestrictSUIDSGID=true
        LockPersonality=true
        CapabilityBoundingSet=
        AmbientCapabilities=
        UMask=0077
        LimitNOFILE=8192
        TasksMax=512
        MemoryMax=1536M

        [Install]
        WantedBy=multi-user.target
        """;

    internal static void ValidateDeploymentOptions(CheckerAgentDeploymentOptions settings)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(settings.Image,
            @"^[a-z0-9][a-z0-9._/-]{2,200}:[A-Za-z0-9._-]{1,80}$"))
            throw new InvalidOperationException("CheckerAgentDeployment:Image содержит небезопасное имя.");
        if (!Uri.TryCreate(settings.NativeAssetBaseUrl, UriKind.Absolute, out var assets) ||
            assets.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(assets.UserInfo) ||
            !string.IsNullOrEmpty(assets.Query) || !string.IsNullOrEmpty(assets.Fragment) ||
            !System.Text.RegularExpressions.Regex.IsMatch(assets.AbsolutePath, @"^/[A-Za-z0-9._~/-]*$"))
            throw new InvalidOperationException("CheckerAgentDeployment:NativeAssetBaseUrl должен быть безопасным HTTPS URL.");
        if (!System.Text.RegularExpressions.Regex.IsMatch(settings.NativeRuntimeVersion, @"^10\.0\.[0-9]{1,3}$"))
            throw new InvalidOperationException("CheckerAgentDeployment:NativeRuntimeVersion должен быть версией .NET 10 runtime.");
        if (!IsSha512(settings.NativeRuntimeLinuxX64Sha512) || !IsSha512(settings.NativeRuntimeLinuxArm64Sha512))
            throw new InvalidOperationException("CheckerAgentDeployment: SHA-512 runtime должен содержать 128 hex-символов.");
        if (settings.NativeMinimumFreeMegabytes is < 200 or > 10_240)
            throw new InvalidOperationException("CheckerAgentDeployment:NativeMinimumFreeMegabytes должен быть 200..10240.");
    }

    private static bool IsSha512(string value) => value.Length == 128 && value.All(Uri.IsHexDigit);

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
