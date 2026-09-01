import { useCallback, useEffect, useMemo, useState } from "react";
import { SiteSettingsContext } from "./siteSettingsContext";
import { defaultSiteSettings, normalizeSiteSettings, type SiteSettings } from "./siteSettingsModel";

export function SiteSettingsProvider({ children }: { children: React.ReactNode }) {
  const [settings, setSettings] = useState(defaultSiteSettings);
  const [loading, setLoading] = useState(true);
  const refresh = useCallback(async () => {
    const response = await fetch(`${import.meta.env.VITE_API_URL ?? ""}/api/v1/site-settings`, {
      credentials: "same-origin", cache: "no-store",
    });
    if (!response.ok) throw new Error("Не удалось загрузить настройки сайта");
    const next = normalizeSiteSettings((await response.json()) as Partial<SiteSettings>);
    setSettings(next);
    return next;
  }, []);
  useEffect(() => {
    let active = true;
    const timer = window.setTimeout(() => {
      void refresh().catch(() => {
        if (active) setSettings(structuredClone(defaultSiteSettings));
      }).finally(() => {
        if (active) setLoading(false);
      });
    }, 0);
    return () => { active = false; window.clearTimeout(timer); };
  }, [refresh]);
  const value = useMemo(() => ({ settings, loading, refresh }), [settings, loading, refresh]);
  return <SiteSettingsContext.Provider value={value}>{children}</SiteSettingsContext.Provider>;
}
