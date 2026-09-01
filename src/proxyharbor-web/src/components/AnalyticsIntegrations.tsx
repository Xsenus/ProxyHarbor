import { useEffect } from "react";
import {
  analyticsAllowed,
  privacyPreferenceChanged,
} from "../privacyPreferences";
import { useSiteSettings } from "../siteSettingsContext";

type Gtag = (...args: unknown[]) => void;
type Ym = ((...args: unknown[]) => void) & { a?: unknown[][]; l?: number };
type AnalyticsWindow = Window & {
  dataLayer?: unknown[];
  gtag?: Gtag;
  ym?: Ym;
  VK?: {
    Retargeting?: {
      Init: (identifier: string) => void;
      Hit: () => void;
    };
  };
};

function installScript(id: string, source: string, onLoad?: () => void) {
  const existing = document.getElementById(id) as HTMLScriptElement | null;
  if (existing) {
    if (existing.dataset.loaded === "true") onLoad?.();
    return;
  }
  const script = document.createElement("script");
  script.id = id;
  script.async = true;
  script.src = source;
  script.addEventListener("load", () => {
    script.dataset.loaded = "true";
    onLoad?.();
  });
  document.head.append(script);
}

function removeScript(id: string) {
  document.getElementById(id)?.remove();
}

export function AnalyticsIntegrations() {
  const { settings, loading } = useSiteSettings();
  const revision = settings.cookieConsentRevision;

  useEffect(() => {
    if (loading) return;
    const browser = window as AnalyticsWindow;

    const synchronize = () => {
      const allowed = analyticsAllowed(revision);
      const yandex = settings.analytics.yandex;
      const google = settings.analytics.google;
      const vk = settings.analytics.vk;

      if (!allowed || !google.enabled) {
        if (google.identifier)
          (window as unknown as Record<string, unknown>)[`ga-disable-${google.identifier}`] = true;
        removeScript("proxyharbor-google-analytics");
      } else {
        (window as unknown as Record<string, unknown>)[`ga-disable-${google.identifier}`] = false;
        browser.dataLayer ??= [];
        browser.gtag ??= (...args: unknown[]) => browser.dataLayer?.push(args);
        browser.gtag("consent", "update", {
          analytics_storage: "granted",
          ad_storage: "denied",
          ad_user_data: "denied",
          ad_personalization: "denied",
        });
        browser.gtag("js", new Date());
        browser.gtag("config", google.identifier, {
          anonymize_ip: true,
          allow_google_signals: false,
          allow_ad_personalization_signals: false,
        });
        installScript(
          "proxyharbor-google-analytics",
          `https://www.googletagmanager.com/gtag/js?id=${encodeURIComponent(google.identifier)}`,
        );
      }

      if (!allowed || !yandex.enabled) {
        if (yandex.identifier)
          (window as unknown as Record<string, unknown>)[`disableYaCounter${yandex.identifier}`] = true;
        removeScript("proxyharbor-yandex-metrica");
      } else {
        (window as unknown as Record<string, unknown>)[`disableYaCounter${yandex.identifier}`] = false;
        if (!browser.ym) {
          const queue: Ym = (...args: unknown[]) => {
            queue.a ??= [];
            queue.a.push(args);
          };
          queue.l = Date.now();
          browser.ym = queue;
        }
        browser.ym(Number(yandex.identifier), "init", {
          clickmap: false,
          trackLinks: true,
          accurateTrackBounce: true,
          webvisor: false,
          sendTitle: false,
        });
        installScript(
          "proxyharbor-yandex-metrica",
          "https://mc.yandex.ru/metrika/tag.js",
        );
      }

      const startVk = () => {
        if (!allowed || !vk.enabled) return;
        browser.VK?.Retargeting?.Init(vk.identifier);
        browser.VK?.Retargeting?.Hit();
      };
      if (!allowed || !vk.enabled) {
        removeScript("proxyharbor-vk-pixel");
      } else {
        installScript(
          "proxyharbor-vk-pixel",
          "https://vk.com/js/api/openapi.js?169",
          startVk,
        );
      }
    };

    synchronize();
    window.addEventListener(privacyPreferenceChanged, synchronize);
    return () => window.removeEventListener(privacyPreferenceChanged, synchronize);
  }, [loading, revision, settings.analytics]);

  return null;
}
