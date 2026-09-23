// PaperSharp 导出 —— 网页快照器（content script，isolated world，普通脚本、无构建步骤）
//
// 为什么走「计算样式展平」而不是把网页原始 CSS 传给引擎：
//   PaperSharp 是离线渲染引擎，只实现 CSS 子集（无 @layer / @container / CSS 嵌套 / @media /
//   overflow / transform / 网络图）。真实网站的 CSS 直接丢进去，现代语法会被静默丢弃 → 整页裸样式。
//   反过来让**浏览器**把级联、媒体查询、@layer、var()/calc()、阴影 DOM 全解析完，我们只把
//   「每个元素最终生效的那点属性」写成扁平的 `.ps-N{...}` 规则，正好落在引擎吃得下的子集上 ——
//   顺带相对 URL、currentColor、used 宽高都由浏览器算好了，签名去重后 CSS 只有几十 KB。
//
// 代价（如实记录，不粉饰）：
//   · 盒尺寸是当前视口下的 used value，改导出宽度不会重新回流（只做整页等比缩放）；
//   · 引擎无 transform：靠 translate 居中/位移的元素回到未变换位置；
//   · 引擎无 overflow：hidden/clip/auto 用已实现的 `clip-path: inset(0 round r)` 近似；
//   · 引擎无 background-size/position/repeat：背景图按原尺寸从盒子左上角铺开；
//   · 引擎无 font-style/list marker：斜体与列表符号丢失；
//   · 引擎量拉丁文本比浏览器宽约 1%（Skia 用 hinting 后的整数字距）→ 钉死盒子偶尔多出一行、压到下一块。
//
// 「钉死」(absolute freeze)：把浏览器算好的矩形直接搬给引擎 —— 块级盒一律 position:absolute +
//   left/top/width/height（相对父盒 padding box），父盒不是定位元素就补一条后发的 position:relative。
//   长图格式整页钉死（版式快照）；PDF 只钉 fixed/sticky/absolute 与 flex/grid 子女，其余留常规流以保住分页回流。
//   表格只钉位置与宽度（display:table 与高度交给引擎），行内片段不钉。
//
// 用法：const snap = await PSCapture.capture({ inlineImages: true, deepFreeze: true, onProgress })

