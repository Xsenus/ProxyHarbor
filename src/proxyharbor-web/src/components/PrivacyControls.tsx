import { Cookie, Settings2, X } from "lucide-react";
import { useEffect, useRef, useState } from "react";
import { publicInfoPaths } from "../publicInfoRoutes";
import {
  openPrivacyPreferences,
  privacyPreferenceChanged,
  privacySignalEnabled,
  readAnalyticsChoice,
  writeAnalyticsChoice,
} from "../privacyPreferences";
import { useSiteSettings } from "../siteSettingsContext";
import type { SiteSettings } from "../siteSettingsModel";

export function PrivacyControls() {
  const { settings, loading, analyticsReady } = useSiteSettings();
  if (loading) return null;
  return <PrivacyDialog key={settings.cookieConsentRevision} settings={settings} analyticsReady={analyticsReady} />;
}

function PrivacyDialog({ settings, analyticsReady }: { settings: SiteSettings; analyticsReady: boolean }) {
  const revision = settings.cookieConsentRevision;
  // Public descriptions and legal documents must be readable before consent.
  // Reading them is not a privacy choice and never enables analytics.
  const readingDocument = Object.hasOwn(publicInfoPaths, window.location.pathname.replace(/\/+$/, "") || "/");
  const [choice, setChoice] = useState(() => readAnalyticsChoice(revision));
  const [preferencesOpen, setPreferencesOpen] = useState(false);
  const dialogRef = useRef<HTMLElement>(null);
  const required = choice === null && !readingDocument;
  const open = required || preferencesOpen;
  const privacySignal = privacySignalEnabled();

  useEffect(() => {
    const refresh = () => setChoice(readAnalyticsChoice(revision));
    const show = () => setPreferencesOpen(true);
    window.addEventListener(privacyPreferenceChanged, refresh);
    window.addEventListener(openPrivacyPreferences, show);
    window.addEventListener("storage", refresh);
    return () => {
      window.removeEventListener(privacyPreferenceChanged, refresh);
      window.removeEventListener(openPrivacyPreferences, show);
      window.removeEventListener("storage", refresh);
    };
  }, [revision]);

  useEffect(() => {
    const dialog = dialogRef.current;
    if (!open || !dialog) return;
    const previousFocus = document.activeElement instanceof HTMLElement ? document.activeElement : null;
    dialog.focus();
    const onKeyDown = (event: KeyboardEvent) => {
      if (event.key === "Escape" && !required) {
        event.preventDefault();
        setPreferencesOpen(false);
      }
      if (event.key !== "Tab" || !required) return;
      const controls = Array.from(dialog.querySelectorAll<HTMLElement>('a[href], button:not(:disabled)'));
      const first = controls[0];
      const last = controls.at(-1);
      if (event.shiftKey && (document.activeElement === first || document.activeElement === dialog)) {
        event.preventDefault();
        (last ?? dialog).focus();
      } else if (!event.shiftKey && (document.activeElement === last || document.activeElement === dialog)) {
        event.preventDefault();
        (first ?? dialog).focus();
      }
    };
    dialog.addEventListener("keydown", onKeyDown);
    return () => {
      dialog.removeEventListener("keydown", onKeyDown);
      if (previousFocus?.isConnected) previousFocus.focus();
    };
  }, [open, required]);

  const choose = (value: "accepted" | "rejected") => {
    const effective = privacySignal ? "rejected" : value;
    writeAnalyticsChoice(effective, revision);
    setChoice(effective);
    setPreferencesOpen(false);
  };

  const enabledAnalytics = [
    settings.analytics.firstPartyEnabled && "собственная статистика ProxyHarbor",
    settings.analytics.yandex.enabled && "Яндекс Метрика",
    settings.analytics.google.enabled && "Google Analytics",
    settings.analytics.vk.enabled && "VK Pixel",
  ].filter(Boolean) as string[];

  return (
    <>
      {settings.cookies.showSettingsButton && (choice !== null || readingDocument) && <button
        className="privacy-settings-button"
        type="button"
        onClick={() => setPreferencesOpen(true)}
        aria-label="Настройки cookies"
      >
        <Settings2 /> Cookies
      </button>}
      {open && (
        <div className={`privacy-consent-layer ${required ? "required" : ""}`}>
          <section
            ref={dialogRef}
            tabIndex={-1}
            className="privacy-consent-banner"
            role="dialog"
            aria-modal={required}
            aria-labelledby="privacy-consent-title"
          >
          {!required && <button
            className="privacy-consent-close"
            type="button"
            aria-label="Закрыть"
            onClick={() => setPreferencesOpen(false)}
          >
            <X />
          </button>}
          <Cookie />
          <div>
            <h2 id="privacy-consent-title">{settings.cookies.bannerTitle}</h2>
            <p>{settings.cookies.bannerText}</p>
            {enabledAnalytics.length > 0 && (
              <p className="privacy-consent-providers">
                По разрешению: {enabledAnalytics.join(", ")}.
              </p>
            )}
            <a href="/cookies">Подробнее о cookies</a>
            {required && <small>Для продолжения выберите один из вариантов.</small>}
            {privacySignal && (
              <small>Браузер передал GPC/DNT — необязательная статистика будет отключена.</small>
            )}
            {!privacySignal && !analyticsReady && (
              <small>Настройки статистики недоступны. Вы можете продолжить с необходимыми cookies.</small>
            )}
            {!privacySignal && analyticsReady && enabledAnalytics.length === 0 && (
              <small>На сайте используются только необходимые cookies.</small>
            )}
            {choice === "rejected" && (
              <small>Необязательная статистика отключена.</small>
            )}
          </div>
          <div className="privacy-consent-actions">
            <button type="button" onClick={() => choose("rejected")}>
              Только необходимые
            </button>
            <button
              className="primary"
              type="button"
              onClick={() => choose("accepted")}
              disabled={!analyticsReady || privacySignal || enabledAnalytics.length === 0}
            >
              Разрешить статистику
            </button>
          </div>
          </section>
        </div>
      )}
    </>
  );
}
