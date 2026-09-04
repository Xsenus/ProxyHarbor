import { createContext, useContext } from "react";
import { defaultSiteSettings, type SiteSettings } from "./siteSettingsModel";

export type SiteSettingsContextValue = {
  settings: SiteSettings;
  loading: boolean;
  analyticsReady: boolean;
  refresh: () => Promise<SiteSettings>;
};
export const SiteSettingsContext = createContext<SiteSettingsContextValue>({
  settings: defaultSiteSettings,
  loading: false,
  analyticsReady: false,
  refresh: async () => defaultSiteSettings,
});
export function useSiteSettings() { return useContext(SiteSettingsContext); }
