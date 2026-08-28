import { Cookie, Settings2, X } from "lucide-react";
import { useEffect, useState } from "react";
import {
  openPrivacyPreferences,
  privacyPreferenceChanged,
  readAnalyticsChoice,
  writeAnalyticsChoice,
} from "../privacyPreferences";

export function PrivacyControls() {
  const [choice, setChoice] = useState(readAnalyticsChoice);
  const [open, setOpen] = useState(choice === null);

  useEffect(() => {
    const refresh = () => setChoice(readAnalyticsChoice());
    const show = () => setOpen(true);
    window.addEventListener(privacyPreferenceChanged, refresh);
    window.addEventListener(openPrivacyPreferences, show);
    return () => {
      window.removeEventListener(privacyPreferenceChanged, refresh);
      window.removeEventListener(openPrivacyPreferences, show);
    };
  }, []);

  const choose = (value: "accepted" | "rejected") => {
    writeAnalyticsChoice(value);
    setChoice(value);
    setOpen(false);
  };

  return (
    <>
      <button
        className="privacy-settings-button"
        type="button"
        onClick={() => setOpen(true)}
        aria-label="Настройки cookies"
      >
        <Settings2 /> Cookies
      </button>
      {open && (
        <section
          className="privacy-consent-banner"
          role="dialog"
          aria-modal="false"
          aria-labelledby="privacy-consent-title"
        >
          <button
            className="privacy-consent-close"
            type="button"
            aria-label="Закрыть"
            onClick={() => setOpen(false)}
          >
            <X />
          </button>
          <Cookie />
          <div>
            <h2 id="privacy-consent-title">Настройки конфиденциальности</h2>
            <p>
              Необходимые cookies обеспечивают вход и язык сайта. Необязательная
              статистика посещений включается только с вашего разрешения.
              Рекламных cookies нет.
            </p>
            <a href="/cookies">Подробнее о cookies</a>
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
            >
              Разрешить статистику
            </button>
          </div>
        </section>
      )}
    </>
  );
}