(function (global) {
  "use strict";

  const UI_ATTR = "data-papersharp-ui";
  const MAX_ELEMENTS = 30000;
  const MAX_DEPTH = 80;
  const MAX_IMG_BYTES = 10 * 1024 * 1024;      // 单图上限
  const MAX_INLINE_BYTES = 80 * 1024 * 1024;   // 内联图片总预算
  const FETCH_TIMEOUT = 12000;
  const FETCH_PARALLEL = 8;

  // 可继承属性：与父元素同值就不重复下发（几千个元素靠这个压体积）
  const INHERITED = new Set([
    "color", "font-family", "font-size", "font-weight", "line-height", "letter-spacing",
    "text-align", "text-transform", "white-space", "word-break", "text-overflow", "direction", "text-wrap",
  ]);
  const SKIP_TAGS = new Set(["SCRIPT", "STYLE", "LINK", "META", "TITLE", "BASE", "NOSCRIPT", "TEMPLATE", "HEAD"]);
  const VOID_TAGS = new Set(["img", "br", "hr", "wbr"]);
  const SIDES = ["top", "right", "bottom", "left"];

  const probe = new CSSStyleSheet();   // 借浏览器颜色解析器把 oklch/color() 等归一成 rgb()

  // ── 入口 ──────────────────────────────────────────────────────────────
  async function capture(opts = {}) {
    const o = {
      inlineImages: opts.inlineImages !== false,
      scaleToWidth: Number(opts.scaleToWidth) || 0,
      onProgress: typeof opts.onProgress === "function" ? opts.onProgress : () => {},
    };
    const ctx = {
      opts: o,
      // 钉死是否在子树内继续传递：整页长图（PNG/JPEG/WebP/ICO/SVG）要像素级还原 → 传；
      // PDF 要跨页回流 → 只在「引擎必然排错」的 fixed/sticky/absolute 与 flex/grid 子女上停住。
      deep: opts.deepFreeze !== false,
      rules: [],
      bySignature: new Map(),
      classSeq: 0,
      imgCache: new Map(),
      inflight: 0,
      waiters: [],
      inlinedBytes: 0,
      counted: 0,
      stats: { elements: 0, skipped: 0, iframes: 0, shadow: 0, images: 0, imagesFailed: 0, canvas: 0, pseudo: 0 },
      t: { walk: 0, image: 0, computed: 0 },
      positioned: new Set(),   // 已经补过 position:relative 的类名（钉死子元素需要定位参照）
      notes: new Map(),
    };
    const body = document.body;
    if (!body) throw new Error("当前页面没有 <body>，无法导出");
    const viewportWidth = Math.max(280, Math.min(4096, Math.round(document.documentElement.clientWidth || 1024)));
    // 目标输出宽 ≠ 视口宽时，不重新回流（引擎按我们下发的 px 排版），而是把浏览器算好的
    // 所有 used px 按比例缩放 —— 等价于「整页等比缩放到这个宽度」，不裁切。
    ctx.k = opts.scaleToWidth ? Math.max(0.05, Math.min(20, Number(opts.scaleToWidth) / viewportWidth)) : 1;

    o.onProgress("等待图片加载", 5);
    await ensureImagesLoaded(body);

    o.onProgress("展平样式", 15);
    const htmlCS = getComputedStyle(document.documentElement);
    const bodyCS = getComputedStyle(body);
    // body 是引擎里唯一的根盒：给它确定的宽高 + 定位参照 —— 被「钉死」的绝对定位子树才有参照系，
    // 画布高度也不会因为绝对定位元素不贡献流内高度而塌成一小截。
    const bodyRect = body.getBoundingClientRect();
    const docW = Math.max(viewportWidth, Math.round(document.documentElement.scrollWidth || viewportWidth));
    const docH = Math.max(1, Math.round(document.documentElement.scrollHeight || bodyRect.height));
    ctx.rules.push(`body{${await flatten(bodyCS, htmlCS, ctx, body)};position:relative;width:${px(docW, ctx)};height:${px(docH, ctx)};box-sizing:border-box}`);
    await emitPseudo(body, bodyCS, "before", ctx, "body");
    await emitPseudo(body, bodyCS, "after", ctx, "body");

    const tWalk = performance.now();
    // 每一层子元素都要知道「相对谁摆位」：父盒 padding box 左上角 + 父类名 + 父是否已是定位元素。
    // 根参照是 body —— 上面刚给它钉了 position:relative 与文档宽高。
    const rootRef = {
      origin: { x: bodyRect.left + num(bodyCS.borderLeftWidth), y: bodyRect.top + num(bodyCS.borderTopWidth) },
      cls: null,
      positioned: true,
    };
    const html = await serializeChildren(body, bodyCS, ctx, 0, rootRef);
    await drainImages(ctx);
    ctx.t.walk = Math.round(performance.now() - tWalk);
    liftRootBackground(ctx, htmlCS, bodyCS);

    return {
      html: html || "<p>(空页面)</p>",
      css: ctx.rules.join("\n"),
      title: document.title || location.hostname,
      viewportWidth,
      contentHeight: Math.max(1, Math.round(document.documentElement.scrollHeight)),
      classes: ctx.classSeq,
      warnings: [...ctx.notes.values()],
      stats: ctx.stats,
      timing: { walk: ctx.t.walk, image: Math.round(ctx.t.image), computed: ctx.t.computed },
    };
  }

  // ── 递归序列化 ────────────────────────────────────────────────────────
  // depth = 该层子元素自己的深度（body 的子女是 1），ref = 子元素的「摆位参照系」：
  //   { origin 父 padding box 左上角, cls 父类名, positioned 父在产物里是否真是定位元素 }
  async function serializeChildren(parent, parentCS, ctx, depth, ref) {
    let out = "";
    for (const node of parent.childNodes) {
      if (node.nodeType === 3) { out += escapeText(node.data); continue; }
      if (node.nodeType !== 1) continue;
      out += await serializeElement(node, parentCS, ctx, depth + 1, ref);
    }
    return out;
  }

  async function serializeElement(el, parentCS, ctx, depth, ref) {
    if (depth > MAX_DEPTH) { note(ctx, "depth", `部分元素超过 ${MAX_DEPTH} 层嵌套，已截断`); return ""; }
    if (ctx.counted++ > MAX_ELEMENTS) { note(ctx, "cap", `元素数超过 ${MAX_ELEMENTS}，后半部分未导出`); return ""; }
    if (el.hasAttribute(UI_ATTR) || el.closest(`[${UI_ATTR}]`)) return "";   // 扩展自己的导出按钮
    const tag = el.tagName.toUpperCase();
    if (SKIP_TAGS.has(tag)) return "";

    ctx.t.computed++;
    const cs = getComputedStyle(el);
    if (cs.display === "none") { ctx.stats.skipped++; return ""; }
    if (cs.visibility === "hidden" || cs.visibility === "collapse") { ctx.stats.skipped++; return ""; }   // 引擎不认 visibility → 整棵子树丢掉
    const rect = el.getBoundingClientRect();
    if (rect.width < 1 && rect.height < 1) { ctx.stats.skipped++; return ""; }

    ctx.stats.elements++;

    // 「钉死」判定：引擎复现不了的定位关系（fixed/sticky/absolute）与弹性、网格排布，
    // 直接用浏览器算好的矩形把元素摆回原位，避免整页塌陷错位。
    // 行内片段不钉（钉了多行内联会挤成一块）；表格家族不钉（引擎的表格排版 + 跨页续表要用得上）。
    const pos = cs.getPropertyValue("position");
    const disp = cs.getPropertyValue("display");
    const parentDisp = parentCS ? parentCS.getPropertyValue("display") : "";
    const flowParent = /flex|grid/.test(parentDisp);
    // deepHit = 整页钉死（长图模式）：块级盒一律按浏览器矩形摆位，等于给页面拍一张可缩放的版式快照。
    //   只要还有一个祖先留在常规流里，引擎给它算的高度和浏览器差多少，它下面所有钉死内容就整体偏移多少
    //   （实测 Bootstrap 顶栏少算 ~50px，正文整片上移）。PDF 反过来：要跨页回流，只在
    //   「引擎必然排错」的 fixed/sticky/absolute 与 flex/grid 子女处钉，其余交给引擎排版。
    const deepHit = !!ctx.deep && !parentDisp.startsWith("table");
    const isInlineBox = disp.startsWith("inline") && disp !== "inline-block" && disp !== "inline-flex" && disp !== "inline-grid";
    const isTable = tag === "TABLE" || disp.startsWith("table");
    const hasUrlBg = /url\(/i.test(cs.getPropertyValue("background-image"));
    // 表格在长图模式下只钉「位置 + 宽度」，高度留给引擎算：钉死高度会废掉自动行高，
    // 而完全不钉又会让它从父盒顶部开始流（实测整张表跳到页首压住标题）。
    const posOnly = isTable && !!ctx.deep;
    // fixed 元素锚在视口上，而 DOM 父盒的**文档**坐标 = 视口 rect + 滚动量。
    // 不补这一刀，用户滚动后再导出会把固定导航栏钉到滚动位置那么远的地方。
    const ax = pos === "fixed" ? ref.origin.x + window.scrollX : ref.origin.x;
    const ay = pos === "fixed" ? ref.origin.y + window.scrollY : ref.origin.y;
    const frozen = ref && ref.origin && depth > 0 && !isInlineBox && (!isTable || posOnly)
      && (pos === "fixed" || pos === "sticky" || pos === "absolute" || flowParent || deepHit)
      ? { left: rect.left - ax, top: rect.top - ay, width: rect.width, height: posOnly ? 0 : rect.height, keepDisplay: posOnly }
      : null;

    const special = await replaceSpecial(el, tag, cs, rect, ctx, frozen);
    if (special) return special;

    if (tag === "SLOT") return serializeAssigned(el, parentCS, ctx, depth, ref);

    const outTag = el.tagName.toLowerCase();
    const decls = await flatten(cs, parentCS, ctx, el, frozen);
    const cls = classFor(decls, ctx);
    if (frozen) ensurePositioning(ctx, ref);
    const attrs = keepAttrs(el, tag, cls);
    let inner;

    // 子元素的定位参照 = 本元素 padding box 左上角（被钉死的子树按这个原点摆位）。
    // positioned 必须按**产物里**的定位算：非钉死的 fixed/sticky 会被改写成 static，那就不是包含块了。
    const childRef = {
      origin: { x: rect.left + num(cs.borderLeftWidth), y: rect.top + num(cs.borderTopWidth) },
      cls,
      positioned: !!frozen || pos === "relative" || pos === "absolute" || (pos === "static" && hasUrlBg),
    };

    if (VOID_TAGS.has(outTag)) return `<${outTag} ${attrs}>`;

    if (el.shadowRoot) {
      ctx.stats.shadow++;
      inner = await serializeChildren(el.shadowRoot, cs, ctx, depth, childRef);   // 开放 shadow root：展平内部
    } else if (tag === "SVG") {
      return serializeSvg(el, cls, rect, ctx);
    } else {
      // url 背景 → 合成成绝对定位的 <img>（引擎不支持 background-image: url()），排在子节点前 = 压在内容下面
      inner = (await backgroundLayers(el, cs, ctx, rect)) + (await serializeChildren(el, cs, ctx, depth, childRef));
    }

    await emitPseudo(el, cs, "before", ctx, "." + cls);
    await emitPseudo(el, cs, "after", ctx, "." + cls);
    return `<${outTag} ${attrs}>${inner}</${outTag}>`;
  }

  async function serializeAssigned(slot, parentCS, ctx, depth, ref) {
    let out = "";
    for (const n of slot.assignedNodes ? slot.assignedNodes({ flatten: true }) : []) {
      if (n.nodeType === 3) out += escapeText(n.data);
      else if (n.nodeType === 1) out += await serializeElement(n, parentCS, ctx, depth, ref);
    }
    return out;
  }

  // ── 特殊元素：图片 / canvas / 媒体 / 嵌入 / 表单 ──────────────────────
  // frozen：该元素已被「钉死」——替换元素走内联 style 摆位（不进 flatten），所以把矩形直接拼进 box。
  async function replaceSpecial(el, tag, cs, rect, ctx, frozen) {
    const pin = frozen ? `position:absolute;left:${px(frozen.left, ctx)};top:${px(frozen.top, ctx)};` : "";
    const box = `${pin}width:${px(rect.width, ctx)};height:${px(rect.height, ctx)}`;

    const asImage = async (src) => {
      const cls = classFor(await flattenImageBox(cs, ctx), ctx);
      const data = src && ctx.opts.inlineImages ? await imageData(src, ctx) : "";
      if (src && ctx.opts.inlineImages && !data) ctx.stats.imagesFailed++;
      if (src) ctx.stats.images++;
      return `<img class="${cls}" src="${data || ""}" style="${box}">`;
    };

    switch (tag) {
      case "IMG": return asImage(currentImageSrc(el));
      case "PICTURE": { const img = el.querySelector("img"); return asImage(img ? currentImageSrc(img) : ""); }
      case "CANVAS": {
        ctx.stats.canvas++;
        let src = "";
        try { src = el.toDataURL("image/png"); } catch { note(ctx, "canvas", "画布像素被跨源污染（tainted），只导出占位盒"); }
        return src ? `<img class="${classFor(await flattenImageBox(cs, ctx), ctx)}" src="${src}" style="${box}">`
                   : `<div class="${classFor(await flatten(cs, null, ctx, el) + ";" + box, ctx)}"></div>`;
      }
      case "VIDEO": {
        const poster = el.getAttribute("poster");
        if (poster) return asImage(absolutize(poster));
        note(ctx, "media", "视频/音频未导出（引擎不做流媒体解码），保留尺寸占位");
        return `<div class="${classFor(await flatten(cs, null, ctx, el) + ";" + box, ctx)}"></div>`;
      }
      case "AUDIO":
        note(ctx, "media", "视频/音频未导出（引擎不做流媒体解码），保留尺寸占位");
        return "";
      case "IFRAME": case "FRAME": case "OBJECT": case "EMBED":
        ctx.stats.iframes++;
        note(ctx, "iframe", `<iframe>/嵌入文档属跨文档内容，未导出（${ctx.stats.iframes} 个），保留尺寸占位`);
        return `<div class="${classFor(await flatten(cs, null, ctx, el) + ";" + box, ctx)}"></div>`;
      case "INPUT": case "TEXTAREA": case "SELECT": case "BUTTON": {
        const cls = classFor(await flatten(cs, null, ctx, el) + ";white-space:pre-wrap", ctx);
        const styled = ["INPUT", "SELECT", "TEXTAREA"].includes(tag) || !!frozen;
        return `<span class="${cls}"${styled ? ` style="${box}"` : ""}>${escapeText(controlText(el, tag))}</span>`;
      }
      default: return null;
    }
  }

  function controlText(el, tag) {
    const type = (el.getAttribute("type") || "text").toLowerCase();
    if (type === "password") return "•".repeat(Math.min(String(el.value || "").length, 24));
    if (type === "checkbox" || type === "radio") return el.checked ? "√" : "";
    if (tag === "SELECT") return el.selectedOptions?.[0]?.textContent || "";
    if (tag === "BUTTON") return el.textContent || "";
    if (tag === "TEXTAREA") return el.value || "";
    // 空输入框留白会看不出那是个搜索框：拿 placeholder 兜底（浏览器渲染时也是这么显示的）
    return el.value || el.getAttribute("placeholder") || "";
  }

  function currentImageSrc(el) {
    const src = el.currentSrc || el.getAttribute("src") || "";
    if (!src) return "";
    return src.startsWith("data:") || src.startsWith("blob:") ? src : absolutize(src);
  }

  // ── 样式展平（只发引擎认得的属性，见文件头）─────────────────────────
  async function flatten(cs, parentCS, ctx, el, frozen) {
    const out = [];
    const push = async (prop, value) => {
      if (value == null || value === "") return;
      // 继承去重必须比「缩放前」的原始值，缩放交给这里做最后一次 sv()：
      // 反过来（调用方先缩放）会让子级值永远不等于父级原值，k≠1 时整页继承属性逐个重发。
      if (parentCS && INHERITED.has(prop) && parentCS.getPropertyValue(prop) === value) return;
      out.push(`${prop}:${sv(value, ctx)}`);
    };
    const raw = (p) => cs.getPropertyValue(p);
    const numv = (p) => num(raw(p));

    // 显示类型：引擎没有 contents/flow-root/list-item，退化处理
    let display = raw("display");
    if (display === "contents" || display === "flow-root" || display === "list-item") display = "block";
    if (display === "inline-flex") display = "flex";
    if (display === "inline-grid") display = "grid";
    out.push(`display:${display}`);

    const position = raw("position");
    const parentFlow = /flex|grid/.test((parentCS && parentCS.getPropertyValue("display")) || "");
    const w = numv("width"), h = numv("height");
    const isInline = display.startsWith("inline") && display !== "inline-block";
    const clips = isClipping(raw("overflow-x")) || isClipping(raw("overflow-y"));

    if (frozen) {
      // 钉死：位置与尺寸一律用浏览器算好的矩形（margin 已含在矩形里，不必再发）。
      // 引擎复现不了 flex/grid 的真实排布，也不认 fixed/sticky，靠流内布局必然整片错位。
      // height=0 = 只钉位置（表格）：只发 left/top/width，行高与续表交给引擎，display 也必须留着 table 别退化。
      out.push(`display:${frozen.keepDisplay ? display : "block"};position:absolute;left:${px(frozen.left, ctx)};top:${px(frozen.top, ctx)};width:${px(frozen.width, ctx)};box-sizing:border-box`
        + (frozen.height ? `;height:${px(frozen.height, ctx)}` : ""));
      const z = raw("z-index");
      if (z !== "auto") out.push(`z-index:${z}`);
      // padding 照发：背景与边框画在 padding box 上，钉住的是 border box
      for (const side of SIDES) {
        const p = numv(`padding-${side}`);
        if (p) out.push(`padding-${side}:${px(p, ctx)}`);
      }
    } else {
      out.push(`position:${position === "fixed" || position === "sticky" ? "static" : position}`);
      if (position === "absolute" || position === "relative") {
        for (const side of SIDES) {
          const v = raw(side);
          if (v && v !== "auto") out.push(`${side}:${sv(v, ctx)}`);
        }
      }
      if (position === "absolute") out.push(`z-index:${raw("z-index")}`);
      if (raw("float") !== "none") out.push(`float:${raw("float")}`);
      if (raw("clear") !== "none") out.push(`clear:${raw("clear")}`);

      // 尺寸取舍（都是实测出来的坑）：
      // · 页面的 min/max 先发，烘焙出的 width/height 后发 —— 后写规则覆盖，别让 computed min-height:0 把下限吃掉；
      // · 宽度发 used value，但「≈父内容宽」（CSS 里 width:auto 撑满）的不发，让引擎按新容器宽重排；
      // · flex/grid 子项不烘焙宽高（它们走上面 frozen 分支，除非父是表格）；
      // · 高度只钉死替换元素/绝对定位/需要裁切/子元素全被抽出常规流的盒子；内容撑高的块容器改发 min-height。
      for (const p of ["min-width", "max-width", "min-height", "max-height"]) {
        const v = raw(p);
        if (v && v !== "auto" && v !== "none" && !v.startsWith("fill") && !v.startsWith("max-content") && !v.startsWith("fit")) out.push(`${p}:${sv(v, ctx)}`);
      }
      if (w > 0 && !isInline && !parentFlow && !fillsParent(w, parentCS)) out.push(`width:${px(w, ctx)}`);
      if (h > 0 && !parentFlow) {
        const inFlow = hasInFlowChildren(el, ctx);
        if (isReplacedTag(el) || position === "absolute" || clips || (hasBlockChildren(el, ctx) && !inFlow)) out.push(`height:${px(h, ctx)}`);
        // 内容撑高的块容器：把浏览器算出的高度当**下限**而不是钉死 —— 引擎排版若需要更高就让它长，
        // 否则 flex/grid 换行数一旦不同，钉死高度会让后面的内容整片重叠（实测卡片行会压住下一节）。
        else if (hasBlockChildren(el, ctx)) out.push(`min-height:${px(h, ctx)}`);
      }
      for (const side of SIDES) {
        const m = numv(`margin-${side}`), p = numv(`padding-${side}`);
        if (m) out.push(`margin-${side}:${px(m, ctx)}`);
        if (p) out.push(`padding-${side}:${px(p, ctx)}`);
      }
    }

    const bw = SIDES.map((s) => numv(`border-${s}-width`));
    if (bw.some((v) => v > 0)) {
      SIDES.forEach((s, i) => out.push(`border-${s}-width:${px(bw[i], ctx)}`));
      const color = normColor(raw("border-top-color"));
      if (color) out.push(`border-color:${color}`);
    }
    for (const corner of ["top-left", "top-right", "bottom-right", "bottom-left"]) {
      const r = firstNum(raw(`border-${corner}-radius`));
      if (r > 0) out.push(`border-${corner}-radius:${px(r, ctx)}`);
    }

    if (!frozen) {
      for (const p of ["flex-direction", "flex-wrap", "justify-content", "align-items", "align-content", "align-self",
        "gap", "grid-template-columns", "grid-template-rows", "grid-column", "grid-row",
        "flex-grow", "flex-shrink", "flex-basis", "border-collapse", "border-spacing"]) {
        const v = raw(p);
        if (!v || v === "none") continue;
        if (p === "gap" && v === "normal") continue;
        if (p === "flex-grow" && v === "0") continue;
        if (p === "flex-shrink" && v === "1") continue;
        out.push(`${p}:${sv(normalizeColorsIn(v), ctx)}`);
      }
    }

    const color = normColor(raw("color"));
    if (color) await push("color", color);
    for (const p of ["font-family", "font-size", "font-weight", "line-height", "letter-spacing",
      "text-align", "text-transform", "white-space", "word-break", "text-overflow", "text-wrap", "direction"]) {
      let v = raw(p);
      if (!v) continue;
      if (p === "text-transform" && v === "none") continue;
      if (p === "direction" && v !== "rtl") continue;
      if (p === "text-align" && (v === "start" || v === "left")) continue;
      if (p === "text-overflow" && v !== "ellipsis") continue;
      if (p === "text-wrap" && v !== "balance") continue;
      await push(p, normalizeColorsIn(v));
    }
    const deco = raw("text-decoration-line");
    if (deco && deco !== "none") out.push(`text-decoration:${deco} ${normColor(raw("text-decoration-color")) || color || "currentColor"}`);

    const bg = normColor(raw("background-color"));
    if (bg && bg !== "rgba(0, 0, 0, 0)") out.push(`background-color:${bg}`);
    const bgi = raw("background-image");
    if (bgi && bgi !== "none") {
      // 引擎完全没有 background-image: url()（实测只认渐变）→ url 层改由合成 <img> 承担，
      // 这里只保留渐变层，免得把几百 KB 的 data URI 塞进 CSS 却什么也不画。
      const gradients = commaSplit(bgi).filter((layer) => !/url\(/i.test(layer));
      if (gradients.length) out.push(`background-image:${gradients.join(",")}`);
      if (urlLayers(bgi).length && !frozen && position === "static") out.push("position:relative");   // 合成背景图的定位锚
    }
    if (raw("background-clip") === "text") out.push("background-clip:text");

    const op = parseFloat(raw("opacity"));
    if (Number.isFinite(op) && op < 1) out.push(`opacity:${r3(op)}`);
    const sh = raw("box-shadow");
    if (sh && sh !== "none") out.push(`box-shadow:${sv(normalizeColorsIn(sh), ctx)}`);
    const filter = raw("filter");
    if (filter && filter !== "none") out.push(`filter:${sv(normalizeColorsIn(filter), ctx)}`);
    const bdf = raw("backdrop-filter");
    if (bdf && bdf !== "none") out.push(`backdrop-filter:${sv(normalizeColorsIn(bdf), ctx)}`);
    const blend = raw("mix-blend-mode");
    if (blend && blend !== "normal") out.push(`mix-blend-mode:${blend}`);
    const mask = raw("mask-image") || raw("-webkit-mask-image");
    if (mask && mask !== "none") out.push(`mask-image:${await inlineUrls(mask, ctx)}`);

    // 引擎无 overflow：靠已实现的 clip-path 裁掉溢出（不带 round —— 实测带 round 的 inset 解析不了）
    if (clips) {
      out.push("clip-path:inset(0px)");
    } else {
      const cp = raw("clip-path");
      if (cp && cp !== "none") out.push(`clip-path:${sv(cp, ctx)}`);
    }

    return out.join(";");
  }

  async function flattenImageBox(cs, ctx) {
    const parts = [];
    const bw = num(cs.getPropertyValue("border-top-width"));
    if (bw) {
      for (const s of SIDES) parts.push(`border-${s}-width:${px(bw, ctx)}`);
      parts.push(`border-color:${normColor(cs.getPropertyValue("border-top-color"))}`);
    }
    const radius = ["top-left", "top-right", "bottom-right", "bottom-left"]
      .map((c) => firstNum(cs.getPropertyValue(`border-${c}-radius`)));
    if (radius.some((v) => v > 0)) {
      const [tl, tr, br, bl] = radius.map((v) => r2(v * (ctx.k || 1)));
      parts.push(`border-top-left-radius:${tl}px`, `border-top-right-radius:${tr}px`,
        `border-bottom-right-radius:${br}px`, `border-bottom-left-radius:${bl}px`);
    }
    const op = parseFloat(cs.getPropertyValue("opacity"));
    if (Number.isFinite(op) && op < 1) parts.push(`opacity:${r3(op)}`);
    const shadow = cs.getPropertyValue("box-shadow");
    if (shadow && shadow !== "none") parts.push(`box-shadow:${normalizeColorsIn(shadow)}`);
    const ov = [cs.getPropertyValue("overflow-x"), cs.getPropertyValue("overflow-y")];
    if (ov.some(isClipping)) parts.push("clip-path:inset(0px)");
    void ctx;
    return parts.join(";");
  }

  function isReplacedTag(el) { return !!el && ["IMG", "SVG", "CANVAS", "VIDEO", "IFRAME"].includes(el.tagName.toUpperCase()); }

  /** 被钉死的元素需要一个定位参照；父元素本身没定位就用后发规则补上（同类名同属性，后者覆盖前者）。
   *  给类补 position:relative 不会挪动该类的任何盒子，只是让它成为子元素的包含块。 */
  function ensurePositioning(ctx, ref) {
    if (!ref || ref.positioned || !ref.cls || ctx.positioned.has(ref.cls)) return;
    ctx.positioned.add(ref.cls);
    ctx.rules.push(`.${ref.cls}{position:relative}`);
  }

  function isClipping(v) { return v === "hidden" || v === "clip" || v === "auto" || v === "scroll"; }

  /** background-image 里的 url 层（CSS 先写的层在上 → 合成 img 时倒序输出） */
  function urlLayers(value) {
    if (!value || value === "none") return [];
    return commaSplit(value)
      .filter((layer) => /url\(/i.test(layer))
      .map((layer) => (layer.match(/url\(\s*(["']?)([^'")]+)\1\s*\)/i) || [])[2])
      .filter(Boolean);
  }

  /** 顶层逗号切分：括号里的逗号不算（linear-gradient(90deg,a,b)） */
  function commaSplit(value) {
    const out = [];
    let depth = 0, cur = "";
    for (const ch of String(value)) {
      if (ch === "(") depth++;
      if (ch === ")") depth--;
      if (ch === "," && depth === 0) { out.push(cur.trim()); cur = ""; continue; }
      cur += ch;
    }
    if (cur.trim()) out.push(cur.trim());
    return out;
  }

  /** 引擎不支持 background-image: url()（实测只认渐变）→ 合成成绝对定位的 <img> 铺在内容下面。
   *  尺寸直接给到父盒的 used 尺寸（不用 100%：父高若是 min-height，% 会退化成图片原始尺寸而溢出）。 */
  async function backgroundLayers(el, cs, ctx, rect) {
    const urls = urlLayers(cs.getPropertyValue("background-image"));
    if (!urls.length || !ctx.opts.inlineImages) return "";
    const cls = classFor("display:block;position:absolute;left:0px;top:0px", ctx);
    const size = `position:absolute;left:0;top:0;width:${px(rect.width, ctx)};height:${px(rect.height, ctx)}`;
    let out = "";
    for (const u of [...urls].reverse()) {
      const data = await imageData(u.startsWith("data:") || u.startsWith("blob:") ? u : absolutize(u), ctx);
      if (data) out += `<img class="${cls}" src="${data}" style="${size}">`;
    }
    return out;
  }

  /** 元素宽度 ≈ 父内容宽 → 说明是 CSS `width:auto` 撑满，不烘焙宽度，交给引擎按新容器宽重排 */
  function fillsParent(w, parentCS) {
    const pw = parentContentWidth(parentCS);
    return pw > 0 && Math.abs(pw - w) <= 1;
  }

  function parentContentWidth(cs) {
    if (!cs) return 0;
    let w = num(cs.getPropertyValue("width"));
    if (!w) return 0;
    if (cs.getPropertyValue("box-sizing") === "border-box") {
      w -= num(cs.getPropertyValue("padding-left")) + num(cs.getPropertyValue("padding-right"))
         + num(cs.getPropertyValue("border-left-width")) + num(cs.getPropertyValue("border-right-width"));
    }
    return w;
  }

  function hasBlockChildren(el, ctx) {
    if (!el) return false;
    for (const c of el.children) {
      ctx.t.computed++;
      const d = getComputedStyle(c).display;
      if (!d.startsWith("inline") || d === "inline-block") return true;
    }
    return false;
  }

  /** 是否有参与常规流的子元素（全被 absolute/fixed 抽走时，盒子高度是结构性的，可以直接烘焙） */
  function hasInFlowChildren(el, ctx) {
    if (!el) return false;
    for (const c of el.children) {
      ctx.t.computed++;
      const cs = getComputedStyle(c);
      if (cs.display === "none" || cs.visibility === "hidden") continue;
      if (cs.position === "absolute" || cs.position === "fixed") continue;
      return true;
    }
    return false;
  }

  async function emitPseudo(el, cs, which, ctx, selector) {
    ctx.t.computed += 1;
    let ps;
    try { ps = getComputedStyle(el, `::${which}`); } catch { return; }
    if (!ps) return;
    let content = ps.getPropertyValue("content");
    if (!content || content === "none" || content === "normal") return;
    if (ps.getPropertyValue("display") === "none" || ps.getPropertyValue("width") === "0px" && !content) return;
    content = await inlineUrls(content, ctx);
    const decls = [`content:${content}`];
    const add = (p) => {
      let v = ps.getPropertyValue(p);
      if (!v || v === "none" || v === "auto" || v === "normal" || v === "0px" || v === "rgba(0, 0, 0, 0)") return;
      if (p === "background-color" || p === "color") v = normColor(v);
      decls.push(`${p}:${sv(v, ctx)}`);   // 伪元素跟正文同一把 k，否则 k≠1 时它会比周围大一档
    };
    for (const p of ["display", "position", "top", "left", "width", "height", "box-sizing", "color", "font-size",
      "background-color", "border-top-width", "border-color", "border-top-left-radius"]) add(p);
    // 伪元素的背景同理：引擎不画 url() 背景（伪元素也没法合成 <img>），只保留渐变层，别白发 data URI
    const pbg = ps.getPropertyValue("background-image");
    if (pbg && pbg !== "none") {
      const gradients = commaSplit(pbg).filter((layer) => !/url\(/i.test(layer));
      if (gradients.length) decls.push(`background-image:${sv(gradients.join(","), ctx)}`);
    }
    for (const p of ["counter-reset", "counter-increment"]) {
      const v = cs.getPropertyValue(p);
      if (v && v !== "none") decls.push(`${p}:${v}`);
    }
    ctx.stats.pseudo++;
    ctx.rules.push(`${selector}::${which}{${decls.join(";")}}`);
  }

  // ── 类名去重 ──────────────────────────────────────────────────────────
  function classFor(decls, ctx) {
    if (!decls) return "ps-empty";
    let cls = ctx.bySignature.get(decls);
    if (!cls) {
      cls = `ps-${ctx.classSeq++}`;
      ctx.bySignature.set(decls, cls);
      ctx.rules.push(`.${cls}{${decls}}`);
    }
    return cls;
  }

  function keepAttrs(el, tag, cls) {
    const parts = [`class="${cls}"`];
    if (tag === "A") {
      const href = el.getAttribute("href");
      if (href && !href.startsWith("#")) parts.push(`href="${escapeAttr(absolutize(href))}"`);
    }
    if (tag === "TD" || tag === "TH") {
      const col = el.getAttribute("colspan"); if (col) parts.push(`colspan="${col}"`);
      const row = el.getAttribute("rowspan"); if (row) parts.push(`rowspan="${row}"`);
    }
    if (tag === "COL" || tag === "COLGROUP") {
      const span = el.getAttribute("span"); if (span) parts.push(`span="${span}"`);
    }
    return parts.join(" ");
  }

  function serializeSvg(el, cls, rect, ctx) {
    // 引擎会画内联 <svg>，但只吃 Presentation 属性 + svg 内部 <style>：页面 CSS 里针对
    // path/g 的选择器（典型 `fill:currentColor`）展平后拿不到 → 把算出来的 fill/stroke 写成属性让子节点继承。
    ctx.t.computed++;
    const cs = getComputedStyle(el);
    const clone = el.cloneNode(true);
    const fill = normColor(cs.getPropertyValue("fill"));
    if (fill) clone.setAttribute("fill", fill);
    const stroke = normColor(cs.getPropertyValue("stroke"));
    if (stroke && stroke !== "rgba(0, 0, 0, 0)") {
      clone.setAttribute("stroke", stroke);
      clone.setAttribute("stroke-width", cs.getPropertyValue("stroke-width") || "1");
    }
    clone.setAttribute("class", cls);
    clone.removeAttribute("style");
    clone.removeAttribute("id");
    for (const n of clone.querySelectorAll("[id]")) n.removeAttribute("id");
    for (const n of clone.querySelectorAll("image")) {
      const href = n.getAttribute("href") || n.getAttribute("xlink:href");
      if (href && !href.startsWith("data:")) n.setAttribute("href", absolutize(href));
    }
    for (const n of clone.querySelectorAll("use")) {
      const href = n.getAttribute("href") || n.getAttribute("xlink:href");
      if (href && !href.startsWith("#")) { note(ctx, "svg", "跨文件 <use> 雪碧图（sprite）未导出，图标可能缺失"); n.setAttribute("href", "#none"); }
    }
    return `<svg class="${cls}" width="${px(rect.width, ctx)}" height="${px(rect.height, ctx)}">${clone.innerHTML || ""}</svg>`;
  }

  // ── 图片内联：缓存 + 并发闸门 + 体积预算 ─────────────────────────────
  function imageData(url, ctx) {
    if (!url) return Promise.resolve("");
    if (url.startsWith("data:")) return Promise.resolve(url);
    if (ctx.imgCache.has(url)) return ctx.imgCache.get(url);
    const p = (async () => {
      if (ctx.inlinedBytes > MAX_INLINE_BYTES) { note(ctx, "budget", "内联图片总量超出预算，后面的图片留白（可在面板关掉「内联图片」再导）"); return ""; }
      await gate(ctx);
      const tf = performance.now();
      try {
        let blob;
        try { blob = await (await fetchWithTimeout(url, { credentials: "include" })).blob(); }
        catch { blob = await (await fetchWithTimeout(url, { credentials: "omit" })).blob(); }
        if (!blob.size) return "";
        if (blob.size > MAX_IMG_BYTES) { note(ctx, "bigimg", "有超过 10MB 的单图，导出时留白"); return ""; }
        ctx.inlinedBytes += blob.size;
        return await blobToDataUrl(blob, url);
      } catch {
        return "";
      } finally {
        ctx.t.image += performance.now() - tf;
        ctx.inflight--;
        const next = ctx.waiters.shift();
        if (next) next();
      }
    })();
    ctx.imgCache.set(url, p);
    return p;
  }

  async function gate(ctx) {
    while (ctx.inflight >= FETCH_PARALLEL) await new Promise((r) => ctx.waiters.push(r));
    ctx.inflight++;
  }

  async function drainImages(ctx) {
    await Promise.allSettled([...ctx.imgCache.values()]);
  }

  /** 把 CSS 值里的每个 url(...) 换成 data URI（拉不到的直接留白：引擎不抓网络） */
  async function inlineUrls(value, ctx) {
    if (!value || !value.includes("url(")) return value;
    const urls = [...value.matchAll(/url\(\s*(['"]?)([^'")]+)\1\s*\)/g)].map((m) => m[2].trim());
    let out = value;
    for (const u of urls) {
      if (u.startsWith("data:")) continue;
      const data = await imageData(u.startsWith("blob:") ? u : absolutize(u), ctx);
      out = out.split(`url(${u})`).join(data ? `url("${data}")` : "none");
      out = out.split(`url("${u}")`).join(data ? `url("${data}")` : "none");
      out = out.split(`url('${u}')`).join(data ? `url("${data}")` : "none");
    }
    return out;
  }

  async function fetchWithTimeout(url, init) {
    const ctrl = new AbortController();
    const t = setTimeout(() => ctrl.abort(), FETCH_TIMEOUT);
    try {
      const res = await fetch(url, { ...init, signal: ctrl.signal, cache: "force-cache" });
      if (!res.ok) throw new Error(`HTTP ${res.status}`);
      return res;
    } finally { clearTimeout(t); }
  }

  async function blobToDataUrl(blob, url) {
    const buf = new Uint8Array(await blob.arrayBuffer());
    let bin = "";
    for (let i = 0; i < buf.length; i += 0x8000) bin += String.fromCharCode.apply(null, buf.subarray(i, i + 0x8000));
    return `data:${blob.type || mimeFromUrl(url)};base64,${btoa(bin)}`;
  }

  function mimeFromUrl(url) {
    const ext = (url.split(/[?#]/)[0].split(".").pop() || "").toLowerCase();
    return ({ png: "image/png", jpg: "image/jpeg", jpeg: "image/jpeg", gif: "image/gif", webp: "image/webp", svg: "image/svg+xml", avif: "image/avif" })[ext] || "application/octet-stream";
  }

  async function ensureImagesLoaded(root) {
    const imgs = [...root.querySelectorAll("img")];
    for (const img of imgs) if (img.loading === "lazy") img.loading = "eager";
    await Promise.all(imgs.map((img) => (img.complete ? null : Promise.race([img.decode().catch(() => { }), sleep(3000)]))));
  }

  // ── 小工具 ────────────────────────────────────────────────────────────
  function liftRootBackground(ctx, htmlCS, bodyCS) {
    const bodyBg = normColor(bodyCS.getPropertyValue("background-color"));
    if (bodyBg && bodyBg !== "rgba(0, 0, 0, 0)") return;
    const htmlBg = normColor(htmlCS.getPropertyValue("background-color"));
    if (!htmlBg || htmlBg === "rgba(0, 0, 0, 0)") return;
    ctx.rules.push(`body{background-color:${htmlBg}}`);   // 引擎只渲染 body，网页画在 <html> 上的底色抬过来
  }

  function normColor(v) {
    if (!v) return "";
    if (/^(#[0-9a-f]{3,8}$|rgb\(|rgba\(|hsl\(|hsla\()/i.test(v) && !v.includes(" / ")) return v;
    probe.replaceSync(`e{color:${v}}`);
    return probe.cssRules[0]?.style?.getPropertyValue("color") || v;
  }

  /** Chrome 计算值会用空格/斜杠语法（rgb(0 0 0 / 50%)），引擎的颜色解析只吃逗号语法 → 归一 */
  function normalizeColorsIn(value) {
    return String(value).replace(/rgba?\(([^)]+)\)/g, (m, inner) => {
      if (inner.includes(",") && !inner.includes("/")) return m;
      const [rgb, alpha] = inner.split("/").map((s) => s.trim());
      const nums = rgb.split(/[\s,]+/).filter(Boolean);
      if (nums.length < 3) return m;
      const a = alpha == null ? 1 : (alpha.endsWith("%") ? parseFloat(alpha) / 100 : parseFloat(alpha));
      return `rgba(${nums[0]}, ${nums[1]}, ${nums[2]}, ${Number.isFinite(a) ? a : 1})`;
    });
  }

  /** 整页等比缩放：k = 目标输出宽 / 视口宽（浏览器算好的 used px 全部按比例下发，不重新回流） */
  function px(n, ctx) { return `${r2(n * (ctx?.k || 1))}px`; }
  function sv(v, ctx) {
    const k = ctx?.k ?? 1;
    if (k === 1 || !v || !String(v).includes("px")) return v;
    return String(v).replace(/(-?\d*\.?\d+)px/g, (_, d) => `${r2(parseFloat(d) * k)}px`);
  }

  function absolutize(url) { try { return new URL(url, document.baseURI).href; } catch { return url; } }
  function num(v) { const n = parseFloat(v); return Number.isFinite(n) ? n : 0; }
  function firstNum(v) { return num(String(v).split(/[\s/]/)[0]); }
  function r2(n) { return Math.round(n * 100) / 100; }
  function r3(n) { return Math.round(n * 1000) / 1000; }
  function sleep(ms) { return new Promise((r) => setTimeout(r, ms)); }
  function escapeText(t) { return String(t).replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;"); }
  function escapeAttr(t) { return String(t).replace(/&/g, "&amp;").replace(/"/g, "&quot;").replace(/</g, "&lt;").replace(/>/g, "&gt;"); }
  function note(ctx, key, text) { if (!ctx.notes.has(key)) ctx.notes.set(key, text); }

  global.PSCapture = { capture, UI_ATTR };
})(self);
