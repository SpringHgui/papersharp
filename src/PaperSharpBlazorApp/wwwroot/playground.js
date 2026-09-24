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
    if (NO_DESCEND.has(el.tagName)) return;
    const seq = childSeqMap();
    for (const kid of el.children) {
      const n = seq(kid.tagName);
      if (NO_BOX.has(kid.tagName)) continue;      // 号已占，人不出现
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
  return { rects: measure(iframe), height };
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
