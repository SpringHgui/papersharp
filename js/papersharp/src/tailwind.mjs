// Tailwind 工具类子集 → PaperSharp 支持的 CSS 声明。
// compileToken 返回 [prop, value][]（便于元素级合成渐变），不识别返回 null（token 原样保留给用户 CSS）。
// 输出用合成类名（.u1…），避免任意值类（w-[1200px]）在 CSS 选择器里转义的麻烦。

const PALETTE = {
  slate: "f8fafc f1f5f9 e2e8f0 cbd5e1 94a3b8 64748b 475569 334155 1e293b 0f172a 020617",
  gray: "f9fafb f3f4f6 e5e7eb d1d5db 9ca3af 6b7280 4b5563 374151 1f2937 111827 030712",
  zinc: "fafafa f4f4f5 e4e4e7 d4d4d8 a1a1aa 71717a 52525b 3f3f46 27272a 18181b 09090b",
  neutral: "fafafa f5f5f5 e5e5e5 d4d4d4 a3a3a3 737373 525252 404040 262626 171717 0a0a0a",
  stone: "fafaf9 f5f5f4 e7e5e4 d6d3d1 a8a29e 78716c 57534e 44403c 292524 1c1917 0c0a09",
  red: "fef2f2 fee2e2 fecaca fca5a5 f87171 ef4444 dc2626 b91c1c 991b1b 7f1d1d 450a0a",
  orange: "fff7ed ffedd5 fed7aa fdba74 fb923c f97316 ea580c c2410c 9a3412 7c2d12 431407",
  amber: "fffbeb fef3c7 fde68a fcd34d fbbf24 f59e0b d97706 b45309 92400e 78350f 451a03",
  yellow: "fefce8 fef9c3 fef08a fde047 facc15 eab308 ca8a04 a16207 854d0e 713f12 422006",
  lime: "f7fee7 ecfccb d9f99d bef264 a3e635 84cc16 65a30d 4d7c0f 3f6212 365314 1a2e05",
  green: "f0fdf4 dcfce7 bbf7d0 86efac 4ade80 22c55e 16a34a 15803d 166534 14532d 052e16",
  emerald: "ecfdf5 d1fae5 a7f3d0 6ee7b7 34d399 10b981 059669 047857 065f46 064e3b 022c22",
  teal: "f0fdfa ccfbf1 99f6e4 5eead4 2dd4bf 14b8a6 0d9488 0f766e 115e59 134e4a 042f2e",
  cyan: "ecfeff cffafe a5f3fc 67e8f9 22d3ee 06b6d4 0891b2 0e7490 155e75 164e63 083344",
  sky: "f0f9ff e0f2fe bae6fd 7dd3fc 38bdf8 0ea5e9 0284c7 0369a1 075985 0c4a6e 082f49",
  blue: "eff6ff dbeafe bfdbfe 93c5fd 60a5fa 3b82f6 2563eb 1d4ed8 1e40af 1e3a8a 172554",
  indigo: "eef2ff e0e7ff c7d2fe a5b4fc 818cf8 6366f1 4f46e5 4338ca 3730a3 312e81 1e1b4b",
  violet: "f5f3ff ede9fe ddd6fe c4b5fd a78bfa 8b5cf6 7c3aed 6d28d9 5b21b6 4c1d95 2e1065",
  purple: "faf5ff f3e8ff e9d5ff d8b4fe c084fc a855f7 9333ea 7e22ce 6b21a8 581c87 3b0764",
  fuchsia: "fdf4ff fae8ff f5d0fe f0abfc e879f9 d946ef c026d3 a21caf 86198f 701a75 4a044e",
  pink: "fdf2f8 fce7f3 fbcfe8 f9a8d4 f472b6 ec4899 db2777 be185d 9d174d 831843 500724",
  rose: "fff1f2 ffe4e6 fecdd3 fda4af fb7185 f43f5e e11d48 be123c 9f1239 881337 4c0519",
};
const SHADES = ["50", "100", "200", "300", "400", "500", "600", "700", "800", "900", "950"];
for (const [name, list] of Object.entries(PALETTE)) {
  PALETTE[name] = Object.fromEntries(list.split(" ").map((h, i) => [SHADES[i], "#" + h]));
}
const NAMED = { black: "#000", white: "#fff", transparent: "transparent" };

