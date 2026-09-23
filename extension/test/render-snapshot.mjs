// 把 out/ 下成对的 snapshot(.html/.css) 渲染成 PNG，用于「改动前 vs 改动后」目视对比。
//   node extension/test/render-snapshot.mjs [前缀] [输出名]      # 默认 snapshot → cmp.png
import { readFileSync, writeFileSync, mkdirSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { renderRaw, installSubsetFont } from "../papersharp/runtime.mjs";

const here = dirname(fileURLToPath(import.meta.url));
const out = join(here, "out");
const prefix = process.argv[2] || "snapshot";
const name = process.argv[3] || `cmp-${prefix}.png`;
const width = Number(process.argv[4] || 1406);

await installSubsetFont(
  readFileSync(join(here, "..", "fonts", "YaheiSub-Regular.ttf")),
  readFileSync(join(here, "..", "fonts", "YaheiSub-Bold.ttf")),
);
const html = readFileSync(join(out, `${prefix}.html`), "utf8");
const css = readFileSync(join(out, `${prefix}.css`), "utf8");
const t0 = Date.now();
const bytes = await renderRaw({ html, css, format: "png", width, scale: 1 });
mkdirSync(out, { recursive: true });
writeFileSync(join(out, name), bytes);
console.log(`${prefix} → out/${name}  ${(bytes.length / 1024) | 0} KB  引擎 ${((Date.now() - t0) / 1000).toFixed(1)}s  (css ${(css.length / 1024) | 0} KB, html ${(html.length / 1024) | 0} KB)`);
process.exit(0);
