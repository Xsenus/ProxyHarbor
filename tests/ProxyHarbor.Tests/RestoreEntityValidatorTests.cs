using ProxyHarbor.Domain;

namespace ProxyHarbor.Tests;

/// <summary>Фиксирует семантический trust boundary потокового restore до PostgreSQL COPY row.</summary>
public sealed class RestoreEntityValidatorTests
{
    [Fact]
    public void AcceptsRepresentativeRowsProducedByCurrentService()
    {
        RestoreEntityValidator.ValidateProxy(ValidProxy());
        RestoreEntityValidator.ValidateSource(ValidSource());
        RestoreEntityValidator.ValidateVpnSource(ValidVpnSource());
        RestoreEntityValidator.ValidateCollectionRun(new CollectionRun
        {
            FinishedAt = DateTimeOffset.UtcNow,
            Status = "completed",
            Error = "System.Exception: failure\n   at worker",
            SourcesProcessed = 2,
            SourcesSucceeded = 1,
            SourcesFailed = 1,
            SourcesTruncated = 1,
            CandidatesFound = 10,
            NewProxies = 3
        });
        RestoreEntityValidator.ValidateCollectionRun(new CollectionRun
        {
            FinishedAt = DateTimeOffset.UtcNow,
            Status = "cancelled",
            Error = "System.OperationCanceledException: host stopping"
        });
        RestoreEntityValidator.ValidateValidationRun(new ValidationRun
        {
            LeaseId = Guid.NewGuid(),
            FinishedAt = DateTimeOffset.UtcNow,
            Status = "completed",
            Error = "System.Exception: failure\n   at validator",
            Claimed = 10,
            Checked = 8,
            Alive = 3,
            Deferred = 2
        });
        RestoreEntityValidator.ValidateBackupRun(new BackupRun
        {
            FinishedAt = DateTimeOffset.UtcNow,
            Status = "completed",
            FileName = "proxyharbor.phbackup",
            SizeBytes = 1024,
            TelegramConfigured = true,
            SentToTelegram = true,
            Error = "System.Exception: failure\n   at backup"
        });
    }

    [Fact]
    public void ValidLegacyLeaseIsDiscardedBecauseOperationalOwnershipIsNotRestorable()
    {
        var proxy = ValidProxy();
        proxy.CheckLeaseId = Guid.NewGuid();
        proxy.CheckLeaseUntil = DateTimeOffset.UtcNow.AddMinutes(5);

        RestoreEntityValidator.ValidateProxy(proxy);

        Assert.Null(proxy.CheckLeaseId);
        Assert.Null(proxy.CheckLeaseUntil);
    }

    [Theory]
    [InlineData("127.0.0.1", 8080, 0, 0)]
    [InlineData("8.8.8.8", 0, 0, 0)]
    [InlineData("8.8.8.8", 8080, 999, 0)]
    [InlineData("8.8.8.8", 8080, 0, 999)]
    public void RejectsInvalidProxyIdentity(string host, int port, int protocol, int status)
    {
        var proxy = ValidProxy();
        proxy.Host = host;
        proxy.Port = port;
        proxy.Protocol = (ProxyProtocol)protocol;
        proxy.Status = (ProxyStatus)status;

        Assert.Throws<InvalidDataException>(() => RestoreEntityValidator.ValidateProxy(proxy));
    }

    [Theory]
    [InlineData("http://8.8.8.8/feed.txt", 100, 0)]
    [InlineData("https://user@8.8.8.8/feed.txt", 100, 0)]
    [InlineData("https://8.8.8.8/feed.txt", 10_001, 0)]
    [InlineData("https://8.8.8.8/feed.txt", 100, 999)]
    public void RejectsInvalidSourceMetadata(string url, int priority, int protocol)
    {
        var source = ValidSource();
        source.Url = url;
        source.Priority = priority;
        source.DefaultProtocol = (ProxyProtocol)protocol;

        Assert.Throws<InvalidDataException>(() => RestoreEntityValidator.ValidateSource(source));
    }

    [Fact]
    public void RejectsContentFetchOutsideSuccessfulSourceTimeline()
    {
        var source = ValidSource();
        source.LastFetchedAt = DateTimeOffset.UtcNow.AddHours(-2);
        source.LastSucceededAt = source.LastFetchedAt;
        source.LastContentFetchedAt = source.LastFetchedAt.Value.AddMinutes(1);

        Assert.Throws<InvalidDataException>(() => RestoreEntityValidator.ValidateSource(source));
    }

    [Fact]
    public void RejectsInvalidVpnConditionalFetchState()
    {
        var source = ValidVpnSource();
        source.HttpETag = "not an etag";

        Assert.Throws<InvalidDataException>(() => RestoreEntityValidator.ValidateVpnSource(source));

        source = ValidVpnSource();
        source.LastContentFetchedAt = source.LastFetchedAt!.Value.AddMinutes(1);
        Assert.Throws<InvalidDataException>(() => RestoreEntityValidator.ValidateVpnSource(source));
    }

    [Fact]
    public void RejectsInconsistentCollectionCounters()
    {
        var run = new CollectionRun
        {
            FinishedAt = DateTimeOffset.UtcNow,
            Status = "completed",
            SourcesProcessed = 2,
            SourcesSucceeded = 2,
            SourcesFailed = 1
        };

        Assert.Throws<InvalidDataException>(() => RestoreEntityValidator.ValidateCollectionRun(run));
    }