function color(token) {
  if (!token) return null;
  if (NAMED[token]) return NAMED[token];
  const dash = token.lastIndexOf("-");
  if (dash < 0) return null;
  return PALETTE[token.slice(0, dash)]?.[token.slice(dash + 1)] ?? null;
}

const TEXT_SIZES = {
  xs: [12, "16px"], sm: [14, "20px"], base: [16, "24px"], lg: [18, "28px"], xl: [20, "28px"],
  "2xl": [24, "32px"], "3xl": [30, "36px"], "4xl": [36, "40px"], "5xl": [48, "normal"],
  "6xl": [60, "normal"], "7xl": [72, "normal"], "8xl": [96, "normal"], "9xl": [128, "normal"],
};
const WEIGHTS = { thin: 100, extralight: 200, light: 300, normal: 400, medium: 500, semibold: 600, bold: 700, extrabold: 800, black: 900 };
const RADII = { sm: 2, DEFAULT: 4, md: 6, lg: 8, xl: 12, "2xl": 16, "3xl": 24, full: 9999 };
const GRADIENT_DIRS = {
  t: "to top", b: "to bottom", l: "to left", r: "to right",
  tr: "to top right", tl: "to top left", br: "to bottom right", bl: "to bottom left",
};

// Tailwind animate-* 工具类：映射到 animation 简写 + 对应 @keyframes（惰性按需注入）。
// 引擎为确定性单帧渲染（无 transform 支持）：pulse 靠 opacity 可正常取帧；
// spin/ping/bounce 依赖 transform，关键帧会被解析但位移不生效（不做假变换）。
const ANIMATIONS = {
  pulse: {
    shorthand: "pulse 2s cubic-bezier(0.4,0,0.6,1) infinite",
    keyframes: "@keyframes pulse{0%,100%{opacity:1}50%{opacity:.5}}",
  },
  spin: {
    shorthand: "spin 1s linear infinite",
    keyframes: "@keyframes spin{to{transform:rotate(360deg)}}",
  },
  ping: {
    shorthand: "ping 1s cubic-bezier(0,0,0.2,1) infinite",
    keyframes: "@keyframes ping{75%,100%{transform:scale(2);opacity:0}}",
  },
  bounce: {
    shorthand: "bounce 1s infinite",
    keyframes: "@keyframes bounce{0%,100%,20%,53%{transform:translateY(0)}40%,80%{transform:translateY(-30%)}70%{transform:translateY(-10%)}}",
  },
};

function spacing(v) {
  if (v === "px") return 1;
  if (v === "auto") return null;
  const n = Number(v);
  return Number.isFinite(n) ? n * 4 : null; // Tailwind 刻度：1 单位 = 4px
}

function frac(v) {
  const m = /^(\d+)\/(\d+)$/.exec(v);
  return m ? (Number(m[1]) / Number(m[2])) * 100 + "%" : null;
}

function arbitraryValue(raw) {
  const m = /^\[(.+)\]$/.exec(raw);
  if (!m) return null;
  let v = m[1].replace(/_/g, " ");
  if (/^-?\d+(\.\d+)?$/.test(v)) v += "px";
  return v;
}

const d = (prop, value) => [prop, String(value)];

