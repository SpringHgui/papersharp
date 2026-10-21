// papersharp — JSX/HTML + CSS → PNG/JPEG/WebP/SVG/PDF，.NET+Skia WASM 引擎，无头浏览器零依赖。
//
//   import { render } from "papersharp";
//   const png = await render(<div tw="w-full h-full flex">…</div>, { width: 1200, height: 630 });
//
// 运行要求：bun 或 node ≥18（node 跑 .tsx 需自行接 JSX 转换，如 tsx/hono/jsx）。

import { renderRaw, registerFontRaw, info } from "./runtime.mjs";
import { jsxToHtml } from "./jsx.mjs";
import { createCompiler } from "./tailwind.mjs";

/**
 * 渲染一张图/一份 PDF。
 * @param {any|string} input JSX 元素（对象树）或 HTML 字符串
 * @param {{width?:number,height?:number,scale?:number,format?:string,css?:string,quality?:number,pageSize?:string,animationTime?:number}} [opts]
 * @returns {Promise<Uint8Array>} 图片/PDF 字节
 */
export async function render(input, opts = {}) {
  const compiler = createCompiler();
  let html = typeof input === "string" ? input : jsxToHtml(input, compiler);

  const width = opts.width ?? 1200;
  // 固定高度：包一层确定尺寸的容器，让根元素的 w-full/h-full 有 100% 参照
  if (opts.height) html = `<div style="width:${width}px;height:${opts.height}px">${html}</div>`;

  const css = [compiler.css(), opts.css ?? ""].filter(Boolean).join("\n");
  return renderRaw({
    html,
    css,
    width,
    scale: opts.scale ?? 1,
    format: opts.format ?? "png",
    quality: opts.quality,
    pageSize: opts.pageSize,
    // 确定性取帧时刻（秒）：给定则按该时刻求值 @keyframes/animation 插值
    animationTime: opts.animationTime,
  });
}

/**
 * 结束 .NET WASM 运行时并退出进程。
 * .NET 的 wasm 运行时会常驻事件循环（渲染很快，但进程不会自己退），
 * 脚本渲染完需显式 quit()——或直接用 renderToFile（写完默认自动退出）。
 * @param {number} [code] 退出码，默认 0
 */
export function quit(code = 0) {
  process.exit(code);
}

/**
 * 渲染并写盘。写完默认结束常驻运行时并退出进程（对齐 takumi 的一次性脚本体验）。
 * 需在长驻服务里连续渲染、不希望进程退出时，传 opts.keepAlive = true。
 * @param {string} path 输出文件路径
 */
export async function renderToFile(path, input, opts = {}) {
  const bytes = await render(input, opts);
  const { writeFile } = await import("node:fs/promises");
  await writeFile(path, bytes);
  if (!opts.keepAlive) quit(0);
  return bytes;
}

/**
 * 注册字体（渲染中文/自定义字体的推荐方式）。
 * @param {string} family CSS font-family 里写的名字
 * @param {string|Uint8Array} source ttf/otf 文件路径、URL 或字节
 * @param {number} [weight] 默认 400
 */
export async function registerFont(family, source, weight = 400) {
  let bytes;
  if (typeof source === "string") {
    if (/^https?:/.test(source)) {
      const res = await fetch(source);
      if (!res.ok) throw new Error(`papersharp: font fetch failed ${res.status} ${source}`);
      bytes = new Uint8Array(await res.arrayBuffer());
    } else {
      const { readFile } = await import("node:fs/promises");
      bytes = new Uint8Array(await readFile(source));
    }
  } else {
    bytes = source;
  }
  await registerFontRaw(family, bytes, weight);
}

/** 把本地图片/URL 读成 data URI，供 <img src> 使用（WASM 沙箱读不到相对路径文件）。 */
export async function dataUri(src) {
  if (/^data:/.test(src)) return src;
  let bytes;
  if (/^https?:/.test(src)) {
    const res = await fetch(src);
    if (!res.ok) throw new Error(`papersharp: fetch failed ${res.status} ${src}`);
    bytes = Buffer.from(await res.arrayBuffer());
  } else {
    const { readFile } = await import("node:fs/promises");
    bytes = await readFile(src);
  }
  const ext = src.split("?")[0].split(".").pop()?.toLowerCase();
  const mime = { png: "image/png", jpg: "image/jpeg", jpeg: "image/jpeg", webp: "image/webp", gif: "image/gif", svg: "image/svg+xml" }[ext] ?? "application/octet-stream";
  return `data:${mime};base64,${bytes.toString("base64")}`;
}

export { info };
