export type PublicInfoKind =
  | "legal"
  | "pricing"
  | "service"
  | "offer"
  | "privacy"
  | "personal-data-consent"
  | "marketing-consent"
  | "acceptable-use"
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
  "/marketing-consent": "marketing-consent",
  "/acceptable-use": "acceptable-use",
  "/cookies": "cookies",
  "/refunds": "refunds",
  "/requisites": "requisites",
};
