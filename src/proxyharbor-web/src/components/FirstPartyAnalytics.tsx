import { useEffect, useRef } from 'react';
import { analyticsAllowed, privacyPreferenceChanged } from '../privacyPreferences';
import { useSiteSettings } from '../siteSettingsContext';

export function FirstPartyAnalytics() {
  const { settings, loading, analyticsReady } = useSiteSettings();
  const recordedPath = useRef('');
  const revision = settings.cookieConsentRevision;
  const enabled = settings.analytics.firstPartyEnabled;

  useEffect(() => {
    if (loading || !analyticsReady || !enabled) return;
    const record = () => {
      const path = window.location.pathname.replace(/\/+$/, '') || '/';
      if (!analyticsAllowed(revision) || recordedPath.current === path ||
        typeof navigator.sendBeacon !== 'function') return;
      try {
        // Never include query/fragment, even for login/reset/payment routes.
        const payload = new Blob([JSON.stringify({ path })], { type: 'application/json' });
        if (navigator.sendBeacon(`${import.meta.env.VITE_API_URL ?? ''}/api/v1/telemetry/visit`, payload)) {
          recordedPath.current = path;
        }
      } catch {
        // Optional browser telemetry must not break rendering or navigation.
      }
    };
    record();
    window.addEventListener(privacyPreferenceChanged, record);
    window.addEventListener('storage', record);
    window.addEventListener('popstate', record);
    return () => {
      window.removeEventListener(privacyPreferenceChanged, record);
      window.removeEventListener('storage', record);
      window.removeEventListener('popstate', record);
    };
  }, [loading, analyticsReady, enabled, revision]);

  return null;
}
