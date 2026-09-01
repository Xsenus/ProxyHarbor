export const siteSectionCodes = [
  "legal", "pricing", "service", "offer", "privacy", "personal-data-consent",
  "marketing-consent", "acceptable-use", "cookies", "refunds", "requisites",
] as const;
export type SiteSectionCode = (typeof siteSectionCodes)[number];
export const requiredSiteSections = new Set<SiteSectionCode>(["offer", "privacy", "personal-data-consent", "cookies"]);
export const siteSectionLabels: Record<SiteSectionCode, string> = {
  legal: "Документы", pricing: "Тарифы", service: "Получение доступа", offer: "Публичная оферта",
  privacy: "Политика обработки данных", "personal-data-consent": "Согласие на обработку данных",
  "marketing-consent": "Согласие на рассылки", "acceptable-use": "Допустимое использование",
  cookies: "Политика cookies", refunds: "Отмена и возврат", requisites: "Контакты и реквизиты",
};
export const requisiteFieldCodes = [
  "fullName", "inn", "taxStatus", "address", "email", "phone", "bankRecipient", "bankAccount",
  "bankName", "bik", "correspondentAccount", "bankInn", "bankKpp",
] as const;
export type RequisiteFieldCode = (typeof requisiteFieldCodes)[number];
export const bankRequisiteFields = new Set<RequisiteFieldCode>([
  "bankRecipient", "bankAccount", "bankName", "bik", "correspondentAccount", "bankInn", "bankKpp",
]);
export const requisiteFieldLabels: Record<RequisiteFieldCode, string> = {
  fullName: "ФИО", inn: "ИНН", taxStatus: "Статус", address: "Адрес для корреспонденции",
  email: "E-mail", phone: "Телефон", bankRecipient: "Получатель", bankAccount: "Счёт",
  bankName: "Банк", bik: "БИК", correspondentAccount: "Корреспондентский счёт",
  bankInn: "ИНН банка", bankKpp: "КПП банка",
};
export type SiteSectionSettings = { published: boolean; showInNavigation: boolean };
export type RequisiteFieldSettings = { value: string; visible: boolean };
export type ExternalAnalyticsSettings = { enabled: boolean; identifier: string };
export type SiteSettings = {
  sections: Record<SiteSectionCode, SiteSectionSettings>;
  requisites: { introTitle: string; introDescription: string; bankSectionVisible: boolean; note: string; fields: Record<RequisiteFieldCode, RequisiteFieldSettings> };
  cookies: { showSettingsButton: boolean; bannerTitle: string; bannerText: string };
  analytics: { firstPartyEnabled: boolean; yandex: ExternalAnalyticsSettings; google: ExternalAnalyticsSettings; vk: ExternalAnalyticsSettings };
  cookieConsentRevision: number; consentRequired: boolean; updatedAt?: string;
};
const sections = Object.fromEntries(siteSectionCodes.map((code) => [code, {
  published: true, showInNavigation: code !== "personal-data-consent" && code !== "marketing-consent",
}])) as Record<SiteSectionCode, SiteSectionSettings>;
const field = (): RequisiteFieldSettings => ({ value: "", visible: false });
// The browser bundle deliberately contains no operator PII. Values arrive only
// after server-side visibility and redaction rules have been applied.
export const defaultSiteSettings: SiteSettings = {
  sections,
  requisites: {
    introTitle: "Исполнитель",
    introDescription: "Самозанятый гражданин Российской Федерации, плательщик налога на профессиональный доход.",
    bankSectionVisible: false,
    note: "Для обращений по заказу используйте e-mail или телефон. Паспортные данные и реквизиты банковской карты клиента на сайте не публикуются и службой поддержки не запрашиваются.",
    fields: { fullName: field(), inn: field(), taxStatus: field(), address: field(), email: field(), phone: field(), bankRecipient: field(), bankAccount: field(), bankName: field(), bik: field(), correspondentAccount: field(), bankInn: field(), bankKpp: field() },
  },
  cookies: { showSettingsButton: true, bannerTitle: "Настройки конфиденциальности", bannerText: "Необходимые cookies обеспечивают вход и язык сайта. Статистика включается только с вашего разрешения." },
  analytics: { firstPartyEnabled: true, yandex: { enabled: false, identifier: "" }, google: { enabled: false, identifier: "" }, vk: { enabled: false, identifier: "" } },
  cookieConsentRevision: 1, consentRequired: true,
};
export function normalizeSiteSettings(value: Partial<SiteSettings> | null): SiteSettings {
  if (!value) return structuredClone(defaultSiteSettings);
  const result = structuredClone(defaultSiteSettings);
  for (const code of siteSectionCodes) result.sections[code] = { ...result.sections[code], ...value.sections?.[code] };
  result.requisites = { ...result.requisites, ...value.requisites };
  for (const code of requisiteFieldCodes) result.requisites.fields[code] = { ...result.requisites.fields[code], ...value.requisites?.fields?.[code] };
  result.cookies = { ...result.cookies, ...value.cookies };
  result.analytics = { ...result.analytics, ...value.analytics };
  result.analytics.yandex = { ...result.analytics.yandex, ...value.analytics?.yandex };
  result.analytics.google = { ...result.analytics.google, ...value.analytics?.google };
  result.analytics.vk = { ...result.analytics.vk, ...value.analytics?.vk };
  result.cookieConsentRevision = Math.max(1, value.cookieConsentRevision ?? 1);
  result.consentRequired = true; result.updatedAt = value.updatedAt;
  return result;
}
export function sectionHref(code: SiteSectionCode) { return `/${code}`; }
