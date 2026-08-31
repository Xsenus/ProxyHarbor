import { readFile, writeFile } from "node:fs/promises";
import { join } from "node:path";

const root = process.cwd();
const dist = join(root, "dist");
const metadata = JSON.parse(await readFile(join(root, "src", "seoMetadata.json"), "utf8"));
const template = await readFile(join(dist, "index.html"), "utf8");
const buildDate = new Date().toISOString().slice(0, 10);

function escapeHtml(value) {
  return value.replaceAll("&", "&amp;").replaceAll('"', "&quot;").replaceAll("<", "&lt;").replaceAll(">", "&gt;");
}

function structuredData(path, page) {
  const url = `${metadata.siteUrl}${path === "/" ? "/" : path}`;
  const graph = [
    {
      "@type": "Organization",
      "@id": `${metadata.siteUrl}/#organization`,
      name: metadata.siteName,
      url: `${metadata.siteUrl}/`,
      email: "ilel@list.ru",
      telephone: "+7-913-014-93-49",
    },
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
      name: metadata.siteName,
      description: page.description,
      serviceType: "Информационный онлайн-сервис",
      areaServed: "RU",
      provider: { "@id": `${metadata.siteUrl}/#organization` },
      offers: { "@type": "Offer", url: `${metadata.siteUrl}/pricing`, priceCurrency: "RUB" },
    });
  }
  return JSON.stringify({ "@context": "https://schema.org", "@graph": graph }).replaceAll("<", "\\u003c");
}

function render(path, page) {
  const url = `${metadata.siteUrl}${path === "/" ? "/" : path}`;
  const head = [
    `    <meta name="description" content="${escapeHtml(page.description)}" />`,
    '    <meta name="robots" content="index, follow, max-image-preview:large" />',
    '    <meta name="googlebot" content="index, follow" />',
    '    <meta name="yandex" content="index, follow" />',
    `    <link rel="canonical" href="${url}" />`,
    '    <meta property="og:type" content="website" />',
    `    <meta property="og:locale" content="${metadata.locale}" />`,
    `    <meta property="og:site_name" content="${metadata.siteName}" />`,
    `    <meta property="og:title" content="${escapeHtml(page.title)}" />`,
    `    <meta property="og:description" content="${escapeHtml(page.description)}" />`,
    `    <meta property="og:url" content="${url}" />`,
    '    <meta name="twitter:card" content="summary" />',
    `    <meta name="twitter:title" content="${escapeHtml(page.title)}" />`,
    `    <meta name="twitter:description" content="${escapeHtml(page.description)}" />`,
    `    <script id="proxyharbor-structured-data" type="application/ld+json">${structuredData(path, page)}</script>`,
  ].join("\n");
  return template
    .replace(/\s*<meta name="description"[^>]*>/, "")
    .replace(/\s*<meta name="robots"[^>]*>/, "")
    .replace(/\s*<title>[^<]*<\/title>/, "")
    .replace("    <meta name=\"viewport\"", `${head}\n    <title>${escapeHtml(page.title)}</title>\n    <meta name=\"viewport\"`);
}

for (const [path, page] of Object.entries(metadata.pages)) {
  const filename = path === "/" ? "index.html" : `${path.slice(1)}.html`;
  await writeFile(join(dist, filename), render(path, page), "utf8");
}

const sitemapEntries = Object.entries(metadata.pages).map(([path, page]) => `  <url>
    <loc>${metadata.siteUrl}${path === "/" ? "/" : path}</loc>
    <lastmod>${buildDate}</lastmod>
    <changefreq>${page.changeFrequency}</changefreq>
    <priority>${page.priority.toFixed(1)}</priority>
  </url>`).join("\n");
await writeFile(join(dist, "sitemap.xml"), `<?xml version="1.0" encoding="UTF-8"?>
<urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">
${sitemapEntries}
</urlset>
`, "utf8");

await writeFile(join(dist, "robots.txt"), `User-agent: *
Allow: /
Disallow: /admin
Disallow: /account
Disallow: /login
Disallow: /register
Disallow: /forgot-password
Disallow: /reset-password

Sitemap: ${metadata.siteUrl}/sitemap.xml
Host: ${new URL(metadata.siteUrl).host}
`, "utf8");

console.log(`SEO artifacts generated for ${Object.keys(metadata.pages).length} public routes.`);
