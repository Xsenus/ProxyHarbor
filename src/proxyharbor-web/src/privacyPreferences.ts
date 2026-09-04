export type AnalyticsChoice = "accepted" | "rejected" | null;

const storageKey = (revision: number) =>
  `proxyharbor.analytics-consent.v${Math.max(1, Math.trunc(revision))}`;
// A browser may deny storage entirely or allow reads while rejecting writes.
// Failed writes must override stale persisted consent, especially on revocation.
// This fallback expires with the document; it never grants consent implicitly.
const sessionChoices = new Map<string, Exclude<AnalyticsChoice, null>>();
export const privacyPreferenceChanged =
  "proxyharbor:privacy-preference-changed";
export const openPrivacyPreferences = "proxyharbor:open-privacy-preferences";

export function privacySignalEnabled() {
  const browser = navigator as Navigator & { globalPrivacyControl?: boolean };
  return browser.globalPrivacyControl === true || navigator.doNotTrack === "1";
}

export function readAnalyticsChoice(revision = 1): AnalyticsChoice {
  const key = storageKey(revision);
  let value: string | null = sessionChoices.get(key) ?? null;
  if (value === null) {
    try {
      value = localStorage.getItem(key);
    } catch {
      // Unknown consent is fail-closed, but must not crash the application.
      return null;
    }
  }
  if (value !== "accepted" && value !== "rejected") return null;
  return privacySignalEnabled() ? "rejected" : value;
}

export function writeAnalyticsChoice(
  choice: Exclude<AnalyticsChoice, null>,
  revision = 1,
) {
  const key = storageKey(revision);
  const effective = privacySignalEnabled() ? "rejected" : choice;
  try {
    localStorage.setItem(key, effective);
    sessionChoices.delete(key);
  } catch {
    sessionChoices.set(key, effective);
  }
  window.dispatchEvent(new Event(privacyPreferenceChanged));
}

export function analyticsAllowed(revision = 1) {
  return !privacySignalEnabled() && readAnalyticsChoice(revision) === "accepted";
}
