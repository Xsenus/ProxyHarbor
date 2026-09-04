import { Cookie, Settings2, X } from "lucide-react";
import { useEffect, useState } from "react";
import {
  openPrivacyPreferences,
  privacyPreferenceChanged,
  privacySignalEnabled,
  readAnalyticsChoice,
  writeAnalyticsChoice,
} from "../privacyPreferences";
import { useSiteSettings } from "../siteSettingsContext";

export function PrivacyControls() {
  const { settings, loading, analyticsReady } = useSiteSettings();
  const revision = settings.cookieConsentRevision;
  const [choice, setChoice] = useState(() => readAnalyticsChoice(revision));
  const [open, setOpen] = useState(choice === null);
  const privacySignal = privacySignalEnabled();

  useEffect(() => {
    const refresh = () => setChoice(readAnalyticsChoice(revision));
    const show = () => setOpen(true);
    const synchronizeStorage = () => {
      const next = readAnalyticsChoice(revision);
      setChoice(next);
      // Another tab can complete the initial choice or clear consent entirely.
      // Keep explicitly opened settings visible when a known choice changes.
      if (next === null) setOpen(true);
      else if (choice === null) setOpen(false);
    };
    window.addEventListener(privacyPreferenceChanged, refresh);
    window.addEventListener(openPrivacyPreferences, show);
    window.addEventListener("storage", synchronizeStorage);
    return () => {
      window.removeEventListener(privacyPreferenceChanged, refresh);
      window.removeEventListener(openPrivacyPreferences, show);
      window.removeEventListener("storage", synchronizeStorage);
    };
  }, [revision, choice]);

  useEffect(() => {
    const timer = window.setTimeout(() => {
      const next = readAnalyticsChoice(revision);
      setChoice(next);
      if (next === null) setOpen(true);
    }, 0);
    return () => window.clearTimeout(timer);
  }, [revision]);

  const choose = (value: "accepted" | "rejected") => {
    const effective = privacySignal ? "rejected" : value;
    writeAnalyticsChoice(effective, revision);
    setChoice(effective);
    setOpen(false);
  };

  if (loading) return null;

  const enabledAnalytics = [
    settings.analytics.firstPartyEnabled && "собственная статистика ProxyHarbor",
    settings.analytics.yandex.enabled && "Яндекс Метрика",
    settings.analytics.google.enabled && "Google Analytics",
    settings.analytics.vk.enabled && "VK Pixel",
  ].filter(Boolean) as string[];

  return (
    <>
      {settings.cookies.showSettingsButton && choice !== null && <button
        className="privacy-settings-button"
        type="button"
        onClick={() => setOpen(true)}
        aria-label="Настройки cookies"
      >
        <Settings2 /> Cookies
      </button>}
      {open && (
        <div className={`privacy-consent-layer ${choice === null ? "required" : ""}`}>
          <section
            className="privacy-consent-banner"
            role="dialog"
            aria-modal={choice === null}
            aria-labelledby="privacy-consent-title"
          >
          {choice !== null && <button
            className="privacy-consent-close"
            type="button"
            aria-label="Закрыть"
            onClick={() => setOpen(false)}
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
            {choice === null && <small>Для продолжения выберите один из вариантов.</small>}
            {privacySignal && (
              <small>Браузер передал GPC/DNT — необязательная статистика будет отключена.</small>
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
