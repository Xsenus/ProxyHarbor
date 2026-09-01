using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ProxyHarbor.Api;
using ProxyHarbor.Api.Controllers;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Tests;

public sealed class SiteSettingsControllerTests
{
    [Fact]
    public async Task PublicSnapshotRedactsHiddenOptionalAndBankValues()
    {
        var settings = new SitePublicationSettings();
        settings.Requisites.Fields["phone"].Visible = false;
        settings.Requisites.Fields["phone"].Value = "+7 secret";
        settings.Requisites.Fields["bankAccount"].Value = "secret-bank-account";
        settings.Requisites.Fields["address"].Visible = false;
        var store = new Store(settings);

        var response = Assert.IsType<OkObjectResult>(
            await new SiteSettingsController(store).Get(default));
        var json = JsonSerializer.Serialize(response.Value);
        using var document = JsonDocument.Parse(json);
        var fields = document.RootElement.GetProperty("Requisites").GetProperty("Fields");

        Assert.DoesNotContain("+7 secret", json, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-bank-account", json, StringComparison.Ordinal);
        // The operator address remains available to the mandatory privacy/legal pages,
        // while its row stays hidden on the standalone requisites page.
        Assert.Contains("Новосибирская область", fields.GetProperty("address").GetProperty("Value").GetString(), StringComparison.Ordinal);
        Assert.False(fields.GetProperty("address").GetProperty("Visible").GetBoolean());
    }

    [Fact]
    public async Task AdminSnapshotKeepsUnpublishedValuesEditable()
    {
        var settings = new SitePublicationSettings();
        settings.Requisites.BankSectionVisible = false;
        settings.Requisites.Fields["bankAccount"].Value = "editable-bank-account";

        var response = Assert.IsType<OkObjectResult>(
            await new AdminSiteSettingsController(new Store(settings)).Get(default));

        Assert.Contains("editable-bank-account", JsonSerializer.Serialize(response.Value), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConsentContractChangeIncrementsRevisionAndPersists()
    {
        var current = new SitePublicationSettings { CookieConsentRevision = 7 };
        var store = new Store(current);
        var request = new SitePublicationSettings();
        request.Cookies.BannerText = "Новый понятный текст обязательного выбора.";
        request.Analytics.Yandex = new ExternalAnalyticsOptions
        {
            Enabled = true,
            Identifier = "12345678"
        };

        Assert.IsType<OkObjectResult>(
            await new AdminSiteSettingsController(store).Update(request, default));

        Assert.NotNull(store.Saved);
        Assert.Equal(8, store.Saved.CookieConsentRevision);
        Assert.Equal("12345678", store.Saved.Analytics.Yandex.Identifier);
    }

    [Fact]
    public async Task RejectsUnpublishingRequiredDocumentsAndArbitraryTrackerCode()
    {
        var controller = new AdminSiteSettingsController(new Store(new SitePublicationSettings()));
        var unpublished = new SitePublicationSettings();
        unpublished.Sections["privacy"].Published = false;
        Assert.IsType<BadRequestObjectResult>(await controller.Update(unpublished, default));

        var script = new SitePublicationSettings();
        script.Analytics.Google = new ExternalAnalyticsOptions
        {
            Enabled = true,
            Identifier = "<script>alert(1)</script>"
        };
        Assert.IsType<BadRequestObjectResult>(await controller.Update(script, default));
    }

    [Fact]
    public async Task RejectsIncompleteNullAndMalformedPublicationContracts()
    {
        var controller = new AdminSiteSettingsController(new Store(new SitePublicationSettings()));

        await Reject(new SitePublicationSettings { Sections = null! });
        var missingSection = new SitePublicationSettings();
        missingSection.Sections.Remove("pricing");
        await Reject(missingSection);
        var nullSection = new SitePublicationSettings();
        nullSection.Sections["pricing"] = null!;
        await Reject(nullSection);

        await Reject(new SitePublicationSettings { Requisites = null! });
        var nullFields = new SitePublicationSettings();
        nullFields.Requisites.Fields = null!;
        await Reject(nullFields);
        var missingField = new SitePublicationSettings();
        missingField.Requisites.Fields.Remove("phone");
        await Reject(missingField);
        var nullField = new SitePublicationSettings();
        nullField.Requisites.Fields["phone"] = null!;
        await Reject(nullField);
        var invalidTitle = new SitePublicationSettings();
        invalidTitle.Requisites.IntroTitle = "line\nbreak";
        await Reject(invalidTitle);
        var invalidDescription = new SitePublicationSettings();
        invalidDescription.Requisites.IntroDescription = "bad\0text";
        await Reject(invalidDescription);
        var invalidNote = new SitePublicationSettings();
        invalidNote.Requisites.Note = new string('x', 1_001);
        await Reject(invalidNote);
        var invalidFieldValue = new SitePublicationSettings();
        invalidFieldValue.Requisites.Fields["phone"].Value = new string('x', 501);
        await Reject(invalidFieldValue);

        await Reject(new SitePublicationSettings { Cookies = null! });
        var invalidCookieTitle = new SitePublicationSettings();
        invalidCookieTitle.Cookies.BannerTitle = string.Empty.PadLeft(121, 'x');
        await Reject(invalidCookieTitle);
        var invalidCookieText = new SitePublicationSettings();
        invalidCookieText.Cookies.BannerText = "bad\0text";
        await Reject(invalidCookieText);

        await Reject(new SitePublicationSettings { Analytics = null! });
        var nullYandex = new SitePublicationSettings();
        nullYandex.Analytics.Yandex = null!;
        await Reject(nullYandex);
        var nullGoogle = new SitePublicationSettings();
        nullGoogle.Analytics.Google = null!;
        await Reject(nullGoogle);
        var nullVk = new SitePublicationSettings();
        nullVk.Analytics.Vk = null!;
        await Reject(nullVk);
        var enabledWithoutIdentifier = new SitePublicationSettings();
        enabledWithoutIdentifier.Analytics.Yandex.Enabled = true;
        await Reject(enabledWithoutIdentifier);
        var longIdentifier = new SitePublicationSettings();
        longIdentifier.Analytics.Vk.Identifier = new string('x', 129);
        await Reject(longIdentifier);

        async Task Reject(SitePublicationSettings request) =>
            Assert.IsType<BadRequestObjectResult>(await controller.Update(request, default));
    }

    [Fact]
    public async Task UnchangedConsentContractPreservesRevisionAndNormalizesValues()
    {
        var current = new SitePublicationSettings { CookieConsentRevision = 9 };
        current.Analytics.Google.Identifier = "g-abcd1234";
        current.Analytics.Vk.Identifier = "12345";
        var store = new Store(current);
        var request = current;
        request.Requisites.IntroTitle = "  Исполнитель  ";

        Assert.IsType<OkObjectResult>(
            await new AdminSiteSettingsController(store).Update(request, default));

        Assert.NotNull(store.Saved);
        Assert.Equal(9, store.Saved.CookieConsentRevision);
        Assert.Equal("Исполнитель", store.Saved.Requisites.IntroTitle);
        Assert.Equal("G-ABCD1234", store.Saved.Analytics.Google.Identifier);
    }

    [Fact]
    public async Task DatabaseStoreCreatesUpdatesNormalizesAndReadsSingleton()
    {
        var options = new DbContextOptionsBuilder<ProxyHarborDbContext>()
            .UseInMemoryDatabase($"site-settings-{Guid.NewGuid():N}").Options;
        await using var db = new ProxyHarborDbContext(options);
        var store = new SiteConfigurationStore(db);

        var defaults = await store.GetAsync();
        Assert.Equal(1, defaults.CookieConsentRevision);

        var incomplete = new SitePublicationSettings
        {
            Sections = null!,
            Requisites = new PublicRequisitesOptions { Fields = null! },
            Cookies = null!,
            Analytics = new SiteAnalyticsOptions
            {
                Yandex = null!,
                Google = null!,
                Vk = null!
            },
            CookieConsentRevision = 0
        };
        await store.SaveAsync(incomplete);
        var saved = await store.GetAsync();
        Assert.Equal(1, saved.CookieConsentRevision);
        Assert.True(saved.Sections["privacy"].Published);
        Assert.Equal(RequisiteFieldCodes.All.Length, saved.Requisites.Fields.Count);

        saved.Cookies.BannerTitle = "Изменено";
        await store.SaveAsync(saved);
        Assert.Equal("Изменено", (await store.GetAsync()).Cookies.BannerTitle);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("null")]
    public async Task DatabaseStoreRejectsCorruptedSnapshots(string json)
    {
        var options = new DbContextOptionsBuilder<ProxyHarborDbContext>()
            .UseInMemoryDatabase($"site-settings-corrupt-{Guid.NewGuid():N}").Options;
        await using var db = new ProxyHarborDbContext(options);
        db.SiteConfigurations.Add(new SiteConfiguration
        {
            Id = 1,
            SettingsJson = json,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => new SiteConfigurationStore(db).GetAsync());
    }

    private sealed class Store(SitePublicationSettings current) : ISiteConfigurationStore
    {
        internal SitePublicationSettings? Saved { get; private set; }
        public Task<SitePublicationSettings> GetAsync(CancellationToken token = default) =>
            Task.FromResult(Saved ?? current);
        public Task SaveAsync(SitePublicationSettings settings, CancellationToken token = default)
        {
            Saved = settings;
            return Task.CompletedTask;
        }
    }
}
