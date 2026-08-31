import { readFileSync } from "node:fs";
import { join } from "node:path";
import { describe, expect, it } from "vitest";
import metadata from "./seoMetadata.json";
import { publicInfoPaths } from "./publicInfoRoutes";

describe("SEO source of truth", () => {
  it("covers every indexable route with concise unique metadata", () => {
    expect(Object.keys(metadata.pages).sort()).toEqual(["/", ...Object.keys(publicInfoPaths)].sort());
    const pages = Object.values(metadata.pages);
    expect(new Set(pages.map(page => page.title)).size).toBe(pages.length);
    expect(new Set(pages.map(page => page.description)).size).toBe(pages.length);
    for (const page of pages) {
      expect(page.title.length).toBeGreaterThanOrEqual(20);
      expect(page.title.length).toBeLessThanOrEqual(65);
      expect(page.description.length).toBeGreaterThanOrEqual(80);
      expect(page.description.length).toBeLessThanOrEqual(180);
    }
  });

  it("generates dedicated HTML, sitemap and crawler policy during production build", () => {
    const script = readFileSync(join(process.cwd(), "scripts", "generate-seo.mjs"), "utf8");
    expect(script).toContain('writeFile(join(dist, "sitemap.xml")');
    expect(script).toContain('writeFile(join(dist, "robots.txt")');
    expect(script).toContain('`${path.slice(1)}.html`');
  });
});
