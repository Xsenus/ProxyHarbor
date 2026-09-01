import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { AnalyticsIntegrations } from "./components/AnalyticsIntegrations";
import { PrivacyControls } from "./components/PrivacyControls";
import { SiteSettingsProvider } from "./siteSettings";
import { defaultSiteSettings, type SiteSettings } from "./siteSettingsModel";

function response(settings: SiteSettings) {
  return new Response(JSON.stringify(settings), {
    status: 200,
    headers: { "Content-Type": "application/json" },
  });
}

function configured(revision = 1): SiteSettings {
  const settings = structuredClone(defaultSiteSettings);
  settings.cookieConsentRevision = revision;
  settings.analytics.yandex = { enabled: true, identifier: "12345678" };
  settings.analytics.google = { enabled: true, identifier: "G-TEST1234" };
  settings.analytics.vk = { enabled: true, identifier: "VK-RTRG-test" };
  return settings;
}

describe("runtime site settings", () => {
  beforeEach(() => {
    localStorage.clear();
    vi.stubGlobal("fetch", vi.fn(async () => response(configured())));
  });
  afterEach(() => {
    cleanup();
    document.querySelectorAll('script[id^="proxyharbor-"]').forEach((node) => node.remove());
    vi.restoreAllMocks();
    vi.unstubAllGlobals();
  });

  it("blocks a first visit until an explicit cookie choice and loads no tracker before consent", async () => {
    render(<SiteSettingsProvider><PrivacyControls /><AnalyticsIntegrations /></SiteSettingsProvider>);

    const dialog = await screen.findByRole("dialog", { name: "Настройки конфиденциальности" });
    expect(dialog).toHaveAttribute("aria-modal", "true");
    expect(screen.queryByRole("button", { name: "Закрыть" })).not.toBeInTheDocument();
    expect(document.getElementById("proxyharbor-google-analytics")).toBeNull();
    expect(document.getElementById("proxyharbor-yandex-metrica")).toBeNull();
    expect(document.getElementById("proxyharbor-vk-pixel")).toBeNull();

    fireEvent.click(screen.getByRole("button", { name: "Разрешить статистику" }));
    await waitFor(() => expect(document.getElementById("proxyharbor-google-analytics")).not.toBeNull());
    expect(document.getElementById("proxyharbor-yandex-metrica")).not.toBeNull();
    expect(document.getElementById("proxyharbor-vk-pixel")).not.toBeNull();
    expect(localStorage.getItem("proxyharbor.analytics-consent.v1")).toBe("accepted");
  });

  it("asks again when the administrator changes the consent revision", async () => {
    localStorage.setItem("proxyharbor.analytics-consent.v1", "accepted");
    vi.mocked(fetch).mockImplementation(async () => response(configured(2)));

    render(<SiteSettingsProvider><PrivacyControls /></SiteSettingsProvider>);

    expect(await screen.findByRole("dialog", { name: "Настройки конфиденциальности" })).toBeVisible();
    expect(localStorage.getItem("proxyharbor.analytics-consent.v2")).toBeNull();
  });

  it("keeps third-party scripts disabled after choosing necessary cookies only", async () => {
    render(<SiteSettingsProvider><PrivacyControls /><AnalyticsIntegrations /></SiteSettingsProvider>);
    fireEvent.click(await screen.findByRole("button", { name: "Только необходимые" }));

    await waitFor(() => expect(localStorage.getItem("proxyharbor.analytics-consent.v1")).toBe("rejected"));
    expect(document.getElementById("proxyharbor-google-analytics")).toBeNull();
    expect(document.getElementById("proxyharbor-yandex-metrica")).toBeNull();
    expect(document.getElementById("proxyharbor-vk-pixel")).toBeNull();
  });
});
