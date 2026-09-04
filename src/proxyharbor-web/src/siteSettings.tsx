import { useCallback, useEffect, useMemo, useState } from "react";
import { SiteSettingsContext } from "./siteSettingsContext";
import { defaultSiteSettings, normalizeSiteSettings, type SiteSettings } from "./siteSettingsModel";

export function SiteSettingsProvider({ children }: { children: React.ReactNode }) {
  const [settings, setSettings] = useState(defaultSiteSettings);
  const [loading, setLoading] = useState(true);
  const [analyticsReady, setAnalyticsReady] = useState(false);
  const refresh = useCallback(async () => {
    const response = await fetch(`${import.meta.env.VITE_API_URL ?? ""}/api/v1/site-settings`, {
      credentials: "same-origin", cache: "no-store",
    });
    if (!response.ok) throw new Error("Не удалось загрузить настройки сайта");
    const supplied = (await response.json()) as Partial<SiteSettings> | null;
    const next = normalizeSiteSettings(supplied);
    // Display defaults remain usable after an incomplete response, but are not
    // authoritative permission to collect analytics under an old revision.
    setAnalyticsReady(Number.isSafeInteger(supplied?.cookieConsentRevision) &&
      (supplied?.cookieConsentRevision ?? 0) > 0 &&
      typeof supplied?.analytics?.firstPartyEnabled === 'boolean');
    setSettings(next);
    return next;
  }, []);
  useEffect(() => {
    let active = true;
    const timer = window.setTimeout(() => {
      void refresh().catch(() => {
        if (active) {
          setSettings(structuredClone(defaultSiteSettings));
          setAnalyticsReady(false);
        }
      }).finally(() => {
        if (active) setLoading(false);
      });
    }, 0);
    return () => { active = false; window.clearTimeout(timer); };
  }, [refresh]);
  const value = useMemo(() => ({ settings, loading, analyticsReady, refresh }), [settings, loading, analyticsReady, refresh]);
  return <SiteSettingsContext.Provider value={value}>{children}</SiteSettingsContext.Provider>;
}
