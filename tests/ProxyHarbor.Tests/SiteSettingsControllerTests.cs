using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using ProxyHarbor.Api;
using ProxyHarbor.Api.Controllers;

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
