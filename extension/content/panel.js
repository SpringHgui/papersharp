// PaperSharp 导出 —— 右上角按钮与面板（closed shadow DOM，页面样式互不影响）
//
// 面板只负责「选格式 + 收参数 + 显示进度/结果」，真正的快照与渲染在 main.js / offscreen。
// 选项持久化用 chrome.storage.local（无 storage 权限时自动退化为内存态）。

(function (global) {
  "use strict";

  const FORMATS = [
    { id: "pdf", label: "PDF", hint: "矢量 · 分页 · 书签 + 链接" },
    { id: "png", label: "PNG", hint: "整页长图 · 可透明底" },
    { id: "jpeg", label: "JPEG", hint: "整页照片 · 有质量参数" },
    { id: "webp", label: "WebP", hint: "整页 · 比 JPEG 更小" },
    { id: "svg", label: "SVG", hint: "矢量 · 文字可选中" },
    { id: "ico", label: "ICO", hint: "取左上方形区域做图标" },
  ];

  const LIMITS = [
    "引擎不实现 transform：靠 translate 做位移/居中的元素会回到未变换位置",
    "引擎不实现 overflow：hidden 用 clip-path 近似，极端情况仍可能溢出",
    "引擎不实现 background-size/position：背景图按原尺寸从左上角铺开",
    "盒尺寸取浏览器当前视口下的 used value：改「输出宽度」是整页等比缩放，不会重新回流（响应式重排引擎不做）",
    "<iframe>、跨文件 SVG 雪碧图、视频帧、网页字体（woff2）不导出；未注册字体的中文用打包雅黑子集兜底",
    "斜体（font-style）与列表符号（::marker）不在引擎支持范围内，导出会丢失",
  ];

  const STYLE = `
:host { all: initial; }
* { box-sizing: border-box; font-family: system-ui, -apple-system, "Segoe UI", "Microsoft YaHei", sans-serif; }
.wrap { position: fixed; top: 16px; right: 16px; z-index: 2147483647; display: flex; flex-direction: column; align-items: flex-end; gap: 8px; }
.fab { display: flex; align-items: center; gap: 6px; height: 34px; padding: 0 13px 0 10px; border: 1px solid rgba(15,23,42,.14);
  border-radius: 999px; background: #ffffff; color: #0f172a; font-size: 13px; font-weight: 600; cursor: pointer;
  box-shadow: 0 6px 20px rgba(15,23,42,.16); }
.fab:hover { background: #f8fafc; }
.fab svg { width: 15px; height: 15px; display: block; }
.card { width: 300px; background: #ffffff; border: 1px solid rgba(15,23,42,.1); border-radius: 12px;
  box-shadow: 0 18px 44px rgba(15,23,42,.2); padding: 12px; color: #0f172a; font-size: 12px; }
.card[hidden] { display: none; }
.head { display: flex; align-items: center; justify-content: space-between; margin-bottom: 8px; }
.title { font-size: 12px; font-weight: 700; letter-spacing: .02em; color: #334155; }
.x { border: 0; background: transparent; color: #94a3b8; font-size: 16px; line-height: 1; cursor: pointer; padding: 2px 4px; }
.chips { display: flex; flex-wrap: wrap; gap: 6px; margin-bottom: 6px; }
.chip { border: 1px solid rgba(15,23,42,.12); background: #f8fafc; color: #334155; border-radius: 8px;
  padding: 5px 9px; font-size: 12px; font-weight: 600; cursor: pointer; }
.chip[aria-pressed="true"] { background: #1d4ed8; border-color: #1d4ed8; color: #fff; }
.hint { color: #64748b; min-height: 15px; margin-bottom: 8px; }
.row { display: flex; align-items: center; justify-content: space-between; gap: 8px; margin-bottom: 6px; }
.row label { color: #475569; }
.row input[type=number], .row select, .row input[type=text] { width: 132px; padding: 4px 6px; border: 1px solid rgba(15,23,42,.16);
  border-radius: 6px; font-size: 12px; color: #0f172a; background: #fff; }
.row input[type=range] { width: 132px; }
.row input[type=text] { width: 176px; }
.check { display: flex; align-items: center; gap: 6px; color: #475569; margin-bottom: 8px; }
.go { width: 100%; margin-top: 4px; padding: 8px 10px; border: 0; border-radius: 9px; background: #1d4ed8; color: #fff;
  font-size: 13px; font-weight: 700; cursor: pointer; }
.go:hover { background: #1e40af; }
.go[disabled] { background: #94a3b8; cursor: progress; }
.status { margin-top: 8px; color: #475569; line-height: 1.5; word-break: break-all; }
.status.err { color: #b91c1c; }
.status.ok { color: #15803d; }
.bar { height: 3px; margin-top: 6px; border-radius: 2px; background: #e2e8f0; overflow: hidden; }
.bar i { display: block; height: 100%; width: 0; background: #1d4ed8; transition: width .18s ease-out; }
.warn { margin-top: 8px; padding: 6px 8px; border-radius: 8px; background: #fff7ed; color: #9a3412; line-height: 1.5; }
.warn[hidden], .bar[hidden] { display: none; }
details { margin-top: 10px; color: #64748b; }
summary { cursor: pointer; color: #475569; }
details ul { margin: 6px 0 0; padding-left: 16px; line-height: 1.6; }
`;

  let mounted = null;

  /** @param {{onExport:(opts:any)=>Promise<any>, probe?:()=>Promise<any>}} handlers */
  async function mount(handlers) {
    if (mounted) return mounted;
    if (!document.body) return null;

    const host = document.createElement("div");
    host.setAttribute("data-papersharp-ui", "");
    const root = host.attachShadow({ mode: "closed" });
    const sheet = new CSSStyleSheet();
    sheet.replaceSync(STYLE);
    root.adoptedStyleSheets = [sheet];

    const state = {
      format: "pdf",
      width: 0,
      scale: 2,
      quality: 90,
      pageSize: "A4",
      icoSize: 256,
      animationTime: "",
      inlineImages: true,
      filename: sanitize(document.title || "page"),
    };
    Object.assign(state, await loadOptions());
    if (!state.width) state.width = Math.round(document.documentElement.clientWidth || 1024);

    root.innerHTML = `
      <div class="wrap">
        <button class="fab" part="fab" aria-haspopup="dialog" aria-expanded="false">
          <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">
            <path d="M12 3v12"/><path d="M7 10l5 5 5-5"/><path d="M4 19h16"/>
          </svg><span>导出</span>
        </button>
        <div class="card" hidden role="dialog" aria-label="PaperSharp 导出">
          <div class="head"><span class="title">PaperSharp 导出</span><button class="x" title="收起">×</button></div>
          <div class="chips"></div>
          <div class="hint"></div>
          <div class="opts"></div>
          <button class="go">导出</button>
          <div class="bar" hidden><i></i></div>
          <div class="status">引擎在本机渲染，页面内容不外发。</div>
          <div class="warn" hidden></div>
          <details><summary>已知限制（与引擎能力一致）</summary><ul>${LIMITS.map((l) => `<li>${l}</li>`).join("")}</ul></details>
        </div>
      </div>`;

    const els = {
      fab: root.querySelector(".fab"),
      card: root.querySelector(".card"),
      chips: root.querySelector(".chips"),
      hint: root.querySelector(".hint"),
      opts: root.querySelector(".opts"),
      go: root.querySelector(".go"),
      bar: root.querySelector(".bar"),
      fill: root.querySelector(".bar i"),
      status: root.querySelector(".status"),
      warn: root.querySelector(".warn"),
    };

    for (const f of FORMATS) {
      const b = document.createElement("button");
      b.className = "chip";
      b.textContent = f.label;
      b.dataset.id = f.id;
      b.addEventListener("click", () => { state.format = f.id; render(); saveOptions(state); });
      els.chips.appendChild(b);
    }

    els.fab.addEventListener("click", () => { const open = els.card.hidden; els.card.hidden = !open; els.fab.setAttribute("aria-expanded", String(open)); if (open) render(); });
    root.querySelector(".x").addEventListener("click", () => { els.card.hidden = true; els.fab.setAttribute("aria-expanded", "false"); });
    els.go.addEventListener("click", () => run());
    document.addEventListener("keydown", (e) => {
      if (e.key === "Escape" && !els.card.hidden) els.card.hidden = true;
    }, true);

    render();
    document.body.appendChild(host);
    mounted = { host, root, els, state, render, progress, finish, fail };
    return mounted;

    function render() {
      const f = FORMATS.find((x) => x.id === state.format);
      els.hint.textContent = f ? f.hint : "";
      for (const chip of els.chips.children) chip.setAttribute("aria-pressed", String(chip.dataset.id === state.format));

      const rows = [];
      rows.push(fieldNumber("输出宽度 px", "width", state, 280, 4096));
      if (["png", "jpeg", "webp", "ico"].includes(state.format)) rows.push(fieldSelect("像素倍率", "scale", state, [[1, "1×"], [2, "2×"], [3, "3×"]]));
      if (["jpeg", "webp"].includes(state.format)) rows.push(fieldRange("质量", "quality", state, 30, 100));
      if (state.format === "pdf") rows.push(fieldSelect("分页比例", "pageSize", state, [["A4", "A4 (1:1.414)"], ["Letter", "Letter (1:1.294)"]]));
      if (state.format === "ico") rows.push(fieldSelect("图标尺寸", "icoSize", state, [[16, "16"], [32, "32"], [48, "48"], [64, "64"], [128, "128"], [256, "256"]]));
      rows.push(fieldNumber("动画取帧秒数", "animationTime", state, 0, 600, { allowEmpty: true, title: "留空=按元素自身样式；填 0.5 则取 @keyframes 第 0.5 秒那一帧" }));
      rows.push(fieldCheck("内联图片为 data URI", "inlineImages", state));
      rows.push(fieldText("文件名", "filename", state));

      els.opts.replaceChildren(...rows);
      for (const row of rows) {
        const input = row.querySelector("input,select");
        if (!input) continue;
        input.addEventListener("change", () => { saveOptions(state); });
        input.addEventListener("input", () => { if (input.type === "range") saveOptions(state); });
      }
    }

    function fieldNumber(label, key, st, min, max, extra = {}) {
      const row = document.createElement("div"); row.className = "row";
      const lab = document.createElement("label"); lab.textContent = label;
      const inp = document.createElement("input"); inp.type = "number";
      inp.min = min; inp.max = max; inp.step = "any"; inp.value = st[key] ?? "";
      if (extra.title) inp.title = extra.title;
      inp.addEventListener("change", () => {
        const v = inp.value.trim();
        st[key] = v === "" ? "" : Math.min(max, Math.max(min, Number(v) || min));
        if (v !== "" && String(st[key]) !== v) inp.value = st[key];
      });
      row.append(lab, inp);
      return row;
    }

    function fieldSelect(label, key, st, choices) {
      const row = document.createElement("div"); row.className = "row";
      const lab = document.createElement("label"); lab.textContent = label;
      const sel = document.createElement("select");
      for (const [value, text] of choices) {
        const o = document.createElement("option"); o.value = value; o.textContent = text; sel.appendChild(o);
      }
      sel.value = String(st[key]);
      sel.addEventListener("change", () => { st[key] = typeof st[key] === "number" ? Number(sel.value) : sel.value; });
      row.append(lab, sel);
      return row;
    }

    function fieldRange(label, key, st, min, max) {
      const row = document.createElement("div"); row.className = "row";
      const lab = document.createElement("label"); lab.textContent = label;
      const inp = document.createElement("input"); inp.type = "range"; inp.min = min; inp.max = max; inp.value = st[key];
      const out = document.createElement("span"); out.textContent = String(st[key]); out.style.width = "26px"; out.style.textAlign = "right";
      inp.addEventListener("input", () => { st[key] = Number(inp.value); out.textContent = inp.value; });
      row.append(lab, inp, out);
      return row;
    }

    function fieldCheck(label, key, st) {
      const lab = document.createElement("label"); lab.className = "check";
      const inp = document.createElement("input"); inp.type = "checkbox"; inp.checked = !!st[key];
      inp.addEventListener("change", () => { st[key] = inp.checked; });
      lab.append(inp, document.createTextNode(label));
      return lab;
    }

    function fieldText(label, key, st) {
      const row = document.createElement("div"); row.className = "row";
      const lab = document.createElement("label"); lab.textContent = label;
      const inp = document.createElement("input"); inp.type = "text"; inp.value = st[key];
      inp.addEventListener("change", () => { st[key] = sanitize(inp.value) || "page"; });
      row.append(lab, inp);
      return row;
    }

    async function run() {
      els.go.disabled = true;
      els.warn.hidden = true;
      els.bar.hidden = false;
      progress("准备快照…", 2);
      try {
        const opts = {
          format: state.format,
          width: Number(state.width) || Math.round(document.documentElement.clientWidth || 1024),
          scale: Number(state.scale) || 1,
          quality: Number(state.quality) || 90,
          pageSize: state.pageSize,
          icoSize: Number(state.icoSize) || 32,
          animationTime: state.animationTime === "" ? undefined : Number(state.animationTime),
          inlineImages: state.inlineImages,
          filename: sanitize(state.filename) + "." + (state.format === "jpeg" ? "jpg" : state.format),
        };
        const result = await handlers.onExport(opts, (label, pct) => progress(label, pct));
        finish(result);
      } catch (e) {
        fail(e);
      } finally {
        els.go.disabled = false;
      }
    }

    function progress(label, pct) {
      els.status.className = "status";
      els.status.textContent = label;
      els.fill.style.width = `${Math.max(2, Math.min(100, pct))}%`;
    }

    function finish(result) {
      els.fill.style.width = "100%";
      els.bar.hidden = true;
      els.status.className = "status ok";
      els.status.textContent = `已导出 ${result.filename}（${mb(result.bytes)}）→ 浏览器下载文件夹`;
      const warnings = result.warnings || [];
      if (warnings.length) {
        els.warn.hidden = false;
        els.warn.textContent = "提示：" + warnings.join("；");
      }
    }

    function fail(e) {
      els.bar.hidden = true;
      els.status.className = "status err";
      els.status.textContent = "导出失败：" + String(e?.message || e);
    }
  }

  function sanitize(name) {
    return String(name || "page").replace(/[\\/:*?"<>|\u0000-\u001f]/g, "_").replace(/^\.+/, "").trim().slice(0, 80) || "page";
  }

  function mb(bytes) {
    if (!bytes) return "0 KB";
    return bytes > 1024 * 1024 ? (bytes / 1024 / 1024).toFixed(2) + " MB" : Math.max(1, Math.round(bytes / 1024)) + " KB";
  }

  async function loadOptions() {
    try {
      const stored = await chrome.storage.local.get("papersharp:options");
      return stored?.["papersharp:options"] || {};
    } catch { return {}; }
  }

  function saveOptions(state) {
    try { chrome.storage.local.set({ "papersharp:options": state }); } catch { /* 无 storage 权限：内存态即可 */ }
  }

  // 供 main.js 驱动：进度、成功、失败（这三条都发生在 mount 之后，用模块级句柄转给面板）
  function progress(label, pct) { mounted?.progress(label, pct); }
  function finish(result) { mounted?.finish(result); }
  function fail(err) { mounted?.fail(err); }

  global.PSPanel = { mount, sanitize, mb, progress, finish, fail, open: () => mounted, get state() { return mounted?.state; } };
})(self);