    [Fact]
    public void CancellationStatusIsExclusiveToCollectionRuns()
    {
        var finishedAt = DateTimeOffset.UtcNow;
        RestoreEntityValidator.ValidateCollectionRun(new CollectionRun
        {
            StartedAt = finishedAt.AddSeconds(-1),
            FinishedAt = finishedAt,
            Status = "cancelled"
        });

        Assert.Throws<InvalidDataException>(() => RestoreEntityValidator.ValidateValidationRun(new ValidationRun
        {
            LeaseId = Guid.NewGuid(),
            StartedAt = finishedAt.AddSeconds(-1),
            FinishedAt = finishedAt,
            Status = "cancelled"
        }));
        Assert.Throws<InvalidDataException>(() => RestoreEntityValidator.ValidateBackupRun(new BackupRun
        {
            StartedAt = finishedAt.AddSeconds(-1),
            FinishedAt = finishedAt,
            Status = "cancelled"
        }));
    }

    [Fact]
    public void RejectsRunThatFinishesBeforeItStarts()
    {
        var run = new CollectionRun
        {
            StartedAt = DateTimeOffset.UtcNow,
            FinishedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            Status = "completed"
        };

        Assert.Throws<InvalidDataException>(() => RestoreEntityValidator.ValidateCollectionRun(run));
    }

    [Fact]
    public void RejectsProxyCountersThatOverflowSuccessRate()
    {
        var proxy = ValidProxy();
        proxy.SuccessfulChecks = int.MaxValue;
        proxy.FailedChecks = 1;

        Assert.Throws<InvalidDataException>(() => RestoreEntityValidator.ValidateProxy(proxy));
    }

    [Fact]
    public void RejectsAliveProxyWithoutPublishedEvidence()
    {
        var proxy = ValidProxy();
        proxy.LatencyMs = null;

        Assert.Throws<InvalidDataException>(() => RestoreEntityValidator.ValidateProxy(proxy));
    }

    [Fact]
    public void RejectsDeadProxyWithoutFailedCheckEvidence()
    {
        var proxy = ValidProxy();
        proxy.Status = ProxyStatus.Dead;
        proxy.SuccessfulChecks = 0;
        proxy.FailedChecks = 0;

        Assert.Throws<InvalidDataException>(() => RestoreEntityValidator.ValidateProxy(proxy));
    }

    [Fact]
    public void RejectsValidationCountsBeyondClaimedBatch()
    {
        var run = new ValidationRun
        {
            LeaseId = Guid.NewGuid(),
            FinishedAt = DateTimeOffset.UtcNow,
            Status = "completed",
            Claimed = 10,
            Checked = 9,
            Deferred = 2
        };

        Assert.Throws<InvalidDataException>(() => RestoreEntityValidator.ValidateValidationRun(run));
    }

    [Theory]
    [InlineData("../backup.phbackup", true, true)]
    [InlineData("backup.phbackup", false, true)]
    [InlineData("backup.phbackup", true, false)]
    public void RejectsUnsafeOrInconsistentBackupAudit(
        string fileName,
        bool telegramConfigured,
        bool sentToTelegram)
    {
        var run = new BackupRun
        {
            FinishedAt = DateTimeOffset.UtcNow,
            Status = "completed",
            FileName = fileName,
            SizeBytes = 1,
            TelegramConfigured = telegramConfigured,
            SentToTelegram = sentToTelegram
        };

        Assert.Throws<InvalidDataException>(() => RestoreEntityValidator.ValidateBackupRun(run));
    }

    private static ProxyEndpoint ValidProxy()
    {
        var now = DateTimeOffset.UtcNow;
        return new ProxyEndpoint
        {
            Host = "8.8.8.8",
            Port = 8080,
            Protocol = ProxyProtocol.Http,
            Status = ProxyStatus.Alive,
            LatencyMs = 120,
            FirstSeenAt = now.AddHours(-1),
            LastSeenAt = now,
            LastCheckedAt = now,
            SuccessfulChecks = 2,
            FailedChecks = 1,
            ConsecutiveFailedChecks = 1,
            LastError = "System.Exception: failure\n   at probe"
        };
    }

    private static ProxySource ValidSource() => new()
    {
        Name = "Valid source",
        Url = "https://8.8.8.8/feed.txt",
        DefaultProtocol = ProxyProtocol.Http,
        Priority = 100,
        HttpETag = "W/\"valid-v1\"",
        HttpLastModifiedAt = DateTimeOffset.UtcNow.AddHours(-1),
        LastError = "System.Exception: failure\n   at collector"
    };

    private static VpnSource ValidVpnSource()
    {
        var now = DateTimeOffset.UtcNow;
        return new VpnSource
        {
            Name = "Valid VPN source",
            Provider = "Integration provider",
            Url = "https://8.8.4.4/vpn.txt",
            DefaultProtocol = VpnProtocol.Vless,
            Priority = 100,
            License = "MIT",
            LastFetchedAt = now,
            LastSucceededAt = now.AddMinutes(-1),
            LastContentFetchedAt = now.AddMinutes(-2),
            HttpETag = "W/\"vpn-valid-v1\"",
            HttpLastModifiedAt = now.AddHours(-1),
            LastItemCount = 4
        };
    }
}
