import { readFile, readdir, stat } from "node:fs/promises";
import { resolve } from "node:path";

const maximumInitialJavaScriptBytes = 480 * 1024;
const outputDirectory = resolve("dist");
const html = await readFile(resolve(outputDirectory, "index.html"), "utf8");

// Vite declares the entry script and every eagerly required shared chunk in HTML.
// Lazy route chunks are intentionally absent and therefore do not consume this budget.
const initialAssetUrls = new Set(
  [...html.matchAll(/(?:src|href)="([^"?]+\.js)(?:\?[^\"]*)?"/g)].map(
    (match) => match[1],
  ),
);

const emittedAssets = await readdir(resolve(outputDirectory, "assets"));
const requiredLazyChunks = ["AccountPage-", "AdminCheckerNodesPage-", "AdminPaymentsPage-", "AdminTelegramPage-", "AdminUsersPage-", "AdminVpnPage-"];
for (const prefix of requiredLazyChunks) {
  const chunk = emittedAssets.find((asset) => asset.startsWith(prefix) && asset.endsWith(".js"));
  if (!chunk) {
    throw new Error(`Bundle budget: required lazy route chunk ${prefix}*.js was not emitted.`);
  }
  if ([...initialAssetUrls].some((assetUrl) => assetUrl.endsWith(`/${chunk}`))) {
    throw new Error(`Bundle budget: lazy route chunk ${chunk} was added to the initial page.`);
  }
}

if (initialAssetUrls.size === 0) {
  throw new Error("Bundle budget: index.html does not reference an initial JavaScript asset.");
}

let initialBytes = 0;
for (const assetUrl of initialAssetUrls) {
  const relativePath = assetUrl.replace(/^\/+/, "");
  initialBytes += (await stat(resolve(outputDirectory, relativePath))).size;
}

const formattedSize = (initialBytes / 1024).toFixed(1);
const formattedBudget = (maximumInitialJavaScriptBytes / 1024).toFixed(0);
if (initialBytes > maximumInitialJavaScriptBytes) {
  throw new Error(
    `Initial JavaScript is ${formattedSize} KiB; budget is ${formattedBudget} KiB. ` +
      "Move route-specific code behind a dynamic import instead of raising the budget.",
  );
}

console.log(`Bundle budget passed: ${formattedSize} KiB / ${formattedBudget} KiB initial JavaScript.`);
