import { cleanup, render, waitFor } from "@testing-library/react";
import { afterEach, describe, expect, it } from "vitest";
import { SeoHead } from "./SeoHead";

function meta(name: string) {
  return document.head.querySelector<HTMLMetaElement>(`meta[name="${name}"]`);
}

describe("SeoHead", () => {
  afterEach(() => {
    cleanup();
    document.head.querySelectorAll('meta[name="robots"],meta[name="googlebot"],meta[name="yandex"],meta[name^="twitter:"],meta[property^="og:"],link[rel="canonical"],#proxyharbor-structured-data').forEach(element => element.remove());
    window.history.replaceState({}, "", "/");
  });

  it("publishes canonical metadata and structured data for public pages", async () => {
    window.history.replaceState({}, "", "/pricing");
    render(<SeoHead />);

    await waitFor(() => expect(document.title).toBe("Тарифы на доступ к прокси и VPN — ProxyHarbor"));
    expect(meta("robots")?.content).toContain("index, follow");
    expect(document.head.querySelector<HTMLLinkElement>('link[rel="canonical"]')?.href).toBe("https://proxy.blagodaty.ru/pricing");
    expect(document.head.querySelector<HTMLMetaElement>('meta[property="og:url"]')?.content).toBe("https://proxy.blagodaty.ru/pricing");
    const jsonLd = document.head.querySelector<HTMLScriptElement>("#proxyharbor-structured-data");
    expect(JSON.parse(jsonLd?.textContent ?? "")["@context"]).toBe("https://schema.org");
  });

  it("marks account and unknown routes as non-indexable", async () => {
    window.history.replaceState({}, "", "/account");
    const view = render(<SeoHead />);
    await waitFor(() => expect(meta("robots")?.content).toContain("noindex"));
    expect(document.head.querySelector("#proxyharbor-structured-data")).not.toBeInTheDocument();

    view.unmount();
    window.history.replaceState({}, "", "/missing-page");
    render(<SeoHead />);
    await waitFor(() => expect(document.title).toBe("Страница не найдена — ProxyHarbor"));
    expect(meta("robots")?.content).toContain("noindex");
  });
});
