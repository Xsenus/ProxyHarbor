export type AnalyticsChoice = "accepted" | "rejected" | null;

const storageKey = "proxyharbor.analytics-consent.v1";
export const privacyPreferenceChanged =
  "proxyharbor:privacy-preference-changed";
export const openPrivacyPreferences = "proxyharbor:open-privacy-preferences";

function privacySignalEnabled() {
  const browser = navigator as Navigator & { globalPrivacyControl?: boolean };
  return browser.globalPrivacyControl === true || navigator.doNotTrack === "1";
}

export function readAnalyticsChoice(): AnalyticsChoice {
  if (privacySignalEnabled()) return "rejected";
  const value = localStorage.getItem(storageKey);
  return value === "accepted" || value === "rejected" ? value : null;
}

export function writeAnalyticsChoice(choice: Exclude<AnalyticsChoice, null>) {
  localStorage.setItem(storageKey, choice);
  window.dispatchEvent(new Event(privacyPreferenceChanged));
}

export function analyticsAllowed() {
  return readAnalyticsChoice() === "accepted";
}
