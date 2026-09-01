import { useEffect } from "react";
import metadata from "./seoMetadata.json";
import { publicInfoPaths } from "./publicInfoRoutes";
import { useSiteSettings } from "./siteSettingsContext";
import type { SiteSettings } from "./siteSettingsModel";

type SeoPage = {
  title: string;
  description: string;
  changeFrequency: string;
  priority: number;
};

const pages = metadata.pages as Record<string, SeoPage>;
const privatePath = /^\/(?:admin(?:\/|$)|account(?:\/|$)|login$|register$|forgot-password$|reset-password$)/;

function normalizedPath() {
  return window.location.pathname.replace(/\/+$/, "") || "/";
}

function setNamedMeta(name: string, content: string) {
  let element = document.head.querySelector<HTMLMetaElement>(`meta[name="${name}"]`);
  if (!element) {
    element = document.createElement("meta");
    element.name = name;
    document.head.append(element);
  }
  element.content = content;
}

function setPropertyMeta(property: string, content: string) {
  let element = document.head.querySelector<HTMLMetaElement>(`meta[property="${property}"]`);
  if (!element) {
    element = document.createElement("meta");
    element.setAttribute("property", property);
    document.head.append(element);
  }
  element.content = content;
}

function setCanonical(url: string) {
  let element = document.head.querySelector<HTMLLinkElement>('link[rel="canonical"]');
  if (!element) {
    element = document.createElement("link");
    element.rel = "canonical";
    document.head.append(element);
  }
  element.href = url;
}

function structuredData(path: string, page: SeoPage, settings: SiteSettings) {
  const url = `${metadata.siteUrl}${path === "/" ? "/" : path}`;
  const email = settings.requisites.fields.email;
  const phone = settings.requisites.fields.phone;
  const organization: Record<string, unknown> = {
    "@type": "Organization",
    "@id": `${metadata.siteUrl}/#organization`,
    name: metadata.siteName,
    url: `${metadata.siteUrl}/`,
  };
  if (email.visible && email.value.trim()) organization.email = email.value.trim();
  if (phone.visible && phone.value.trim()) organization.telephone = phone.value.trim();
  const graph: Record<string, unknown>[] = [
    organization,
    {
      "@type": "WebSite",
      "@id": `${metadata.siteUrl}/#website`,
      url: `${metadata.siteUrl}/`,
      name: metadata.siteName,
      inLanguage: "ru-RU",
      publisher: { "@id": `${metadata.siteUrl}/#organization` },
    },
    {
      "@type": "WebPage",
      "@id": `${url}#webpage`,
      url,
      name: page.title,
      description: page.description,
      inLanguage: "ru-RU",
      isPartOf: { "@id": `${metadata.siteUrl}/#website` },
      about: { "@id": `${metadata.siteUrl}/#organization` },
    },
  ];

  if (path === "/") {
    graph.push({
      "@type": "Service",
      "@id": `${metadata.siteUrl}/#service`,
      name: "ProxyHarbor",
      description: page.description,
      serviceType: "Информационный онлайн-сервис",
      areaServed: "RU",
      provider: { "@id": `${metadata.siteUrl}/#organization` },
      ...(settings.sections.pricing.published
        ? { offers: { "@type": "Offer", url: `${metadata.siteUrl}/pricing`, priceCurrency: "RUB" } }
        : {}),
    });
  }

  return { "@context": "https://schema.org", "@graph": graph };
}

export function SeoHead() {
  const path = normalizedPath();
  const { settings, loading } = useSiteSettings();

  useEffect(() => {
    const page = pages[path];
    const isPrivate = privatePath.test(path);
    const section = publicInfoPaths[path];
    const published = !section || settings.sections[section].published;
    const isIndexable = Boolean(page) && !isPrivate && published && !loading;
    const title = page?.title ?? (isPrivate ? "Личный кабинет — ProxyHarbor" : "Страница не найдена — ProxyHarbor");
    const description = page?.description ?? (isPrivate
      ? "Защищённый раздел ProxyHarbor."
      : "Запрошенная страница ProxyHarbor не найдена.");
    const canonical = `${metadata.siteUrl}${path === "/" ? "/" : path}`;

    document.documentElement.lang = "ru";
    document.title = title;
    setNamedMeta("description", description);
    setNamedMeta("robots", isIndexable ? "index, follow, max-image-preview:large" : "noindex, nofollow, noarchive");
    setNamedMeta("googlebot", isIndexable ? "index, follow" : "noindex, nofollow, noarchive");
    setNamedMeta("yandex", isIndexable ? "index, follow" : "noindex, nofollow, noarchive");
    setNamedMeta("twitter:card", "summary");
    setNamedMeta("twitter:title", title);
    setNamedMeta("twitter:description", description);
    setPropertyMeta("og:type", "website");
    setPropertyMeta("og:locale", metadata.locale);
    setPropertyMeta("og:site_name", metadata.siteName);
    setPropertyMeta("og:title", title);
    setPropertyMeta("og:description", description);
    setPropertyMeta("og:url", canonical);
    setCanonical(canonical);

    let script = document.head.querySelector<HTMLScriptElement>('#proxyharbor-structured-data');
    if (!isIndexable) {
      script?.remove();
      return;
    }
    if (!script) {
      script = document.createElement("script");
      script.id = "proxyharbor-structured-data";
      script.type = "application/ld+json";
      document.head.append(script);
    }
    script.textContent = JSON.stringify(structuredData(path, page, settings));
  }, [path, settings, loading]);

  return null;
}