/** @returns {[string,string][]|null} */
export function compileToken(token) {
  let m;

  switch (token) {
    case "flex": return [d("display", "flex")];
    case "inline-flex": return [d("display", "inline-flex")];
    case "grid": return [d("display", "grid")];
    case "block": return [d("display", "block")];
    case "inline-block": return [d("display", "inline-block")];
    case "hidden": return [d("display", "none")];
    case "flex-row": return [d("flex-direction", "row")];
    case "flex-col": return [d("flex-direction", "column")];
    case "flex-wrap": return [d("flex-wrap", "wrap")];
    case "grow": case "flex-1": return [d("flex-grow", 1)];
    case "shrink-0": return [d("flex-shrink", 0)];
    case "relative": return [d("position", "relative")];
    case "absolute": return [d("position", "absolute")];
    case "truncate": return [d("overflow", "hidden"), d("text-overflow", "ellipsis"), d("white-space", "nowrap")];
    case "italic": return [d("font-style", "italic")];
    case "uppercase": return [d("text-transform", "uppercase")];
    case "lowercase": return [d("text-transform", "lowercase")];
    case "capitalize": return [d("text-transform", "capitalize")];
    case "whitespace-nowrap": return [d("white-space", "nowrap")];
    case "text-left": return [d("text-align", "left")];
    case "text-center": return [d("text-align", "center")];
    case "text-right": return [d("text-align", "right")];
    case "items-center": return [d("align-items", "center")];
    case "items-start": return [d("align-items", "flex-start")];
    case "items-end": return [d("align-items", "flex-end")];
    case "justify-center": return [d("justify-content", "center")];
    case "justify-between": return [d("justify-content", "space-between")];
    case "justify-around": return [d("justify-content", "space-around")];
    case "justify-evenly": return [d("justify-content", "space-evenly")];
    case "justify-end": return [d("justify-content", "flex-end")];
    case "self-center": return [d("align-self", "center")];
    case "border": return [d("border-width", "1px"), d("border-style", "solid")];
    case "rounded": return cornerRadii("4", null);
    case "font-mono": return [d("font-family", "monospace")];
    case "font-serif": return [d("font-family", "serif")];
    case "font-sans": return [d("font-family", "sans-serif")];
  }

  // 尺寸
  if ((m = /^w-(.+)$/.exec(token))) {
    if (m[1] === "full") return [d("width", "100%")];
    if (m[1] === "screen") return [d("width", "100vw")];
    const a = arbitraryValue(m[1]); if (a) return [d("width", a)];
    const f = frac(m[1]); if (f) return [d("width", f)];
    const px = spacing(m[1]); if (px != null) return [d("width", px + "px")];
  }
  if ((m = /^h-(.+)$/.exec(token))) {
    if (m[1] === "full") return [d("height", "100%")];
    if (m[1] === "screen") return [d("height", "100vh")];
    const a = arbitraryValue(m[1]); if (a) return [d("height", a)];
    const f = frac(m[1]); if (f) return [d("height", f)];
    const px = spacing(m[1]); if (px != null) return [d("height", px + "px")];
  }
  if ((m = /^min-w-(.+)$/.exec(token))) {
    const px = spacing(m[1]); if (px != null) return [d("min-width", px + "px")];
  }
  if ((m = /^max-w-(.+)$/.exec(token))) {
    const px = spacing(m[1]); if (px != null) return [d("max-width", px + "px")];
  }

  // 间距
  if ((m = /^p([xytrbl]?)-(.+)$/.exec(token))) {
    const decls = boxSides("padding", m[1], m[2]); if (decls) return decls;
  }
  if ((m = /^m([xytrbl]?)-(.+)$/.exec(token))) {
    const decls = boxSides("margin", m[1], m[2]); if (decls) return decls;
  }
  if ((m = /^gap-(.+)$/.exec(token))) {
    const a = arbitraryValue(m[1]); if (a) return [d("gap", a)];
    const v = spacing(m[1]); if (v != null) return [d("gap", v + "px")];
  }

  // 排版
  if ((m = /^text-(xs|sm|base|lg|xl|2xl|3xl|4xl|5xl|6xl|7xl|8xl|9xl)$/.exec(token))) {
    const [size, lh] = TEXT_SIZES[m[1]];
    return [d("font-size", size + "px"), d("line-height", lh)];
  }
  if ((m = /^font-(thin|extralight|light|normal|medium|semibold|bold|extrabold|black)$/.exec(token)))
    return [d("font-weight", WEIGHTS[m[1]])];
  if ((m = /^leading-(none|tight|snug|normal|relaxed|loose)$/.exec(token)))
    return [d("line-height", { none: 1, tight: 1.25, snug: 1.375, normal: 1.5, relaxed: 1.625, loose: 2 }[m[1]])];
  if ((m = /^tracking-(tighter|tight|normal|wide|wider|widest)$/.exec(token)))
    return [d("letter-spacing", { tighter: -1.2, tight: -0.6, normal: 0, wide: 0.4, wider: 0.8, widest: 1.6 }[m[1]] + "px")];

  // 颜色（text-<color> 优先于 text-<size>，size 已在上面命中）
  if ((m = /^text-(.+)$/.exec(token))) {
    const c = color(m[1]); if (c) return [d("color", c)];
    const a = arbitraryValue(m[1]); if (a && (a.startsWith("#") || a.startsWith("rgb"))) return [d("color", a)];
  }
  if ((m = /^border-(.+)$/.exec(token))) {
    const c = color(m[1]); if (c) return [d("border-color", c)];
    if ((m = /^border-(\d)$/.exec(token))) return [d("border-width", m[1] + "px"), d("border-style", "solid")];
  }

  // 渐变（v4: bg-linear-to-b；v3: bg-gradient-to-b）+ from-/via-/to- 色标
  if ((m = /^bg-(?:linear|gradient)-to-(t|b|l|r|tr|tl|br|bl)$/.exec(token)))
    return [d("x-grad-dir", GRADIENT_DIRS[m[1]])];
  if ((m = /^bg-(.+)$/.exec(token))) {
    const c = color(m[1]); if (c) return [d("background-color", c)];
    const a = arbitraryValue(m[1]); if (a) return [d("background-color", a)];
  }
  if ((m = /^from-(.+)$/.exec(token))) { const c = color(m[1]); if (c) return [d("x-grad-from", c)]; }
  if ((m = /^via-(.+)$/.exec(token))) { const c = color(m[1]); if (c) return [d("x-grad-via", c)]; }
  if ((m = /^to-(.+)$/.exec(token))) { const c = color(m[1]); if (c) return [d("x-grad-to", c)]; }

  // 圆角
  if ((m = /^rounded(-sm|-md|-lg|-xl|-2xl|-3xl|-full)?(-t|-b|-l|-r)?$/.exec(token))) {
    const r = RADII[(m[1] ?? "").slice(1) || "DEFAULT"];
    return cornerRadii(String(r), m[2]?.slice(1));
  }

  // 透明度 / 层级
  if ((m = /^opacity-(\d+)$/.exec(token))) return [d("opacity", Number(m[1]) / 100)];
  if ((m = /^z-(\d+)$/.exec(token))) return [d("z-index", m[1])];

  // 动画（animate-pulse/spin/ping/bounce）
  if ((m = /^animate-(pulse|spin|ping|bounce)$/.exec(token))) return [d("animation", ANIMATIONS[m[1]].shorthand)];

  // 定位偏移（含负值）
  if ((m = /^-(t|r|b|l)-(.+)$/.exec(token))) {
    const v = spacing(m[2]); if (v != null) return [d({ t: "top", r: "right", b: "bottom", l: "left" }[m[1]], -v + "px")];
  }
  if ((m = /^(t|r|b|l)-(.+)$/.exec(token))) {
    const a = arbitraryValue(m[2]); if (a) return [d({ t: "top", r: "right", b: "bottom", l: "left" }[m[1]], a)];
    const v = spacing(m[2]); if (v != null) return [d({ t: "top", r: "right", b: "bottom", l: "left" }[m[1]], v + "px")];
  }

  return null;
}

