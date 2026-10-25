// Playground 的浏览器侧：把用户输入装进 iframe、量出每个元素的矩形、并把两个窗格滚动绑在一起。
//
// 关键约束是 measure() 里的路径算法必须与 StyleResolver.Walk 逐字同构，否则 Δ 表会把
// A 元素的偏差记到 B 元素头上。三处刻意对齐，改动时请同步：
//   1) 不成盒的兄弟（script/style/br/template…）照样占 nth-of-type 的号，只是自己不出现；
//   2) svg / input 不下钻（引擎里它们是替换元素，内部节点不成盒）；
//   3) display:none 整棵子树两边都不记（引擎 ToBoxTree 直接跳过）。
// 坐标系：都减去 body 的边框盒原点，单位 CSS px —— 与 Renderer.RenderRasterWithBoxes 同一套。

/** 引擎 NonRenderingTags + br：不成盒、不占位，但占 nth-of-type 的号。 */
const NO_BOX = new Set(["STYLE", "SCRIPT", "TEMPLATE", "META", "LINK", "TITLE", "BASE", "NOSCRIPT", "HEAD", "BR"]);

/** 替换元素：自己成盒，但内部节点不进盒树。 */
const NO_DESCEND = new Set(["SVG", "INPUT"]);

/**
 * 表格行组：引擎里是透传容器（表格算法直接摆 tr/td），行组盒自身没有几何。
 * 核心库已把它们从盒列表剔除，这里也必须同样跳过——否则它们只出现在浏览器一侧，
 * 假偏差没消掉，只是从「漂移 1235px」改叫「浏览器独有 6 个」。仍然要下钻，tr/td 是真的。
 */
const PASS_THROUGH_DISPLAY = new Set(["table-row-group", "table-header-group", "table-footer-group"]);

function pathOf(parentPath, tag, seq) {
  return `${parentPath}/${tag.toLowerCase()}[${seq}]`;
}

/** 与 StyleResolver 的 tagSeq 同口径：按父节点的**全部**元素子节点数同标签序。 */
function childSeqMap() {
  const seen = new Map();
  return (tagName) => {
    const n = (seen.get(tagName) ?? 0) + 1;
    seen.set(tagName, n);
    return n;
  };
}

function measure(iframe) {
  const doc = iframe.contentDocument;
  const win = iframe.contentWindow;
  const body = doc?.body;
  if (!body) return [];

  const bodyRect = body.getBoundingClientRect();
  const out = [];

  const visit = (el, path) => {
    const cs = win.getComputedStyle(el);
    // tagName 大小写陷阱：HTML 元素是 SVG 命名空间外的大写标签，而 <svg> 及其内部一律小写
    // （el.tagName === "svg"）。直接 has("SVG") 判不中，就会下钻进 svg，把 rect/circle/path/text
    // 量成「浏览器独有盒」——引擎侧 svg 是替换元素、内部不成盒，这些全是假差异。
    const tn = el.tagName.toUpperCase();
    if (cs.display === "none") return;            // 引擎也不成盒，两边都不记
    if (!PASS_THROUGH_DISPLAY.has(cs.display)) {  // 行组不 emit，但要继续下钻找 tr/td
      const r = el.getBoundingClientRect();
      out.push({
        domPath: path,
        tag: el.tagName.toLowerCase(),
        x: r.left + win.scrollX - bodyRect.left,
        y: r.top + win.scrollY - bodyRect.top,
        w: r.width,
        h: r.height,
      });
    }
    if (NO_DESCEND.has(tn)) return;
    const seq = childSeqMap();
    for (const kid of el.children) {
      const n = seq(kid.tagName);
      if (NO_BOX.has(kid.tagName.toUpperCase())) continue;   // 号已占，人不出现
      visit(kid, pathOf(path, kid.tagName, n));
    }
  };

  visit(body, "body[1]");
  return out;
}

/** 等布局稳定：两帧 + 字体就绪。load 事件时 @font-face 往往还没落地，量早了全是假偏差。 */
async function settle(doc, win) {
  await new Promise((r) => win.requestAnimationFrame(() => win.requestAnimationFrame(r)));
  try { await doc.fonts?.ready; } catch { /* 老内核没有 FontFaceSet，忽略 */ }
  // 动画定格：引擎出的是 animationTime 上的静态一帧（Playground 取初始帧），浏览器却在实时播放。
  // 不钉回 t=0，被动画改过的盒每一帧都在报漂移，且数值随刷新时机随机变——比的就不是同一份输入了。
  try { for (const a of doc.getAnimations()) { a.pause(); a.currentTime = 0; } } catch { /* 老内核无 Web Animations，忽略 */ }
  await new Promise((r) => win.requestAnimationFrame(() => win.requestAnimationFrame(r)));
}

/**
 * 装载预览文档并按目标宽度量出全部矩形。
 * iframe 高度设成文档全高，让它自身不出滚动条 —— 滚动交给外层窗格，
 * 两个窗格才在同一个 scrollTop 上比同一块内容。
 */
