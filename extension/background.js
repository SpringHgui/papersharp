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
      touchIdleAlarm();   // 一轮导出走到底：把回收时间推后，别让下次导出前宿主已经被关掉
      startDownload(msg).then(sendResponse, (e) => sendResponse({ ok: false, error: String(e?.message || e) }));
      return true;

    default:
      return false;
  }
}

/**
 * 拉起 offscreen 并**确认脚本真的跑起来了**才回 ok。
 *
 * 为什么必须回问一次 ping：chrome.offscreen.createDocument 只代表文档建好了，
 * offscreen.js 是 ESM（要先解析 papersharp/runtime.mjs → wasm/_framework/dotnet.js），
 * 消息监听者在脚本跑完之前根本不存在。产物没同步（缺 extension/papersharp/wasm 或 fonts）时
 * 这条 import 链 404，监听者永远不会出现 —— 不确认就回 ok，content 那边会把"假成功"
 * 当成已就绪，错误要等到传快照才暴露成一句看不出原因的「快照分片传输失败」。
 */
async function ensureOffscreen() {
  if (await hasOffscreen()) { touchIdleAlarm(); return await awaitHost(); }
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
      return await awaitHost();
    } catch (e) {
      const msg = String(e?.message || e);
      // 并发下已被别的请求建好：同样要确认脚本就位，别直接当成功
      if (/single offscreen|already/i.test(msg)) { touchIdleAlarm(); return await awaitHost(); }
      if (!/reasons/i.test(msg)) throw e;                                        // 参数以外的错误直接抛，别吞
    }
  }
  throw new Error("当前浏览器不接受任何已知的 offscreen reason，无法启动渲染宿主");
}

/** 等到 offscreen.js 的消息监听者出现（wasm 产物缺失时等不到，直接把原因说清楚） */
async function awaitHost(timeoutMs = 8000) {
  const deadline = Date.now() + timeoutMs;
  for (;;) {
    try {
      if (await hostPinged()) return;
    } catch { /* 监听者还没出现 */ }
    if (Date.now() >= deadline) break;
    await new Promise((r) => setTimeout(r, 200));
  }
  throw new Error("渲染宿主没有响应（offscreen 脚本未能加载）。多半是 wasm / 字体产物缺失，先跑：node extension/build.mjs --publish");
}

/** @returns {Promise<boolean>} 宿主是否在线（busy 时也已在线） */
async function hostPinged() {
  const res = await chrome.runtime.sendMessage({ k: "ping" });
  return !!res?.ok;
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
  if (await hostBusy()) { touchIdleAlarm(); return; }   // 引擎还在渲染：别把宿主从它脚下抽走，顺延一轮
  try { await chrome.offscreen.closeDocument(); } catch { /* 已关闭 */ }
});

/** offscreen.js 的 ping 会回报 busy；问不到就当空闲（宿主本就不在，关掉也是空操作） */
async function hostBusy() {
  try { return (await chrome.runtime.sendMessage({ k: "ping" }))?.busy === true; }
  catch { return false; }
}

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
