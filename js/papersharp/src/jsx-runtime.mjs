// 轻量 JSX runtime：让 tsx/esbuild 的 automatic 转换直接产出 papersharp 可识别的元素对象，
// 无需依赖 react。配合 tsconfig.json 的 jsxImportSource: "papersharp" 使用。

const REACT_ELEMENT = Symbol.for("react.element");

export function jsx(type, props, key) {
  return { $$typeof: REACT_ELEMENT, type, props: props ?? {}, key: key ?? null };
}
export const jsxs = jsx;
export const Fragment = Symbol.for("react.fragment");
