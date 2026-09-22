using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using ProxyHarbor.Api;
using ProxyHarbor.Domain;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Tests;

public sealed class TelegramBackupDestinationAdapterTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task RecipientDeliveryUsesNativeMultipartTransportAndCleansParts()
    {
        var recipientId = Guid.NewGuid();
        var resolver = new StubResolver(recipientId);
        var transport = new RecordingTransport();
        var adapter = new TelegramBackupDestinationAdapter(
            resolver, transport, new EphemeralDataProtectionProvider());
        var destination = Destination(recipientId, maximumPartBytes: 2);
        var path = Path.Combine(Path.GetTempPath(), $"telegram-adapter-{Guid.NewGuid():N}.phbackup");
        await File.WriteAllBytesAsync(path, [1, 2, 3, 4, 5]);
        try
        {
            var result = await adapter.PutAsync(destination, path, CancellationToken.None);

            Assert.Equal(3, result.ConfirmedParts);
            Assert.Equal(3, transport.Paths.Count);
            Assert.All(transport.Paths, sent => Assert.False(File.Exists(sent)));
            Assert.Equal(["1/3", "2/3", "3/3"], transport.Captions.Select(PartNumber));
            Assert.Equal(recipientId, resolver.LastRecipientId);
        }
        finally
        {
            File.Delete(path);
            foreach (var part in Directory.EnumerateFiles(
                Path.GetDirectoryName(path)!, $"{Path.GetFileName(path)}.part*"))
                File.Delete(part);
        }
    }

    [Fact]
    public async Task FailureAfterConfirmedPartIsUnknownAndNeverReportsSuccess()
    {
        var transport = new RecordingTransport(failAttempt: 2);
        var adapter = new TelegramBackupDestinationAdapter(
            new StubResolver(Guid.NewGuid()), transport, new EphemeralDataProtectionProvider());
        var path = Path.Combine(Path.GetTempPath(), $"telegram-partial-{Guid.NewGuid():N}.phbackup");
        await File.WriteAllBytesAsync(path, [1, 2, 3]);
        try
        {
            var exception = await Assert.ThrowsAsync<BackupDestinationOperationException>(() =>
                adapter.PutAsync(Destination(Guid.NewGuid(), 1), path, CancellationToken.None));

            Assert.Equal(BackupDestinationErrorCode.UnknownOutcome, exception.Failure.Code);
            Assert.Equal(BackupDestinationFailureDisposition.UnknownOutcome, exception.Failure.Disposition);
            Assert.Empty(Directory.EnumerateFiles(
                Path.GetDirectoryName(path)!, $"{Path.GetFileName(path)}.part*"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task LegacyProtectedCredentialsRemainSupportedWithoutRecipientId()
    {
        var dataProtection = new EphemeralDataProtectionProvider();
        var protector = dataProtection.CreateProtector("ProxyHarbor.BackupDestination.Secrets.v1");
        var transport = new RecordingTransport();
        var adapter = new TelegramBackupDestinationAdapter(
            new StubResolver(Guid.NewGuid()), transport, dataProtection);
        var destination = Destination(null, 1024);
        destination.ProtectedSecrets = protector.Protect(JsonSerializer.Serialize(new
        {
            botToken = "123456789:test-token",
            chatId = "-1001234567890"
        }, Json));
        var path = Path.Combine(Path.GetTempPath(), $"telegram-legacy-{Guid.NewGuid():N}.phbackup");
        await File.WriteAllBytesAsync(path, [1, 2, 3]);
        try
        {
            var result = await adapter.PutAsync(destination, path, CancellationToken.None);

            Assert.Equal(1, result.ConfirmedParts);
            Assert.Equal("123456789:test-token", transport.BotTokens.Single());
            Assert.Equal("-1001234567890", transport.ChatIds.Single());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, BackupDestinationErrorCode.AuthenticationFailed,
        BackupDestinationFailureDisposition.Permanent)]
    [InlineData(HttpStatusCode.TooManyRequests, BackupDestinationErrorCode.RateLimited,
        BackupDestinationFailureDisposition.Retryable)]
    [InlineData(HttpStatusCode.BadGateway, BackupDestinationErrorCode.Unavailable,
        BackupDestinationFailureDisposition.Retryable)]
    public void ProviderErrorsHaveStableSafeClassification(
        HttpStatusCode status,
        BackupDestinationErrorCode expectedCode,
        BackupDestinationFailureDisposition expectedDisposition)
    {
        var failure = TelegramBackupDestinationAdapter.ClassifyProviderFailure(
            new HttpRequestException("unsafe provider detail", null, status));

        Assert.Equal(expectedCode, failure.Code);
        Assert.Equal(expectedDisposition, failure.Disposition);
    }

    private static BackupDestination Destination(Guid? recipientId, long maximumPartBytes) => new()
    {
        Id = Guid.NewGuid(),
        Name = "telegram",
        Kind = "telegram",
        Enabled = true,
        FailureDomain = "telegram",
        SettingsJson = JsonSerializer.Serialize(new { recipientId, maximumPartBytes }, Json)
    };

    private static string PartNumber(string caption) => caption[(caption.LastIndexOf(' ') + 1)..];

    private sealed class StubResolver(Guid expectedRecipientId) : ITelegramBackupDeliveryResolver
    {
        internal Guid? LastRecipientId { get; private set; }

        public Task<TelegramBackupDelivery> ResolveAsync(
            Guid recipientId,
            CancellationToken token = default)
        {
            LastRecipientId = recipientId;
            return Task.FromResult(new TelegramBackupDelivery(
                expectedRecipientId, "123456789:resolved-token", "-1009876543210", "test", null));
        }
    }

    private sealed class RecordingTransport(int? failAttempt = null) : ITelegramBackupTransport
    {
        internal List<string> Paths { get; } = [];
        internal List<string> Captions { get; } = [];
        internal List<string> BotTokens { get; } = [];
        internal List<string> ChatIds { get; } = [];

        public Task SendAsync(
            string path,
            string caption,
            string botToken,
            string chatId,
            CancellationToken token)
        {
            Paths.Add(path);
            Captions.Add(caption);
            BotTokens.Add(botToken);
            ChatIds.Add(chatId);
            if (Paths.Count == failAttempt)
                throw new HttpRequestException("unsafe URI with bot token");
            return Task.CompletedTask;
        }
    }
}
