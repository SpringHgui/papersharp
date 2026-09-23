// PaperSharp 导出 —— content script 入口：装配面板、跑快照、分片送 offscreen 渲染、等下载回执。
//
// 一条导出流水：
//   面板点「导出」 → ①（SW）确保 offscreen 在   → ②（本地）PSCapture 展平 DOM/CSS + 内联图片
//   → ③ 分片 sendMessage 给 offscreen           → ④ offscreen 用 .NET WASM 渲染，字节回传 SW 落地下载
//   → ⑤ SW 把 chrome.downloads 的最终状态推回本页标签 → 面板显示「已导出 / 失败」
//
// 页面里也可以程序化触发（同时是端到端测试的入口）：
//   window.postMessage({type:'papersharp:export', format:'png'}, '*')
//   → 收到 {type:'papersharp:export-result', ok, filename, bytes}

(function (global) {
  "use strict";

  const CHUNK = 1_000_000;          // 每条消息的字符数（chrome 消息上限 64MB，这里保守分片 + 顺便给进度条）
  const RENDER_TIMEOUT = 180_000;
  const DOWNLOAD_TIMEOUT = 90_000;

  const waiting = new Map();        // jobId → {resolve, reject, timer}
  let seq = 0;
  let booted = false;
  let lastSnapshot = null;

  main();

  async function main() {
    if (!global.document?.body) return;
    chrome.runtime.onMessage.addListener(onPush);
    await PSPanel.mount({ onExport });

    global.addEventListener("message", (e) => {
      if (e.source !== global || e.data?.type !== "papersharp:export") return;
      exportProgrammatically(e.data).catch((err) => console.warn("[PaperSharp]", err));
    });
  }

  function onPush(msg) {
    if (msg?.k !== "download-state") return;
    const job = waiting.get(msg.id);
    if (!job) return;
    clearTimeout(job.timer);
    waiting.delete(msg.id);
    if (msg.ok) job.resolve({ bytes: msg.bytes });
    else job.reject(new Error(msg.error ? `下载失败（${msg.error}）` : `下载未完成（${msg.state || "中断"}）`));
  }

  async function onExport(opts, report) {
    const jobId = `ps-${Date.now().toString(36)}-${seq++}`;

    if (!booted) {
      report("启动本机渲染引擎（首次约 2–5 秒）…", 4);
      const res = await chrome.runtime.sendMessage({ k: "ensure-offscreen" });
      if (!res?.ok) throw new Error(res?.error || "无法启动渲染宿主（offscreen）");
      booted = true;
    }

    report("抓取整页 DOM 与生效样式…", 10);
    const tCap = performance.now();
    const snap = await PSCapture.capture({
      inlineImages: opts.inlineImages,
      scaleToWidth: opts.width,          // 目标输出宽 ≠ 视口宽时整页等比缩放（不重新回流、不裁切）
      // PDF 要跨页回流：钉死只在 fixed/sticky/absolute 与 flex/grid 子女处停住，不往子树里传
      deepFreeze: opts.format !== "pdf",
      onProgress: (label, pct) => report(label, Math.min(60, pct)),
    });
    const captureMs = Math.round(performance.now() - tCap);
    report(`已展平 ${snap.stats.elements} 个元素 / ${snap.classes} 组样式，${snap.stats.images} 张图内联中…`, 62);

    lastSnapshot = snap;                       // --dump 用：端到端测试把快照落盘后离线复现/基准
    const downloadTicket = waitForDownload(jobId);
    try {
      const payload = JSON.stringify({ html: snap.html, css: snap.css, title: snap.title });
      const tPush = performance.now();
      await pushChunks(payload, jobId, report);
      const transportMs = Math.round(performance.now() - tPush);
      report("引擎渲染中…", 82);
      const rendered = await withTimeout(
        sendToOffscreen({ k: "render", id: jobId, total: chunksOf(payload).length, opts, filename: opts.filename }),
        RENDER_TIMEOUT, "渲染超时（页面可能过大，试着关掉「内联图片」或缩小宽度）");
      if (!rendered?.ok) throw new Error(rendered?.error || "渲染失败");
      report("保存到下载文件夹…", 92);
      const { bytes } = await downloadTicket;
      return {
        filename: opts.filename, bytes, warnings: snap.warnings,
        // 各段耗时：定位「慢」到底慢在展平、传快照、还是引擎渲染
        timing: {
          captureMs, walkMs: snap.timing?.walk ?? 0, imageMs: snap.timing?.image ?? 0, computedStyles: snap.timing?.computed ?? 0,
          transportMs, renderMs: rendered.renderMs ?? 0, bootMs: rendered.bootMs ?? 0,
          elements: snap.stats.elements, classes: snap.classes, payloadKB: Math.round(payload.length / 1024),
          htmlKB: Math.round(snap.html.length / 1024), cssKB: Math.round(snap.css.length / 1024),
        },
      };
    } catch (e) {
      waiting.delete(jobId);
      downloadTicket.catch(() => { });   // 已失败：别留下悬空 rejection
      throw e;
    }
  }

  function withTimeout(promise, ms, message) {
    let timer;
    return Promise.race([
      promise.finally(() => clearTimeout(timer)),
      new Promise((_, reject) => { timer = setTimeout(() => reject(new Error(message)), ms); }),
    ]);
  }

  function chunksOf(text) {
    const out = [];
    for (let i = 0; i < text.length; i += CHUNK) out.push(text.slice(i, i + CHUNK));
    return out.length ? out : [""];
  }

  async function pushChunks(payload, id, report) {
    const parts = chunksOf(payload);
    for (let i = 0; i < parts.length; i++) {
      const res = await sendToOffscreen({ k: "push", id, seq: i, total: parts.length, data: parts[i] });
      if (!res?.ok) throw new Error(res?.error || "快照分片传输失败");
      report(`传输快照 ${i + 1}/${parts.length}（${PSPanel.mb(payload.length)}）…`, 62 + Math.round(18 * ((i + 1) / parts.length)));
    }
  }

  function sendToOffscreen(msg) {
    return chrome.runtime.sendMessage(msg).catch((e) => {
      throw new Error(String(e?.message || e).includes("receiving end") ? "渲染宿主未就绪，请重试" : String(e?.message || e));
    });
  }

  function waitForDownload(id) {
    return new Promise((resolve, reject) => {
      const timer = setTimeout(() => { waiting.delete(id); reject(new Error("下载回执超时（浏览器可能被拦截，检查下载页面）")); }, DOWNLOAD_TIMEOUT);
      waiting.set(id, { resolve, reject, timer });
    });
  }

  async function exportProgrammatically(detail = {}) {
    const state = PSPanel.state || {};
    const opts = {
      format: ["pdf", "png", "jpeg", "webp", "svg", "ico"].includes(detail.format) ? detail.format : "png",
      width: Number(detail.width) || Math.round(document.documentElement.clientWidth || 1024),
      scale: Number(detail.scale) || 1,
      quality: Number(detail.quality) || 90,
      pageSize: detail.pageSize || "A4",
      icoSize: Number(detail.icoSize) || 256,
      animationTime: detail.animationTime == null ? undefined : Number(detail.animationTime),
      inlineImages: detail.inlineImages !== false,
      filename: PSPanel.sanitize(detail.filename || document.title || "page") + "." + (detail.format === "jpeg" ? "jpg" : (detail.format || "png")),
    };
    void state;
    try {
      const result = await onExport(opts, (label, pct) => global.postMessage({ type: "papersharp:progress", label, pct }, "*"));
      global.postMessage({ type: "papersharp:export-result", ok: true, ...result, dump: detail.wantDump ? lastSnapshot : undefined }, "*");
      return result;
    } catch (e) {
      global.postMessage({ type: "papersharp:export-result", ok: false, error: String(e?.message || e), filename: opts.filename }, "*");
      throw e;
    }
  }

})(self);
