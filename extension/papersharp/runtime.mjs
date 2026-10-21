// PaperSharp WASM 运行时适配层（浏览器扩展用，亦可直接在 node/bun 冒烟）。
// 懒加载单例：首次调用时启动 .NET runtime，之后每次渲染无冷启动开销。
// 与 js/papersharp/src/runtime.mjs 同源，区别：
//   1) import 路径面向本扩展目录（./wasm/_framework/dotnet.js）
//   2) 暴露 installSubsetFont —— 安装引擎兜底中文字体（必须在首次 Render 前调用）

import { dotnet } from "./wasm/_framework/dotnet.js";

let apiPromise = null;

async function getApi() {
  if (!apiPromise) {
    apiPromise = (async () => {
      const runtime = await dotnet.create();
      const exports = await runtime.getAssemblyExports("PaperSharp.Wasm");
      return { api: exports.PaperSharp.Wasm.Api, runtime };
    })();
    apiPromise.catch(() => (apiPromise = null));
  }
  const { api } = await apiPromise;
  return api;
}

function b64ToBytes(b64) {
  if (typeof Buffer !== "undefined") return new Uint8Array(Buffer.from(b64, "base64"));
  const bin = atob(b64);
  const out = new Uint8Array(bin.length);
  for (let i = 0; i < bin.length; i++) out[i] = bin.charCodeAt(i);
  return out;
}

function bytesToB64(data) {
  if (typeof Buffer !== "undefined") return Buffer.from(data).toString("base64");
  let bin = "";
  for (let i = 0; i < data.length; i++) bin += String.fromCharCode(data[i]);
  return btoa(bin);
}

async function call(method, ...args) {
  const api = await getApi();
  const raw = await api[method](...args);
  const res = JSON.parse(raw);
  if (!res.ok) throw new Error(`papersharp: ${res.error}`);
  return res.data;
}

/** @param {{html:string,css:string,format?:string,width?:number,scale?:number,quality?:number,pageSize?:string,animationTime?:number}} req */
export async function renderRaw(req) {
  const b64 = await call("Render", JSON.stringify(req));
  return b64ToBytes(b64);
}

/** @param {string} family @param {Uint8Array} data @param {number} weight */
export async function registerFontRaw(family, data, weight) {
  return call("RegisterFont", family, bytesToB64(data), weight);
}

/**
 * 安装引擎兜底中文字体（YaheiSub 子集）。必须在首次 Render 前调用；
 * 之后所有未知 font-family 的文本都落到该字体，中文不再出豆腐块。
 * @param {Uint8Array} regular @param {Uint8Array} bold
 */
export async function installSubsetFont(regular, bold) {
  await call("InstallSubsetFont", bytesToB64(regular), 400);
  await call("InstallSubsetFont", bytesToB64(bold), 700);
}

export async function info() {
  return JSON.parse(await call("Info"));
}
