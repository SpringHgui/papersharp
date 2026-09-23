// JSX 元素树 → HTML 字符串。兼容 bun/node 的 react-jsx 转换产物（React element 对象），
// 支持函数组件（无 hooks 场景）、Fragment、style 对象、className/tw 双写。
// tw/className 里的 Tailwind token 经 compiler 换成合成类名（.u1…），CSS 由 compiler.css() 汇总。

const VOID_TAGS = new Set(["img", "br", "hr", "input", "meta", "link", "source", "area", "base", "col", "embed", "track", "wbr"]);

function escapeText(s) {
  return String(s).replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;");
}
function escapeAttr(s) {
  return escapeText(s).replace(/"/g, "&quot;");
}

function isElement(v) {
  return v != null && typeof v === "object" && v.$$typeof && typeof v.props === "object";
}

const UNITLESS = new Set(["opacity", "zIndex", "fontWeight", "lineHeight", "flexGrow", "flexShrink", "order", "zoom", "aspectRatio", "fillOpacity", "strokeOpacity"]);

function styleToString(style) {
  if (typeof style === "string") return style;
  const parts = [];
  for (const [k, v] of Object.entries(style)) {
    if (v == null || v === "") continue;
    const prop = k.replace(/[A-Z]/g, (m) => "-" + m.toLowerCase());
    parts.push(`${prop}:${typeof v === "number" && !UNITLESS.has(k) ? v + "px" : v}`);
  }
  return parts.join(";");
}

function walk(node, out, compiler) {
  if (node == null || node === false || node === true) return;
  if (typeof node === "string" || typeof node === "number") {
    out.push(escapeText(node));
    return;
  }
  if (Array.isArray(node)) {
    for (const child of node) walk(child, out, compiler);
    return;
  }
  if (!isElement(node)) {
    if (node && typeof node.toString === "function") out.push(escapeText(node.toString()));
    return;
  }

  const { type, props } = node;
  if (typeof type === "function") {
    walk(type(props ?? {}), out, compiler);
    return;
  }
  if (typeof type === "symbol" || type == null) {
    walk(props?.children, out, compiler);
    return;
  }
  if (typeof type !== "string") return;

  const p = props ?? {};
  const attrs = [];
  const rawTokens = [];
  for (const [k, v] of Object.entries(p)) {
    if (k === "children" || k === "key" || k === "ref" || v == null || v === false) continue;
    if (k === "className" || k === "tw" || k === "class") {
      for (const t of String(v).split(/\s+/)) if (t) rawTokens.push(t);
      continue;
    }
    if (k === "style") {
      const css = styleToString(v);
      if (css) attrs.push(`style="${escapeAttr(css)}"`);
      continue;
    }
    if (v === true) {
      attrs.push(k);
      continue;
    }
    const name = k.replace(/[A-Z]/g, (m) => "-" + m.toLowerCase());
    attrs.push(`${name}="${escapeAttr(v)}"`);
  }
  if (rawTokens.length) {
    const cls = compiler.resolveAll(rawTokens);
    if (cls) attrs.unshift(`class="${cls}"`);
  }

  const open = `<${type}${attrs.length ? " " + attrs.join(" ") : ""}>`;
  if (VOID_TAGS.has(type)) {
    out.push(open);
    return;
  }
  out.push(open);
  if (p.dangerouslySetInnerHTML?.__html) out.push(p.dangerouslySetInnerHTML.__html);
  else walk(p.children, out, compiler);
  out.push(`</${type}>`);
}

/**
 * @param {any} tree JSX 元素 / 元素数组 / 字符串
 * @param {{resolve:(token:string)=>string}} compiler Tailwind 编译器（token → 类名）
 * @returns {string} html
 */
export function jsxToHtml(tree, compiler) {
  const out = [];
  walk(tree, out, compiler);
  return out.join("");
}