function cornerRadii(px, side) {
  const corners = side === "t" ? ["top-left", "top-right"] : side === "b" ? ["bottom-left", "bottom-right"]
    : side === "l" ? ["top-left", "bottom-left"] : side === "r" ? ["top-right", "bottom-right"]
    : ["top-left", "top-right", "bottom-left", "bottom-right"];
  return corners.map((c) => d("border-" + c + "-radius", px + "px"));
}

function boxSides(base, side, key) {
  const neg = key.startsWith("-");
  if (neg) key = key.slice(1);
  const props = { "": [base], x: [base + "-left", base + "-right"], y: [base + "-top", base + "-bottom"],
    t: [base + "-top"], r: [base + "-right"], b: [base + "-bottom"], l: [base + "-left"] }[side];
  if (!props) return null;
  if (key === "auto") return base === "margin" ? props.map((p) => d(p, "auto")) : null;
  const a = arbitraryValue(key);
  if (a) return props.map((p) => d(p, a));
  const v = spacing(key);
  if (v == null) return null;
  return props.map((p) => d(p, (neg ? -v : v) + "px"));
}

/** 元素级合成：把 x-grad-* 伪声明折叠成 background 渐变。 */
export function foldDeclarations(decls) {
  let dir, from, via, to;
  const out = [];
  for (const [prop, value] of decls) {
    if (prop === "x-grad-dir") { dir = value; continue; }
    if (prop === "x-grad-from") { from = value; continue; }
    if (prop === "x-grad-via") { via = value; continue; }
    if (prop === "x-grad-to") { to = value; continue; }
    out.push(d(prop, value));
  }
  if (dir && (from || to)) {
    const stops = [from, via, to].filter(Boolean);
    out.push(d("background", `linear-gradient(${dir}, ${stops.join(", ")})`));
  }
  return out;
}

