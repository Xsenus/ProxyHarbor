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

function installScript(id: string, source: string, onLoad: () => void) {
  const script = document.createElement("script");
  script.id = id;
  script.async = true;
  script.src = source;
  script.addEventListener("load", onLoad, { once: true });
  document.head.append(script);
  return () => {
    script.removeEventListener("load", onLoad);
    script.remove();
  };
}

export function AnalyticsIntegrations() {
  const { settings, loading } = useSiteSettings();
  const revision = settings.cookieConsentRevision;

  useEffect(() => {
    if (loading) return;
    const browser = window as AnalyticsWindow;
    const flags = window as unknown as Record<string, unknown>;
    const { yandex, google, vk } = settings.analytics;
    let disposed = false;
    const running = new Map<string, () => void>();

    // Third-party SDK failures must not prevent other providers being stopped.
    const callSdk = (action: () => void) => {
      try { action(); } catch { console.warn('Optional analytics SDK operation failed.'); }
    };
    const manage = (key: string, enabled: boolean, source: string, start: () => void, stop: () => void) => {
      if (!enabled) {
        running.get(key)?.();
        running.delete(key);
        return;
      }
      if (running.has(key)) return;
      let cancelled = false;
      let started = false;
      const remove = installScript(`proxyharbor-${key}`, source, () => {
        // A detached script can finish loading after revocation or reconfiguration.
        if (cancelled || disposed || !analyticsAllowed(revision)) return;
        started = true;
        callSdk(start);
      });
      running.set(key, () => {
        cancelled = true;
        remove();
        if (started) callSdk(stop);
      });
    };

    const disableGoogle = () => {
      if (google.identifier) flags[`ga-disable-${google.identifier}`] = true;
    };
    const disableYandex = () => {
      if (yandex.identifier) flags[`disableYaCounter${yandex.identifier}`] = true;
    };

    const synchronize = () => {
      const allowed = !disposed && analyticsAllowed(revision);
      const googleEnabled = allowed && google.enabled && !!google.identifier;
      const yandexEnabled = allowed && yandex.enabled && !!yandex.identifier;
      const vkEnabled = allowed && vk.enabled && !!vk.identifier;
      if (!googleEnabled) disableGoogle();
      if (!yandexEnabled) disableYandex();
      // SDK bootstraps must exist before their script executes. Configuration
      // waits for load so a cancelled download cannot replay an old init queue.
      if (googleEnabled) {
        browser.dataLayer ??= [];
        browser.gtag ??= (...args: unknown[]) => browser.dataLayer?.push(args);
      }
      if (yandexEnabled && !browser.ym) {
        const queue: Ym = (...args: unknown[]) => {
          queue.a ??= [];
          queue.a.push(args);
        };
        queue.l = Date.now();
        browser.ym = queue;
      }

      manage('google-analytics', googleEnabled,
        `https://www.googletagmanager.com/gtag/js?id=${encodeURIComponent(google.identifier)}`, () => {
        flags[`ga-disable-${google.identifier}`] = false;
        browser.gtag?.("consent", "update", {
          analytics_storage: "granted",
          ad_storage: "denied",
          ad_user_data: "denied",
          ad_personalization: "denied",
        });
        browser.gtag?.("js", new Date());
        browser.gtag?.("config", google.identifier, {
          anonymize_ip: true,
          allow_google_signals: false,
          allow_ad_personalization_signals: false,
        });
      }, () => {
        disableGoogle();
        // https://developers.google.com/tag-platform/security/guides/privacy
        browser.gtag?.("consent", "update", {
          analytics_storage: "denied", ad_storage: "denied",
          ad_user_data: "denied", ad_personalization: "denied",
        });
      });

      manage('yandex-metrica', yandexEnabled, 'https://mc.yandex.ru/metrika/tag.js', () => {
        flags[`disableYaCounter${yandex.identifier}`] = false;
        browser.ym?.(Number(yandex.identifier), "init", {
          clickmap: false,
          trackLinks: true,
          accurateTrackBounce: true,
          webvisor: false,
          sendTitle: false,
        });
      }, () => {
        disableYandex();
        // https://yandex.ru/support/metrica/ru/code/counter-spa-setup
        browser.ym?.(Number(yandex.identifier), "destruct");
      });

      manage('vk-pixel', vkEnabled, 'https://vk.com/js/api/openapi.js?169', () => {
        browser.VK?.Retargeting?.Init(vk.identifier);
        browser.VK?.Retargeting?.Hit();
      }, () => { /* No recurring VK calls are scheduled by this integration. */ });
    };

    synchronize();
    window.addEventListener(privacyPreferenceChanged, synchronize);
    window.addEventListener('storage', synchronize);
    return () => {
      disposed = true;
      window.removeEventListener(privacyPreferenceChanged, synchronize);
      window.removeEventListener('storage', synchronize);
      disableGoogle();
      disableYandex();
      for (const stop of running.values()) stop();
      running.clear();
    };
  }, [loading, revision, settings.analytics]);

  return null;
}
