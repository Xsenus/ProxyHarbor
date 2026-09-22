using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Api;

/// <summary>Консервативные capabilities существующей Telegram multipart-доставки.</summary>
public sealed class TelegramBackupDestinationAdapter : IBackupDestinationAdapter
{
    private const int MaximumParts = 20;
    private const long MaximumPartBytes = 49L * 1024 * 1024;

    /// <inheritdoc />
    public string Kind => "telegram";
    /// <inheritdoc />
    public BackupDestinationCapabilities Capabilities { get; } = new(
        Put: new(true, MaximumPartBytes * MaximumParts, MaximumParts),
        Verify: new(false),
        Materialize: new(false),
        SupportsConditionalCreate: false,
        ProvidesNativeVersion: false,
        ProvidesNativeChecksum: false);
}
