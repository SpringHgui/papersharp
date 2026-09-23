// .NET WASM 运行时懒加载单例：首次调用 render/registerFont 时才启动。
// 每个进程一个常驻运行时，多次渲染无冷启动开销（首次 ~0.6s，之后每次 <100ms）。

import { dotnet } from "../wasm/_framework/dotnet.js";

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

async function call(method, ...args) {
  const api = await getApi();
  const raw = await api[method](...args);
  const res = JSON.parse(raw);
  if (!res.ok) throw new Error(`papersharp: ${res.error}`);
  return res.data;
}

/** @param {{html:string,css:string,format?:string,width?:number,scale?:number,quality?:number,pageSize?:string,icoSize?:number}} req */
export async function renderRaw(req) {
  const b64 = await call("Render", JSON.stringify(req));
  return b64ToBytes(b64);
}

/** @param {string} family @param {Uint8Array} data @param {number} weight */
export async function registerFontRaw(family, data, weight) {
  let b64;
  if (typeof Buffer !== "undefined") b64 = Buffer.from(data).toString("base64");
  else {
    let bin = "";
    for (let i = 0; i < data.length; i++) bin += String.fromCharCode(data[i]);
    b64 = btoa(bin);
  }
  return call("RegisterFont", family, b64, weight);
}

export async function info() {
  return JSON.parse(await call("Info"));
}