const PREFLIGHT = `
body { margin: 0; }
h1, h2, h3, h4, h5, h6, p, ul, ol, figure, blockquote { margin: 0; }
ul, ol { padding: 0; list-style: none; }
img { display: block; }
`;

/** 创建一次渲染用的 Tailwind 编译器：token → 合成类名 + 规则表。 */
export function createCompiler() {
  const map = new Map();
  const rules = [];
  let counter = 0;
  return {
    /**
     * 元素级解析：把该元素 class 里的所有 token 一起编译、折叠（渐变 dir+from+via+to 需同处才能合成），
     * 生成一个合成类名；不识别的 token 原样保留（交给用户 CSS）。
     * @param {string[]} tokens @returns {string} 写进 class 属性的最终类串
     */
    resolveAll(tokens) {
      const raw = tokens.filter(Boolean);
      if (!raw.length) return "";
      const hit = map.get(raw.join(" "));
      if (hit != null) return hit;
      const decls = [];
      const passthrough = [];
      for (const t of raw) {
        const c = compileToken(t);
        if (c) decls.push(...c);
        else passthrough.push(t);
      }
      const folded = foldDeclarations(decls);
      let name = "";
      if (folded.length) {
        name = "u" + ++counter;
        rules.push([name, folded]);
      }
      const result = [name, ...passthrough].filter(Boolean).join(" ");
      map.set(raw.join(" "), result);
      return result;
    },
    /** 单 token 解析（无跨 token 折叠需求时用）。 */
    resolve(token) {
      return this.resolveAll([token]);
    },
    css() {
      // 按需注入用到的 @keyframes：扫描已生成规则里的 animation 简写，取其动画名
      const used = new Set();
      for (const [, decls] of rules)
        for (const [p, v] of decls)
          if (p === "animation") { const name = /([a-zA-Z_][\w-]*)/.exec(String(v).replace(/^\s*none\b/, "")); if (name && ANIMATIONS[name[1]]) used.add(name[1]); }
      const kf = [...used].map((n) => ANIMATIONS[n].keyframes).join("\n");
      return PREFLIGHT + (kf ? "\n" + kf + "\n" : "\n") + rules
        .map(([name, decls]) => `.${name} { ${decls.map(([p, v]) => `${p}:${v}`).join(";")} }`)
        .join("\n");
    },
  };
}