export async function loadPreview(iframe, srcdoc, widthPx) {
  iframe.style.width = `${Math.max(1, Math.round(widthPx))}px`;
  const loaded = new Promise((resolve) => {
    const on = () => { iframe.removeEventListener("load", on); resolve(); };
    iframe.addEventListener("load", on);
  });
  iframe.srcdoc = srcdoc;
  await loaded;

  const doc = iframe.contentDocument;
  const win = iframe.contentWindow;
  if (!doc?.body) return { rects: [], height: 0 };

  await settle(doc, win);
  const height = Math.max(1, Math.round(doc.documentElement.scrollHeight));
  iframe.style.height = `${height}px`;
  await settle(doc, win);                          // 改高会触发一次回流，量之前再稳一次

  alignPreview(iframe, iframe.closest("[data-mode]")?.dataset?.mode);

  return { rects: measure(iframe), height };
}

/**
 * 叠加对齐：引擎图按 `left:-BodyX; top:-BodyY` 平移，把**引擎 body 原点**摆到舞台 (0,0)；
 * iframe 若不跟着平移，浏览器正文就仍留在它自己的 body margin 处，两层整体错开一个 margin。
 * 实测 m9 bodyX=bodyY=28（body{margin:28px}）→ 叠看重影左移 28px，而差表两边都以 body 归一化，
 * 22 个盒全报 0 —— 差表看不见这个错，只有叠图看得见。
 * 只在叠加/半透明模式下平移：并排模式 iframe 有自己的可见边框与留白，挪了会露出空隙。
 * mode 由调用方显式传入而不是回读 DOM：切换视图模式的按钮只改 Blazor 状态，事件处理里
 * data-mode 可能还没落到 DOM；且平移若只在 loadPreview 算一次，「并排加载 → 切叠加」就永远
 * 错开一个 body margin（m5 实测 36px）。
 */
export function alignPreview(iframe, mode) {
  const doc = iframe?.contentDocument;
  if (!doc?.body) return;
  const br = doc.body.getBoundingClientRect();
  iframe.style.transform = mode === "side" || !br.left && !br.top
    ? ""
    : `translate(${-Math.round(br.left)}px, ${-Math.round(br.top)}px)`;
}

/**
 * 两个窗格绝对同步滚动（同一个 scrollTop 才叫「同一块内容」；按比例同步会在两边高度不同时
 * 把不同区域摆到一起，而高度差本身就是这里要看的差异）。
 * 返回带 dispose() 的对象：Blazor 侧按 IJSObjectReference 持有，组件销毁时必须解绑，
 * 否则电路断了、DOM 还在，监听会一直往已经没人看的节点上写 scrollTop。
 */
export function linkScroll(...els) {
  const live = els.filter(Boolean);
  let busy = false;
  const handler = (self) => () => {
    if (busy) return;
    busy = true;
    for (const el of live) if (el !== self) el.scrollTop = self.scrollTop;
    requestAnimationFrame(() => { busy = false; });
  };
  const bound = live.map((el) => {
    const h = handler(el);
    el.addEventListener("scroll", h, { passive: true });
    return [el, h];
  });
  return { dispose: () => bound.forEach(([el, h]) => el.removeEventListener("scroll", h)) };
}

/**
 * PDF 悬浮窗的拖拽：按住标题条就能把窗推到任意位置，与底下的两窗并存。
 *
 * 三处要紧的口径：
 *  - 起手先把 CSS 的 right/bottom 定位换算成 left/top。只写 left 而留着 right，
 *    两边同时生效会把窗体横向拉满（position:fixed 的默认行为，不是 bug）。
 *  - 标题条上的控件（纸张档、下载、关闭）不参与拖拽：pointerdown 落在它们身上直接放行。
 *  - 夹在视口内且标题条至少留 32px 可见，否则一推就再也抓不回来。
 *
 * 与 linkScroll 同一套生命周期：返回带 dispose() 的对象，组件负责解绑。
 */
export function makeDraggable(handle, panel) {
  if (!handle || !panel) return { dispose() {} };

  const clamp = (v, lo, hi) => (lo > hi ? lo : Math.min(Math.max(v, lo), hi));
  let start = null;

  const onDown = (e) => {
    if (e.button !== 0 || e.target.closest("select,button,a,input")) return;
    const r = panel.getBoundingClientRect();
    panel.style.left = `${r.left}px`;
    panel.style.top = `${r.top}px`;
    panel.style.right = "auto";
    panel.style.bottom = "auto";
    start = { dx: e.clientX - r.left, dy: e.clientY - r.top };
    handle.classList.add("dragging");
    try { handle.setPointerCapture(e.pointerId); } catch { /* 指针已抬起：不捕获也能拖，只是移出窗体就断 */ }
  };

  const onMove = (e) => {
    if (!start) return;
    panel.style.left = `${clamp(e.clientX - start.dx, 60 - panel.offsetWidth, innerWidth - 60)}px`;
    panel.style.top = `${clamp(e.clientY - start.dy, 0, innerHeight - 32)}px`;
  };

  const onUp = () => {
    start = null;
    handle.classList.remove("dragging");
  };

  handle.addEventListener("pointerdown", onDown);
  handle.addEventListener("pointermove", onMove);
  handle.addEventListener("pointerup", onUp);
  handle.addEventListener("pointercancel", onUp);
  return {
    dispose: () => {
      handle.removeEventListener("pointerdown", onDown);
      handle.removeEventListener("pointermove", onMove);
      handle.removeEventListener("pointerup", onUp);
      handle.removeEventListener("pointercancel", onUp);
    },
  };
}

