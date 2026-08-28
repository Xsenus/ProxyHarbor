using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ProxyHarbor.Api;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Tests;

public sealed class TelegramBackupDeliveryResolverTests
{
    [Fact]
    public async Task ResolvesCurrentMainBotTokenAndActiveCrmChat()
    {
        await using var fixture = await Fixture.CreateAsync();

        var result = await fixture.Resolver.ResolveAsync(fixture.RecipientId);

        Assert.Equal(fixture.RecipientId, result.RecipientId);
        Assert.Equal("123456789:abcdefghijklmnopqrstuvwxyz", result.BotToken);
        Assert.Equal("812345", result.ChatId);
        Assert.Equal("Илья Телятников", result.DisplayName);
        Assert.Equal("Xsenus", result.Username);
    }

    [Theory]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public async Task RejectsMainBotThatIsNotReady(bool enabled, bool identity, bool validToken)
    {
        await using var fixture = await Fixture.CreateAsync(enabled, identity, validToken);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.Resolver.ResolveAsync(fixture.RecipientId));

        Assert.Contains("не включён", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RejectsMissingRecipient()
    {
        await using var fixture = await Fixture.CreateAsync();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.Resolver.ResolveAsync(Guid.NewGuid()));

        Assert.Contains("не существует", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RejectsBlockedRecipient()
    {
        await using var fixture = await Fixture.CreateAsync(blocked: true);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.Resolver.ResolveAsync(fixture.RecipientId));

        Assert.Contains("заблокирован", exception.Message, StringComparison.Ordinal);
    }

    private sealed class Fixture(ServiceProvider services, Guid recipientId) : IAsyncDisposable
    {
        internal Guid RecipientId { get; } = recipientId;
        internal TelegramBackupDeliveryResolver Resolver { get; } = new(
            services.GetRequiredService<IServiceScopeFactory>());

        internal static async Task<Fixture> CreateAsync(
            bool enabled = true,
            bool identity = true,
            bool validToken = true,
            bool blocked = false)
        {
            var database = $"telegram-backup-resolver-{Guid.NewGuid():N}";
            var dbOptions = new DbContextOptionsBuilder<ProxyHarborDbContext>()
                .UseInMemoryDatabase(database).Options;
            var dataProtection = DataProtectionProvider.Create(
                Path.Combine(Path.GetTempPath(), $"telegram-backup-protection-{Guid.NewGuid():N}"));
            var services = new ServiceCollection()
                .AddSingleton<IDataProtectionProvider>(dataProtection)
                .AddSingleton(Options.Create(new TelegramBotHostOptions
                {
                    PublicBaseUrl = "https://proxy.example"
                }))
                .AddScoped(_ => new ProxyHarborDbContext(dbOptions))
                .AddScoped<ITelegramBotConfigurationStore, TelegramBotConfigurationStore>()
                .BuildServiceProvider(validateScopes: true);

            var recipientId = Guid.NewGuid();
            await using var scope = services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<ProxyHarborDbContext>();
            db.TelegramChats.Add(new TelegramChat
            {
                Id = recipientId,
                ChatId = 812345,
                TelegramUserId = 812345,
                UserId = Guid.NewGuid(),
                DisplayName = "Илья Телятников",
                Username = "Xsenus",
                IsBlocked = blocked
            });
            await db.SaveChangesAsync();
            var store = scope.ServiceProvider.GetRequiredService<ITelegramBotConfigurationStore>();
            await store.SaveAsync(new TelegramBotOptions
            {
                Enabled = enabled,
                BotId = identity ? 42 : null,
                BotToken = validToken ? "123456789:abcdefghijklmnopqrstuvwxyz" : "bad",
                WebhookSecret = new string('s', 32),
                PublicBaseUrl = "https://proxy.example"
            });
            return new Fixture(services, recipientId);
        }

        public ValueTask DisposeAsync() => services.DisposeAsync();
    }
}
