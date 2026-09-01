export type AnalyticsChoice = "accepted" | "rejected" | null;

const storageKey = (revision: number) =>
  `proxyharbor.analytics-consent.v${Math.max(1, Math.trunc(revision))}`;
export const privacyPreferenceChanged =
  "proxyharbor:privacy-preference-changed";
export const openPrivacyPreferences = "proxyharbor:open-privacy-preferences";

export function privacySignalEnabled() {
  const browser = navigator as Navigator & { globalPrivacyControl?: boolean };
  return browser.globalPrivacyControl === true || navigator.doNotTrack === "1";
}

export function readAnalyticsChoice(revision = 1): AnalyticsChoice {
  const value = localStorage.getItem(storageKey(revision));
  if (value !== "accepted" && value !== "rejected") return null;
  return privacySignalEnabled() ? "rejected" : value;
}

export function writeAnalyticsChoice(
  choice: Exclude<AnalyticsChoice, null>,
  revision = 1,
) {
  localStorage.setItem(
    storageKey(revision),
    privacySignalEnabled() ? "rejected" : choice,
  );
  window.dispatchEvent(new Event(privacyPreferenceChanged));
}

export function analyticsAllowed(revision = 1) {
  return !privacySignalEnabled() && readAnalyticsChoice(revision) === "accepted";
}
