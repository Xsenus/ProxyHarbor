export type PublicInfoKind =
  | "legal"
  | "pricing"
  | "service"
  | "offer"
  | "privacy"
  | "personal-data-consent"
  | "cookies"
  | "refunds"
  | "requisites";

export const publicInfoPaths: Record<string, PublicInfoKind> = {
  "/legal": "legal",
  "/pricing": "pricing",
  "/service": "service",
  "/offer": "offer",
  "/privacy": "privacy",
  "/personal-data-consent": "personal-data-consent",
  "/cookies": "cookies",
  "/refunds": "refunds",
  "/requisites": "requisites",
};
