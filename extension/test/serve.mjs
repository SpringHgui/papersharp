// 端到端测试用的两个静态源（不同端口 = 不同 origin，专门制造跨源 CSS/图片场景）：
//   127.0.0.1:8391  页面（test/fixtures/*.html）
//   127.0.0.1:8392  资源（PNG 由本文件现算现发，不入库）
//
// 用法：node extension/test/serve.mjs [--host 127.0.0.1]

import { createServer } from "node:http";
import { readFile } from "node:fs/promises";
import { deflateSync } from "node:zlib";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const here = dirname(fileURLToPath(import.meta.url));
const HOST = "127.0.0.1";
const PAGE_PORT = 8391;
const ASSET_PORT = 8392;

export const ASSET_ORIGIN = `http://${HOST}:${ASSET_PORT}`;
export const PAGE_ORIGIN = `http://${HOST}:${PAGE_PORT}`;

// ── 现算 PNG（无第三方依赖）──────────────────────────────────────────────
function crc32(buf) {
  let c, table = crc32.table || (crc32.table = (() => {
    const t = new Int32Array(256);
    for (let n = 0; n < 256; n++) { let x = n; for (let k = 0; k < 8; k++) x = x & 1 ? 0xedb88320 ^ (x >>> 1) : x >>> 1; t[n] = x; }
    return t;
  })());
  c = -1;
  for (const b of buf) c = (c >>> 8) ^ table[(c ^ b) & 0xff];
  return (c ^ -1) >>> 0;
}

function chunk(type, data) {
  const len = Buffer.alloc(4); len.writeUInt32BE(data.length);
  const body = Buffer.concat([Buffer.from(type, "ascii"), data]);
  const crc = Buffer.alloc(4); crc.writeUInt32BE(crc32(body));
  return Buffer.concat([len, body, crc]);
}

/** 横向渐变 + 四角纯色块的测试图（导出里能一眼看出是否真被内联、是否错位） */
function gradientPng(w, h) {
  const ihdr = Buffer.alloc(13);
  ihdr.writeUInt32BE(w, 0); ihdr.writeUInt32BE(h, 4);
  ihdr[8] = 8; ihdr[9] = 2; ihdr[10] = 0; ihdr[11] = 0; ihdr[12] = 0;
  const raw = Buffer.alloc(h * (1 + w * 3));
  for (let y = 0; y < h; y++) {
    const row = y * (1 + w * 3);
    raw[row] = 0;
    for (let x = 0; x < w; x++) {
      let r = Math.round(255 * (x / Math.max(1, w - 1)));
      let g = Math.round(255 * (y / Math.max(1, h - 1)));
      let b = 128;
      if (x < 12 && y < 12) { r = 220; g = 30; b = 30; }          // 左上红块
      if (x > w - 13 && y < 12) { r = 30; g = 180; b = 40; }      // 右上绿块
      if (x < 12 && y > h - 13) { r = 40; g = 80; b = 230; }      // 左下蓝块
      const o = row + 1 + x * 3;
      raw[o] = r; raw[o + 1] = g; raw[o + 2] = b;
    }
  }
  return Buffer.concat([
    Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]),
    chunk("IHDR", ihdr), chunk("IDAT", deflateSync(raw)), chunk("IEND", Buffer.alloc(0)),
  ]);
}

const CROSS_ORIGIN_CSS = `
/* 跨源样式表：故意混用 @layer / @media / var() / ::before 计数器，考察展平是否吃得住 */
:root { --brand: #1d4ed8; --pad: 14px }
@layer components {
  .card { padding: var(--pad); border: 1px solid #cbd5e1; border-radius: 12px; background: #fff }
  .card h3 { color: var(--brand); font-size: 16px; margin: 0 0 6px }
}
.grid { display: flex; gap: 12px; flex-wrap: wrap }
.grid > * { flex: 1 1 220px }
.hero { background-image: url(/banner.png); background-size: cover; min-height: 120px; color: #fff }
.badge { position: absolute; top: 8px; right: 8px; padding: 4px 8px; border-radius: 999px; background: #16a34a; color: #fff; font-size: 12px }
ol.counters { counter-reset: item }
ol.counters li { counter-increment: item }
ol.counters li::before { content: "第 " counter(item) " 步："; font-weight: 700; color: #b91c1c }
@media (max-width: 400px) { .grid { flex-direction: column } .only-narrow { display: block } }
@media (min-width: 700px) { .only-narrow { display: none } }
`;

const ASSETS = {
  "/banner.png": ["image/png", gradientPng(320, 120)],
  "/logo.png": ["image/png", gradientPng(64, 64)],
  "/big.png": ["image/png", gradientPng(900, 500)],
  "/sprite.svg": ["image/svg+xml", Buffer.from(`<svg xmlns="http://www.w3.org/2000/svg"><symbol id="star" viewBox="0 0 24 24"><path d="M12 2l3 7h7l-5.5 4.5L18 21l-6-4-6 4 1.5-7.5L2 9h7z"/></symbol></svg>`)],
  "/frame.html": ["text/html; charset=utf-8", Buffer.from(`<!doctype html><meta charset="utf-8"><body style="margin:0;background:#fde68a;font:14px system-ui">我是 <code>iframe</code> 里的内容，导出应留占位盒</body>`, "utf8")],
  "/app.css": ["text/css; charset=utf-8", Buffer.from(CROSS_ORIGIN_CSS, "utf8")],
};


export function startServers() {
  const page = createServer(async (req, res) => {
    const path = decodeURIComponent((req.url || "/").split("?")[0]);
    const file = join(here, "fixtures", path === "/" ? "index.html" : path.replace(/^\//, ""));
    try {
      const data = await readFile(file);
      res.writeHead(200, { "content-type": file.endsWith(".html") ? "text/html; charset=utf-8" : "text/plain" });
      res.end(data);
    } catch {
      res.writeHead(404).end("not found");
    }
  });
  const asset = createServer((req, res) => {
    const path = decodeURIComponent((req.url || "/").split("?")[0]);
    const hit = ASSETS[path];
    if (!hit) { res.writeHead(404).end("not found"); return; }
    const [type, buf] = hit;
    res.writeHead(200, { "content-type": type, "access-control-allow-origin": "*", "cache-control": "no-store" });
    res.end(buf);
  });
  return new Promise((resolve) => {
    page.listen(PAGE_PORT, HOST, () => asset.listen(ASSET_PORT, HOST, () => resolve({
      page, asset,
      stop: () => { page.close(); asset.close(); },
    })));
  });
}

if (process.argv[1] && fileURLToPath(import.meta.url) === process.argv[1]) {
  startServers().then(() => console.log(`pages  → ${PAGE_ORIGIN}/showcase.html\nassets → ${ASSET_ORIGIN}/app.css`));
}
