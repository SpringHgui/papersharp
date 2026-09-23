// PaperSharp 导出 —— MV3 service worker：offscreen 生命周期 + 下载落地。
//
// 为什么渲染不放这里：.NET WASM 需要真实 document + ESM + wasm 实例化，SW 里没有 DOM，
// 官方姿势是 chrome.offscreen 开一个隐藏扩展页承载运行时（offscreen.js）。
// 这里只做两件事：① 首个请求到来时把 offscreen 拉起来（并挂空闲关闭闹钟）；
// ② 拿 offscreen 给的 blob: URL 触发 chrome.downloads，并把最终状态回推给发起导出的标签页。
//
// 注意：SW 里没有 URL.createObjectURL（实测 Edge/Chrome 153 报 not a function），
// 所以 Blob 在 offscreen 文档里建，字节不过消息通道，SW 只接一个 URL 字符串。

const OFFSCREEN_PATH = "offscreen.html";
const IDLE_ALARM = "papersharp:close-offscreen";
const IDLE_MINUTES = 3;

void chrome.runtime.onMessage.addListener(onMessage);

/** @returns {boolean} 是否保持消息通道异步打开 */
function onMessage(msg, sender, sendResponse) {
  switch (msg?.k) {
    case "ensure-offscreen":
      ensureOffscreen().then(() => sendResponse({ ok: true }), (e) => sendResponse({ ok: false, error: String(e?.message || e) }));
      return true;

    case "download":
      startDownload(msg).then(sendResponse, (e) => sendResponse({ ok: false, error: String(e?.message || e) }));
      return true;

    default:
      return false;
  }
}

async function ensureOffscreen() {
  if (await hasOffscreen()) return;
  const params = (reason) => ({
    url: OFFSCREEN_PATH,
    reasons: [reason],
    justification: "在本机 .NET WASM 里渲染网页快照为 PDF/PNG/JPEG/WebP/SVG/ICO",
  });
  // offscreen 的 reason 枚举各浏览器/各版本不一致（实测 Edge 153 已无 WASM_SCRIPTING，Chrome 116+ 有），
  // 按候选依次试到被接受为止，避免在某一版内核上直接报参数错。
  for (const reason of ["WASM_SCRIPTING", "WORKERS"]) {
    try {
      await chrome.offscreen.createDocument(params(reason));
      touchIdleAlarm();
      return;
    } catch (e) {
      const msg = String(e?.message || e);
      if (/single offscreen|already/i.test(msg)) { touchIdleAlarm(); return; }   // 并发下已被别的请求建好
      if (!/reasons/i.test(msg)) throw e;                                        // 参数以外的错误直接抛，别吞
    }
  }
  throw new Error("当前浏览器不接受任何已知的 offscreen reason，无法启动渲染宿主");
}

// MV3 里 chrome.runtime.getViews 已被移除，用 offscreen.hasDocument()（Chrome 116+）
async function hasOffscreen() {
  if (typeof chrome.offscreen.hasDocument === "function") return chrome.offscreen.hasDocument();
  return false;
}

function touchIdleAlarm() {
  chrome.alarms.create(IDLE_ALARM, { delayInMinutes: IDLE_MINUTES });
}

chrome.alarms.onAlarm.addListener(async (alarm) => {
  if (alarm.name !== IDLE_ALARM) return;
  try { await chrome.offscreen.closeDocument(); } catch { /* 已关闭 */ }
});

async function startDownload({ id, url, filename, tabId, bytes }) {
  const downloadId = await chrome.downloads.download({
    url,
    filename: safeFilename(filename),
    saveAs: false,
    conflictAction: "uniquify",
  });
  watch(downloadId, url, tabId, id, bytes);
  return { ok: true, downloadId };
}

/** 下载结束（完成/中断）→ 让 offscreen 回收 blob URL，并把结果回给发起导出的标签页。 */
function watch(downloadId, url, tabId, jobId, bytes) {
  const listener = (delta) => {
    if (delta.id !== downloadId) return;
    const state = delta.state?.current;
    if (!state || state === "in_progress") return;
    chrome.downloads.onChanged.removeListener(listener);
    const payload = { k: "download-state", id: jobId, ok: state === "complete", state, bytes, error: delta.error?.current };
    if (tabId != null) chrome.tabs.sendMessage(tabId, payload).catch(() => { /* 页面已关闭 */ });
    chrome.runtime.sendMessage({ k: "revoke", url }).catch(() => { /* offscreen 已关，URL 随文档一起回收 */ });
  };
  chrome.downloads.onChanged.addListener(listener);
}

function safeFilename(name) {
  const cleaned = String(name || "page")
    .replace(/[\\/:*?"<>|\u0000-\u001f]/g, "_")
    .replace(/^\.+/, "")
    .slice(0, 120);
  return cleaned || "page";
}
