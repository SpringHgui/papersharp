// offscreen 文档脚本：唯一持有 .NET WASM 渲染运行时的地方。
//
// 协议（chrome.runtime.sendMessage）：
//   {k:'ping'}                                     → {ok, ready, busy}
//   {k:'push', id, seq, total, data}               → {ok}                     快照分片（content → 这里）
//   {k:'render', id, total, opts, filename}        → {ok, bytes} | {ok:false, error}
//   {k:'revoke', url}                              → —                        SW 下载结束后回收 blob URL
// 快照分片按 id 聚合，收齐后由 render 触发；渲染产物只在本文档里做成 Blob，
// 交给 SW 的只有一个 blob: URL（SW 里没有 URL.createObjectURL，实测不可用）。

import { renderRaw, installSubsetFont, info } from "./papersharp/runtime.mjs";

const CHUNK_KEEP = 8;   // 最多同时记住几份未完成的快照（防页面反复取消导出堆积内存）

/** @type {Promise<any>|null} */
let boot = null;
const jobs = new Map(); // id → { parts: Map<seq,string> }
const blobs = new Set();
let busy = false;

chrome.runtime.onMessage.addListener((msg, sender, sendResponse) => {
  switch (msg?.k) {
    case "ping":
      sendResponse({ ok: true, ready: !!boot && !busy, busy });
      return false;

    case "push": {
      let job = jobs.get(msg.id);
      if (!job) {
        if (jobs.size >= CHUNK_KEEP) jobs.delete(jobs.keys().next().value);
        jobs.set(msg.id, (job = { parts: new Map() }));
      }
      if (!job.parts.has(msg.seq)) job.parts.set(msg.seq, msg.data);
      sendResponse({ ok: true, got: job.parts.size });
      return false;
    }

    case "revoke":
      URL.revokeObjectURL(msg.url);
      blobs.delete(msg.url);
      return false;

    case "render":
      handleRender({ ...msg, tabId: sender?.tab?.id ?? msg.tabId }).then(sendResponse, (e) => sendResponse({ ok: false, error: String(e?.message || e) }));
      return true;

    default:
      return false;
  }
});

async function ensureBooted() {
  if (!boot) {
    boot = (async () => {
      // wasm 无系统字体：先把打包的雅黑子集装成引擎的兜底字体，
      // 否则网页里任何未注册 font-family 的中文都会出豆腐块。
      const [regular, bold] = await Promise.all([
        fetchAsset("fonts/YaheiSub-Regular.ttf"),
        fetchAsset("fonts/YaheiSub-Bold.ttf"),
      ]);
      await installSubsetFont(regular, bold);
      return info();
    })();
    boot.catch(() => (boot = null));   // 冷启动失败不留坏单例
  }
  return boot;
}

async function fetchAsset(path) {
  const res = await fetch(chrome.runtime.getURL(path));
  if (!res.ok) throw new Error(`字体加载失败 ${path}: HTTP ${res.status}`);
  return new Uint8Array(await res.arrayBuffer());
}

async function handleRender({ id, total, opts, filename, tabId }) {
  if (busy) throw new Error("引擎正忙，请等上一次导出结束");
  const job = jobs.get(id);
  if (!job) throw new Error("快照分片丢失，请重试");
  if (total != null && job.parts.size !== total) throw new Error(`快照分片不全（${job.parts.size}/${total}）`);
  busy = true;
  try {
    await ensureBooted();
    const snapshot = JSON.parse(joinParts(job));
    jobs.delete(id);

    const bytes = await renderRaw({
      html: snapshot.html,
      css: snapshot.css + pageRule(opts),
      format: opts.format,
      width: opts.width,
      scale: opts.scale,
      quality: opts.quality,
      pageSize: opts.pageSize,
      icoSize: opts.icoSize,
      animationTime: opts.animationTime,
    });

    const name = filename || filenameFor(snapshot.title, opts.format);
    const url = URL.createObjectURL(new Blob([bytes], { type: mimeOf(name) }));
    blobs.add(url);
    const sent = await chrome.runtime.sendMessage({ k: "download", id, url, filename: name, tabId, bytes: bytes.length });
    if (!sent?.ok) throw new Error(sent?.error || "下载提交失败");
    return { ok: true, bytes: bytes.length };
  } catch (e) {
    jobs.delete(id);
    return { ok: false, error: String(e?.message || e).replace(/^Error: /, "").slice(0, 400) };
  } finally {
    busy = false;
  }
}

function joinParts(job) {
  let out = "";
  for (let i = 0; i < job.parts.size; i++) out += job.parts.get(i) ?? "";
  return out;
}

function filenameFor(title, format) {
  const base = String(title || "").trim().slice(0, 60) || "page";
  return `${base}.${format === "jpeg" ? "jpg" : format}`;
}

/**
 * PDF 分页：快照里的盒尺寸是浏览器按视口宽度算好的（不重新回流），所以页面宽度直接取导出宽度，
 * 高度按纸张比例（A4 1:1.4142 / Letter 1:1.2941）推出 —— 既不留白裁切，也不改变版式。
 */
function pageRule(opts) {
  if (opts.format !== "pdf") return "";
  const w = Math.max(240, Math.min(4096, Number(opts.width) || 900));
  const ratio = String(opts.pageSize || "A4").toLowerCase() === "letter" ? 792 / 612 : 841.89 / 595.28;
  return `\n@page { size: ${Math.round(w)}px ${Math.round(w * ratio)}px; margin: 0px }`;
}

function mimeOf(filename) {
  const ext = String(filename).split(".").pop()?.toLowerCase();
  return ({
    pdf: "application/pdf", png: "image/png", jpg: "image/jpeg", jpeg: "image/jpeg",
    webp: "image/webp", svg: "image/svg+xml", ico: "image/x-icon",
  })[ext] || "application/octet-stream";
}
