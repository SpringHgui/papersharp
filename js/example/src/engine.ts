// 浏览器侧的 .NET WASM 引擎加载层。
//
// 引擎产物是 npm 包 papersharp 的一部分（包内 wasm/_framework），由 scripts/sync-engine.mjs
// 拷进 public/_framework 后按静态资源服务。加载走 loadDotnet() 的 module script 注入：
// dotnet.js 必须留在原 URL 上，它按自身位置取同目录的 .wasm/.dat，被打包就全断了。
//
// C# 侧导出面见 src/PaperSharp.Wasm/Api.cs：入参出参统一 JSON 信封 {ok,data|error}，字节走 base64。

const BASE = import.meta.env.BASE_URL;

export type EngineFormat = "png" | "jpeg" | "webp" | "svg" | "pdf" | "ico";

export interface RenderRequest {
  html: string;
  css: string;
  format: EngineFormat;
  width?: number;
  scale?: number;
  quality?: number;
  pageSize?: "a4" | "letter";
  icoSize?: number;
  animationTime?: number;
}

export interface EngineInfo {
  papersharp: string;
  runtime: string;
  wasm: boolean;
}

type Api = {
  Render(requestJson: string): string | Promise<string>;
  Info(): string | Promise<string>;
};

type Dotnet = { create(): Promise<any> };

let apiPromise: Promise<Api> | null = null;

/**
 * 按 URL 加载 ESM：注入一段 module script，而不是 import(url)。
 * 后者会被打包器改写成 ?import 并塞进自己的模块图，public/ 下的 dotnet.js 不在图里 → 500；
 * 且 dotnet.js 必须留在原 URL 上，它按自身位置去取同目录的 .wasm/.dat。
 *
 * 注意别等 script 的 load 事件：动态插入的 module script 在 Chrome 里不派发它（实测全局已写好、
 * 事件却始终不来），只能轮询取值；onerror 仍然照收，用来快速暴露 404/网络失败。
 */
async function loadDotnet(url: string): Promise<Dotnet> {
  const key = "__papersharpDotnet";
  const existing = (window as any)[key] as Dotnet | undefined;
  if (existing) return existing;

  const errored = new Promise<never>((_, reject) => {
    const el = Object.assign(document.createElement("script"), {
      type: "module",
      textContent: `import { dotnet } from ${JSON.stringify(url)}; window["${key}"] = dotnet;`,
    });
    el.onerror = () => reject(new Error(`引擎脚本加载失败：${url}`));
    document.head.appendChild(el);
  });

  const deadline = Date.now() + 300000;
  for (;;) {
    const got = (window as any)[key] as Dotnet | undefined;
    if (got) return got;
    await Promise.race([new Promise((r) => setTimeout(r, 200)), errored]);
    if (Date.now() > deadline) throw new Error(`等 ${url} 导出 dotnet 超时`);
  }
}

async function getApi(): Promise<Api> {
  if (!apiPromise) {
    apiPromise = (async () => {
      const dotnet = await loadDotnet(`${BASE}_framework/dotnet.js`);
      const runtime = await dotnet.create();
      const exports = await runtime.getAssemblyExports("PaperSharp.Wasm");
      return exports.PaperSharp.Wasm.Api as Api;
    })();
    apiPromise.catch(() => (apiPromise = null));
  }
  return apiPromise;
}

async function call(
  api: Api,
  method: "Render" | "Info",
  ...args: (string | number)[]
): Promise<string> {
  const fn = api[method] as unknown as (...a: (string | number)[]) => string | Promise<string>;
  const raw = await fn(...args);
  const res = JSON.parse(raw);
  if (!res.ok) throw new Error(res.error ?? "papersharp: 渲染失败");
  return res.data;
}

/**
 * 渲染一次，返回产物字节。首次调用会启动运行时。
 * 中文开箱即用：引擎自带一份 Noto SC 子集（PaperSharp.wasm 的内嵌资源），不必再喂字体。
 * 要换别的字体包，走 npm 包 papersharp 的 registerFont(family, url, weight)。
 */
export async function render(req: RenderRequest): Promise<Uint8Array> {
  const api = await getApi();
  const data = await call(api, "Render", JSON.stringify(req));
  return b64ToBytes(data);
}

export async function engineInfo(): Promise<EngineInfo> {
  const api = await getApi();
  return JSON.parse(await call(api, "Info")) as EngineInfo;
}

/** 预热：让站点可以在后台把运行时先起好，用户点第一次渲染不等冷启动。 */
export function warmUp(): Promise<EngineInfo> {
  return engineInfo();
}

function b64ToBytes(b64: string): Uint8Array {
  const bin = atob(b64);
  const out = new Uint8Array(bin.length);
  for (let i = 0; i < bin.length; i++) out[i] = bin.charCodeAt(i);
  return out;
}