/**
 * 窗格边界拖拽：编辑区 ↔ 比对区 ↔ 明细栏，以及并排时下窗（引擎/浏览器之间）。
 *
 * 三条口径：
 *  - 全程只写 .split 上的 CSS 自定义属性，一趟 SignalR 都不发。窗格尺寸本来不参与渲染
 *    （iframe 宽由 loadPreview 钉成排版宽，引擎图按 CSS px 摆），服务端不需要知道它；
 *    而每次 pointermove 等一个往返的话，拖起来就是橡皮筋。
 *  - 事件挂在 .split 上做委托：明细栏与行分隔条随视图模式增删节点，绑在单条上每次切视图都得重挂。
 *  - 夹取按「两条相邻窗格之和」算，所以拖到一边必然给另一边留出最小尺寸，
 *    不会出现整栏被推没、只剩一条 1px 边框找不回来的情况。
 */
export function makeResizable(root) {
  if (!root) return { dispose() { } };

  /** key = 分隔条上的 data-resize；edge 指出这次改的是相邻两栏里的哪一条的宽度。 */
  const SPEC = {
    editor: { axis: "x", edge: "start", a: ".editor", b: ".stage", min: [320, 320], prop: "--w-editor", keep: [".report", 0.24] },
    report: { axis: "x", edge: "end", a: ".stage", b: ".report", min: [320, 260], prop: "--w-report" },
    pane: { axis: "y", edge: "start", a: ".pane-engine", b: ".pane-browser", min: [120, 120], prop: "--h-engine" },
  };
  const clamp = (v, lo, hi) => (lo > hi ? lo : Math.min(Math.max(v, lo), hi));
  const at = (axis, rect) => (axis === "x" ? rect.left : rect.top);
  const end = (axis, rect) => (axis === "x" ? rect.right : rect.bottom);
  const size = (axis, rect) => (axis === "x" ? rect.width : rect.height);
  let drag = null;

  const onDown = (e) => {
    const bar = e.target.closest?.("[data-resize]");
    const spec = bar && SPEC[bar.dataset.resize];
    if (!spec || e.button !== 0) return;
    const a = root.querySelector(spec.a), b = root.querySelector(spec.b);
    if (!a || !b) return;
    const ra = a.getBoundingClientRect(), rb = b.getBoundingClientRect();
    const total = size(spec.axis, ra) + size(spec.axis, rb);
    // keep：轴向上还占着一格、又不会跟着收缩的轨道（明细栏）。它没渲染出来时按 .split 的
    // grid-template 默认档预留（盒级差异下是 24%，见 Playground.razor.css）——否则在并排里把
    // 编辑区拖到顶，一切到「盒级差异」明细栏挤进来，比对区只剩一条缝。
    const kept = spec.keep && root.querySelector(spec.keep[0]);
    const keep = spec.keep ? (kept ? size(spec.axis, kept.getBoundingClientRect())
                                   : Math.max(340, Math.round(root.clientWidth * spec.keep[1]))) : 0;
    const [minA, minB] = spec.min;
    drag = {
      spec, bar,
      anchor: spec.edge === "end" ? end(spec.axis, rb) : at(spec.axis, ra),
      lo: (spec.edge === "end" ? minB : minA),
      hi: total - (spec.edge === "end" ? minA : minB) - keep,
    };
    bar.classList.add("dragging");
    root.classList.add("resizing");
    try { bar.setPointerCapture(e.pointerId); } catch { /* 指针已抬起：不捕获也能拖，只是移出手柄就断 */ }
    e.preventDefault();                         // 顺手掐掉拖出去的文字选区
  };

  const onMove = (e) => {
    if (!drag) return;
    const { axis, edge } = drag.spec;
    const p = axis === "x" ? e.clientX : e.clientY;
    const want = edge === "end" ? drag.anchor - p : p - drag.anchor;
    root.style.setProperty(drag.spec.prop, `${Math.round(clamp(want, drag.lo, drag.hi))}px`);
  };

  const onUp = () => {
    if (!drag) return;
    drag.bar.classList.remove("dragging");
    root.classList.remove("resizing");
    drag = null;
  };

  root.addEventListener("pointerdown", onDown);
  addEventListener("pointermove", onMove);
  addEventListener("pointerup", onUp);
  addEventListener("pointercancel", onUp);
  return {
    dispose: () => {
      root.removeEventListener("pointerdown", onDown);
      removeEventListener("pointermove", onMove);
      removeEventListener("pointerup", onUp);
      removeEventListener("pointercancel", onUp);
    },
  };
}
