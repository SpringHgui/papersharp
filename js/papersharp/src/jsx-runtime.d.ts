// papersharp JSX 运行时类型声明
// 配合 tsconfig.json 的 jsxImportSource: "papersharp" 使用，无需 @types/react。

// ── JSX Runtime 导出 ──────────────────────────────────────────────

export function jsx(
  type: string | ((props: any) => JSX.Element) | symbol,
  props: Record<string, any> | null,
  key?: string | number | null,
): JSX.Element;

export const jsxs: typeof jsx;

export const Fragment: unique symbol;

// ── JSX 命名空间 ──────────────────────────────────────────────────

export namespace JSX {
  type Element = ReactElement;

  interface ElementChildrenProperty {
    children: ReactNode;
  }

  interface IntrinsicElements {
    // 文档结构
    html: HTMLAttributes;
    head: HTMLAttributes;
    body: HTMLAttributes;
    title: HTMLAttributes;
    link: LinkHTMLAttributes;
    meta: MetaHTMLAttributes;
    style: HTMLAttributes;
    script: ScriptHTMLAttributes;
    base: BaseHTMLAttributes;

    // 分区
    div: HTMLAttributes;
    span: HTMLAttributes;
    main: HTMLAttributes;
    section: HTMLAttributes;
    article: HTMLAttributes;
    aside: HTMLAttributes;
    header: HTMLAttributes;
    footer: HTMLAttributes;
    nav: HTMLAttributes;

    // 标题
    h1: HTMLAttributes;
    h2: HTMLAttributes;
    h3: HTMLAttributes;
    h4: HTMLAttributes;
    h5: HTMLAttributes;
    h6: HTMLAttributes;

    // 文本内容
    p: HTMLAttributes;
    blockquote: HTMLAttributes;
    pre: HTMLAttributes;
    code: HTMLAttributes;

    // 列表
    ul: HTMLAttributes;
    ol: HTMLAttributes;
    li: HTMLAttributes;
    dl: HTMLAttributes;
    dt: HTMLAttributes;
    dd: HTMLAttributes;

    // 行内文本
    a: AnchorHTMLAttributes;
    strong: HTMLAttributes;
    em: HTMLAttributes;
    small: HTMLAttributes;
    sub: HTMLAttributes;
    sup: HTMLAttributes;
    mark: HTMLAttributes;
    del: HTMLAttributes;
    ins: HTMLAttributes;
    abbr: HTMLAttributes;
    cite: HTMLAttributes;
    time: HTMLAttributes;
    u: HTMLAttributes;
    s: HTMLAttributes;

    // 媒体
    img: ImgHTMLAttributes;
    picture: HTMLAttributes;
    source: SourceHTMLAttributes;
    svg: SVGAttributes;
    path: SVGAttributes;
    circle: SVGAttributes;
    rect: SVGAttributes;
    line: SVGAttributes;
    polygon: SVGAttributes;
    polyline: SVGAttributes;
    ellipse: SVGAttributes;
    g: SVGAttributes;
    text: SVGAttributes;
    defs: SVGAttributes;
    use: SVGAttributes;
    clipPath: SVGAttributes;
    linearGradient: SVGAttributes;
    radialGradient: SVGAttributes;
    stop: SVGAttributes;
    mask: SVGAttributes;
    pattern: SVGAttributes;
    symbol: SVGAttributes;

    // 表单
    form: FormHTMLAttributes;
    input: InputHTMLAttributes;
    button: ButtonHTMLAttributes;
    select: SelectHTMLAttributes;
    option: OptionHTMLAttributes;
    textarea: TextareaHTMLAttributes;
    label: LabelHTMLAttributes;
    fieldset: HTMLAttributes;
    legend: HTMLAttributes;

    // 表格
    table: TableHTMLAttributes;
    thead: HTMLAttributes;
    tbody: HTMLAttributes;
    tfoot: HTMLAttributes;
    tr: HTMLAttributes;
    th: ThHTMLAttributes;
    td: TdHTMLAttributes;
    caption: HTMLAttributes;
    colgroup: ColgroupHTMLAttributes;
    col: ColHTMLAttributes;

    // 嵌入
    iframe: IframeHTMLAttributes;
    canvas: HTMLAttributes;
    video: VideoHTMLAttributes;
    audio: AudioHTMLAttributes;

    // 其他
    hr: HTMLAttributes;
    br: HTMLAttributes;
    details: HTMLAttributes;
    summary: HTMLAttributes;
    dialog: HTMLAttributes;
    template: HTMLAttributes;
    slot: SlotHTMLAttributes;
  }
}

// ── 内部类型 ──────────────────────────────────────────────────────

interface ReactElement {
  $$typeof: symbol;
  type: string | ((props: any) => ReactElement) | symbol;
  props: Record<string, any>;
  key: string | number | null;
}

type ReactNode =
  | ReactElement
  | string
  | number
  | boolean
  | null
  | undefined
  | ReactNode[];

// ── HTML 属性接口 ─────────────────────────────────────────────────

/** Tailwind CSS 4 工具类（内置编译子集 + 任意值透传） */
type TailwindCSS =
  // Display
  | "flex" | "inline-flex" | "grid" | "inline-grid" | "block" | "inline-block" | "inline" | "hidden" | "contents" | "flow-root"
  // Position
  | "static" | "fixed" | "absolute" | "relative" | "sticky"
  // Visibility / Overflow
  | "visible" | "invisible" | "collapse" | "overflow-auto" | "overflow-hidden" | "overflow-clip" | "overflow-visible" | "overflow-scroll"
  | "overflow-x-auto" | "overflow-x-hidden" | "overflow-x-visible" | "overflow-x-scroll"
  | "overflow-y-auto" | "overflow-y-hidden" | "overflow-y-visible" | "overflow-y-scroll"
  // Float / Clear
  | "float-start" | "float-end" | "float-none"
  | "clear-start" | "clear-end" | "clear-both" | "clear-none"
  // Box Sizing
  | "box-border" | "box-content"
  // Object Fit
  | "object-contain" | "object-cover" | "object-fill" | "object-none" | "object-scale-down"
  // Flexbox
  | "flex-row" | "flex-row-reverse" | "flex-col" | "flex-col-reverse"
  | "flex-wrap" | "flex-wrap-reverse" | "flex-nowrap"
  | "flex-1" | "flex-auto" | "flex-initial" | "flex-none"
  | "grow" | "grow-0" | "shrink" | "shrink-0"
  // Flex Alignment
  | "items-start" | "items-end" | "items-center" | "items-baseline" | "items-stretch"
  | "justify-start" | "justify-end" | "justify-center" | "justify-between" | "justify-around" | "justify-evenly"
  | "self-auto" | "self-start" | "self-end" | "self-center" | "self-stretch"
  | "place-content-center" | "place-content-start" | "place-content-end" | "place-content-between" | "place-content-around" | "place-content-evenly" | "place-content-stretch"
  | "place-items-start" | "place-items-end" | "place-items-center" | "place-items-stretch"
  | "content-start" | "content-end" | "content-center" | "content-between" | "content-around" | "content-evenly" | "content-stretch"
  // Grid
  | "grid-flow-row" | "grid-flow-col" | "grid-flow-dense" | "grid-flow-row-dense" | "grid-flow-col-dense"
  | "grid-cols-1" | "grid-cols-2" | "grid-cols-3" | "grid-cols-4" | "grid-cols-5" | "grid-cols-6" | "grid-cols-7" | "grid-cols-8" | "grid-cols-9" | "grid-cols-10" | "grid-cols-11" | "grid-cols-12" | "grid-cols-none"
  | "grid-rows-1" | "grid-rows-2" | "grid-rows-3" | "grid-rows-4" | "grid-rows-5" | "grid-rows-6" | "grid-rows-none"
  | "col-auto" | "col-span-1" | "col-span-2" | "col-span-3" | "col-span-4" | "col-span-5" | "col-span-6" | "col-span-7" | "col-span-8" | "col-span-9" | "col-span-10" | "col-span-11" | "col-span-12" | "col-span-full"
  | "row-auto" | "row-span-1" | "row-span-2" | "row-span-3" | "row-span-4" | "row-span-5" | "row-span-6" | "row-span-full"
  | "auto-cols-auto" | "auto-cols-min" | "auto-cols-max" | "auto-cols-fr"
  | "auto-rows-auto" | "auto-rows-min" | "auto-rows-max" | "auto-rows-fr"
  // Gap
  | "gap-0" | "gap-px" | "gap-0.5" | "gap-1" | "gap-1.5" | "gap-2" | "gap-2.5" | "gap-3" | "gap-3.5" | "gap-4" | "gap-5" | "gap-6" | "gap-7" | "gap-8" | "gap-9" | "gap-10" | "gap-11" | "gap-12" | "gap-14" | "gap-16" | "gap-20" | "gap-24" | "gap-28" | "gap-32" | "gap-36" | "gap-40" | "gap-44" | "gap-48" | "gap-52" | "gap-56" | "gap-60" | "gap-64" | "gap-72" | "gap-80" | "gap-96"
  | "gap-x-0" | "gap-x-px" | "gap-x-0.5" | "gap-x-1" | "gap-x-1.5" | "gap-x-2" | "gap-x-2.5" | "gap-x-3" | "gap-x-3.5" | "gap-x-4" | "gap-x-5" | "gap-x-6" | "gap-x-7" | "gap-x-8" | "gap-x-9" | "gap-x-10" | "gap-x-11" | "gap-x-12" | "gap-x-14" | "gap-x-16" | "gap-x-20" | "gap-x-24" | "gap-x-28" | "gap-x-32" | "gap-x-36" | "gap-x-40" | "gap-x-44" | "gap-x-48" | "gap-x-52" | "gap-x-56" | "gap-x-60" | "gap-x-64" | "gap-x-72" | "gap-x-80" | "gap-x-96"
  | "gap-y-0" | "gap-y-px" | "gap-y-0.5" | "gap-y-1" | "gap-y-1.5" | "gap-y-2" | "gap-y-2.5" | "gap-y-3" | "gap-y-3.5" | "gap-y-4" | "gap-y-5" | "gap-y-6" | "gap-y-7" | "gap-y-8" | "gap-y-9" | "gap-y-10" | "gap-y-11" | "gap-y-12" | "gap-y-14" | "gap-y-16" | "gap-y-20" | "gap-y-24" | "gap-y-28" | "gap-y-32" | "gap-y-36" | "gap-y-40" | "gap-y-44" | "gap-y-48" | "gap-y-52" | "gap-y-56" | "gap-y-60" | "gap-y-64" | "gap-y-72" | "gap-y-80" | "gap-y-96"
  // Padding
  | "p-0" | "p-px" | "p-0.5" | "p-1" | "p-1.5" | "p-2" | "p-2.5" | "p-3" | "p-3.5" | "p-4" | "p-5" | "p-6" | "p-7" | "p-8" | "p-9" | "p-10" | "p-11" | "p-12" | "p-14" | "p-16" | "p-20" | "p-24" | "p-28" | "p-32" | "p-36" | "p-40" | "p-44" | "p-48" | "p-52" | "p-56" | "p-60" | "p-64" | "p-72" | "p-80" | "p-96"
  | "px-0" | "px-px" | "px-0.5" | "px-1" | "px-1.5" | "px-2" | "px-2.5" | "px-3" | "px-3.5" | "px-4" | "px-5" | "px-6" | "px-7" | "px-8" | "px-9" | "px-10" | "px-11" | "px-12" | "px-14" | "px-16" | "px-20" | "px-24" | "px-28" | "px-32" | "px-36" | "px-40" | "px-44" | "px-48" | "px-52" | "px-56" | "px-60" | "px-64" | "px-72" | "px-80" | "px-96"
  | "py-0" | "py-px" | "py-0.5" | "py-1" | "py-1.5" | "py-2" | "py-2.5" | "py-3" | "py-3.5" | "py-4" | "py-5" | "py-6" | "py-7" | "py-8" | "py-9" | "py-10" | "py-11" | "py-12" | "py-14" | "py-16" | "py-20" | "py-24" | "py-28" | "py-32" | "py-36" | "py-40" | "py-44" | "py-48" | "py-52" | "py-56" | "py-60" | "py-64" | "py-72" | "py-80" | "py-96"
  | "pt-0" | "pt-px" | "pt-0.5" | "pt-1" | "pt-1.5" | "pt-2" | "pt-2.5" | "pt-3" | "pt-3.5" | "pt-4" | "pt-5" | "pt-6" | "pt-7" | "pt-8" | "pt-9" | "pt-10" | "pt-11" | "pt-12" | "pt-14" | "pt-16" | "pt-20" | "pt-24" | "pt-28" | "pt-32" | "pt-36" | "pt-40" | "pt-44" | "pt-48" | "pt-52" | "pt-56" | "pt-60" | "pt-64" | "pt-72" | "pt-80" | "pt-96"
  | "pr-0" | "pr-px" | "pr-0.5" | "pr-1" | "pr-1.5" | "pr-2" | "pr-2.5" | "pr-3" | "pr-3.5" | "pr-4" | "pr-5" | "pr-6" | "pr-7" | "pr-8" | "pr-9" | "pr-10" | "pr-11" | "pr-12" | "pr-14" | "pr-16" | "pr-20" | "pr-24" | "pr-28" | "pr-32" | "pr-36" | "pr-40" | "pr-44" | "pr-48" | "pr-52" | "pr-56" | "pr-60" | "pr-64" | "pr-72" | "pr-80" | "pr-96"
  | "pb-0" | "pb-px" | "pb-0.5" | "pb-1" | "pb-1.5" | "pb-2" | "pb-2.5" | "pb-3" | "pb-3.5" | "pb-4" | "pb-5" | "pb-6" | "pb-7" | "pb-8" | "pb-9" | "pb-10" | "pb-11" | "pb-12" | "pb-14" | "pb-16" | "pb-20" | "pb-24" | "pb-28" | "pb-32" | "pb-36" | "pb-40" | "pb-44" | "pb-48" | "pb-52" | "pb-56" | "pb-60" | "pb-64" | "pb-72" | "pb-80" | "pb-96"
  | "pl-0" | "pl-px" | "pl-0.5" | "pl-1" | "pl-1.5" | "pl-2" | "pl-2.5" | "pl-3" | "pl-3.5" | "pl-4" | "pl-5" | "pl-6" | "pl-7" | "pl-8" | "pl-9" | "pl-10" | "pl-11" | "pl-12" | "pl-14" | "pl-16" | "pl-20" | "pl-24" | "pl-28" | "pl-32" | "pl-36" | "pl-40" | "pl-44" | "pl-48" | "pl-52" | "pl-56" | "pl-60" | "pl-64" | "pl-72" | "pl-80" | "pl-96"
  // Margin
  | "m-0" | "m-px" | "m-0.5" | "m-1" | "m-1.5" | "m-2" | "m-2.5" | "m-3" | "m-3.5" | "m-4" | "m-5" | "m-6" | "m-7" | "m-8" | "m-9" | "m-10" | "m-11" | "m-12" | "m-14" | "m-16" | "m-20" | "m-24" | "m-28" | "m-32" | "m-36" | "m-40" | "m-44" | "m-48" | "m-52" | "m-56" | "m-60" | "m-64" | "m-72" | "m-80" | "m-96" | "m-auto"
  | "mx-0" | "mx-px" | "mx-0.5" | "mx-1" | "mx-1.5" | "mx-2" | "mx-2.5" | "mx-3" | "mx-3.5" | "mx-4" | "mx-5" | "mx-6" | "mx-7" | "mx-8" | "mx-9" | "mx-10" | "mx-11" | "mx-12" | "mx-14" | "mx-16" | "mx-20" | "mx-24" | "mx-28" | "mx-32" | "mx-36" | "mx-40" | "mx-44" | "mx-48" | "mx-52" | "mx-56" | "mx-60" | "mx-64" | "mx-72" | "mx-80" | "mx-96" | "mx-auto"
  | "my-0" | "my-px" | "my-0.5" | "my-1" | "my-1.5" | "my-2" | "my-2.5" | "my-3" | "my-3.5" | "my-4" | "my-5" | "my-6" | "my-7" | "my-8" | "my-9" | "my-10" | "my-11" | "my-12" | "my-14" | "my-16" | "my-20" | "my-24" | "my-28" | "my-32" | "my-36" | "my-40" | "my-44" | "my-48" | "my-52" | "my-56" | "my-60" | "my-64" | "my-72" | "my-80" | "my-96" | "my-auto"
  | "mt-0" | "mt-px" | "mt-0.5" | "mt-1" | "mt-1.5" | "mt-2" | "mt-2.5" | "mt-3" | "mt-3.5" | "mt-4" | "mt-5" | "mt-6" | "mt-7" | "mt-8" | "mt-9" | "mt-10" | "mt-11" | "mt-12" | "mt-14" | "mt-16" | "mt-20" | "mt-24" | "mt-28" | "mt-32" | "mt-36" | "mt-40" | "mt-44" | "mt-48" | "mt-52" | "mt-56" | "mt-60" | "mt-64" | "mt-72" | "mt-80" | "mt-96" | "mt-auto"
  | "mr-0" | "mr-px" | "mr-0.5" | "mr-1" | "mr-1.5" | "mr-2" | "mr-2.5" | "mr-3" | "mr-3.5" | "mr-4" | "mr-5" | "mr-6" | "mr-7" | "mr-8" | "mr-9" | "mr-10" | "mr-11" | "mr-12" | "mr-14" | "mr-16" | "mr-20" | "mr-24" | "mr-28" | "mr-32" | "mr-36" | "mr-40" | "mr-44" | "mr-48" | "mr-52" | "mr-56" | "mr-60" | "mr-64" | "mr-72" | "mr-80" | "mr-96" | "mr-auto"
  | "mb-0" | "mb-px" | "mb-0.5" | "mb-1" | "mb-1.5" | "mb-2" | "mb-2.5" | "mb-3" | "mb-3.5" | "mb-4" | "mb-5" | "mb-6" | "mb-7" | "mb-8" | "mb-9" | "mb-10" | "mb-11" | "mb-12" | "mb-14" | "mb-16" | "mb-20" | "mb-24" | "mb-28" | "mb-32" | "mb-36" | "mb-40" | "mb-44" | "mb-48" | "mb-52" | "mb-56" | "mb-60" | "mb-64" | "mb-72" | "mb-80" | "mb-96" | "mb-auto"
  | "ml-0" | "ml-px" | "ml-0.5" | "ml-1" | "ml-1.5" | "ml-2" | "ml-2.5" | "ml-3" | "ml-3.5" | "ml-4" | "ml-5" | "ml-6" | "ml-7" | "ml-8" | "ml-9" | "ml-10" | "ml-11" | "ml-12" | "ml-14" | "ml-16" | "ml-20" | "ml-24" | "ml-28" | "ml-32" | "ml-36" | "ml-40" | "ml-44" | "ml-48" | "ml-52" | "ml-56" | "ml-60" | "ml-64" | "ml-72" | "ml-80" | "ml-96" | "ml-auto"
  // Width
  | "w-0" | "w-px" | "w-0.5" | "w-1" | "w-1.5" | "w-2" | "w-2.5" | "w-3" | "w-3.5" | "w-4" | "w-5" | "w-6" | "w-7" | "w-8" | "w-9" | "w-10" | "w-11" | "w-12" | "w-14" | "w-16" | "w-20" | "w-24" | "w-28" | "w-32" | "w-36" | "w-40" | "w-44" | "w-48" | "w-52" | "w-56" | "w-60" | "w-64" | "w-72" | "w-80" | "w-96"
  | "w-1/2" | "w-1/3" | "w-2/3" | "w-1/4" | "w-2/4" | "w-3/4" | "w-1/5" | "w-2/5" | "w-3/5" | "w-4/5" | "w-1/6" | "w-2/6" | "w-3/6" | "w-4/6" | "w-5/6" | "w-1/12" | "w-2/12" | "w-3/12" | "w-4/12" | "w-5/12" | "w-6/12" | "w-7/12" | "w-8/12" | "w-9/12" | "w-10/12" | "w-11/12"
  | "w-full" | "w-screen" | "w-min" | "w-max" | "w-fit" | "w-auto"
  // Height
  | "h-0" | "h-px" | "h-0.5" | "h-1" | "h-1.5" | "h-2" | "h-2.5" | "h-3" | "h-3.5" | "h-4" | "h-5" | "h-6" | "h-7" | "h-8" | "h-9" | "h-10" | "h-11" | "h-12" | "h-14" | "h-16" | "h-20" | "h-24" | "h-28" | "h-32" | "h-36" | "h-40" | "h-44" | "h-48" | "h-52" | "h-56" | "h-60" | "h-64" | "h-72" | "h-80" | "h-96"
  | "h-1/2" | "h-1/3" | "h-2/3" | "h-1/4" | "h-2/4" | "h-3/4" | "h-1/5" | "h-2/5" | "h-3/5" | "h-4/5" | "h-1/6" | "h-2/6" | "h-3/6" | "h-4/6" | "h-5/6"
  | "h-full" | "h-screen" | "h-min" | "h-max" | "h-fit" | "h-auto"
  // Size (TW4)
  | "size-0" | "size-px" | "size-0.5" | "size-1" | "size-1.5" | "size-2" | "size-2.5" | "size-3" | "size-3.5" | "size-4" | "size-5" | "size-6" | "size-7" | "size-8" | "size-9" | "size-10" | "size-11" | "size-12" | "size-14" | "size-16" | "size-20" | "size-24" | "size-28" | "size-32" | "size-36" | "size-40" | "size-44" | "size-48" | "size-52" | "size-56" | "size-60" | "size-64" | "size-72" | "size-80" | "size-96"
  | "size-full" | "size-min" | "size-max" | "size-fit"
  // Min / Max Width
  | "min-w-0" | "min-w-full" | "min-w-min" | "min-w-max" | "min-w-fit"
  | "max-w-0" | "max-w-none" | "max-w-xs" | "max-w-sm" | "max-w-md" | "max-w-lg" | "max-w-xl" | "max-w-2xl" | "max-w-3xl" | "max-w-4xl" | "max-w-5xl" | "max-w-6xl" | "max-w-7xl" | "max-w-full" | "max-w-min" | "max-w-max" | "max-w-fit" | "max-w-prose"
  // Min / Max Height
  | "min-h-0" | "min-h-full" | "min-h-screen" | "min-h-min" | "min-h-max" | "min-h-fit"
  | "max-h-none" | "max-h-full" | "max-h-screen" | "max-h-min" | "max-h-max" | "max-h-fit"
  // Typography — Font Size
  | "text-xs" | "text-sm" | "text-base" | "text-lg" | "text-xl" | "text-2xl" | "text-3xl" | "text-4xl" | "text-5xl" | "text-6xl" | "text-7xl" | "text-8xl" | "text-9xl"
  // Typography — Font Weight
  | "font-thin" | "font-extralight" | "font-light" | "font-normal" | "font-medium" | "font-semibold" | "font-bold" | "font-extrabold" | "font-black"
  // Typography — Font Family
  | "font-sans" | "font-serif" | "font-mono"
  // Typography — Text Align
  | "text-left" | "text-center" | "text-right" | "text-justify" | "text-start" | "text-end"
  // Typography — Text Decoration
  | "underline" | "overline" | "line-through" | "no-underline"
  // Typography — Text Transform
  | "uppercase" | "lowercase" | "capitalize" | "normal-case"
  // Typography — Font Style
  | "italic" | "not-italic"
  // Typography — Whitespace
  | "whitespace-normal" | "whitespace-nowrap" | "whitespace-pre" | "whitespace-pre-line" | "whitespace-pre-wrap" | "whitespace-break-spaces"
  // Typography — Word Break
  | "break-normal" | "break-words" | "break-all" | "break-keep"
  // Typography — Text Overflow
  | "truncate" | "text-ellipsis" | "text-clip"
  // Typography — Line Height
  | "leading-3" | "leading-4" | "leading-5" | "leading-6" | "leading-7" | "leading-8" | "leading-9" | "leading-10"
  | "leading-none" | "leading-tight" | "leading-snug" | "leading-normal" | "leading-relaxed" | "leading-loose"
  // Typography — Letter Spacing
  | "tracking-tighter" | "tracking-tight" | "tracking-normal" | "tracking-wide" | "tracking-wider" | "tracking-widest"
  // Typography — List Style
  | "list-none" | "list-disc" | "list-decimal" | "list-inside" | "list-outside"
  // Typography — Text Color (命名色 + 色阶，支持 /opacity)
  | `text-${string}`
  // Background
  | "bg-inherit" | "bg-current" | "bg-transparent" | "bg-black" | "bg-white"
  | `bg-${string}`
  // Background — Gradient (TW4: bg-linear-to-*)
  | "bg-linear-to-t" | "bg-linear-to-tr" | "bg-linear-to-r" | "bg-linear-to-br" | "bg-linear-to-b" | "bg-linear-to-bl" | "bg-linear-to-l" | "bg-linear-to-tl"
  | "bg-gradient-to-t" | "bg-gradient-to-tr" | "bg-gradient-to-r" | "bg-gradient-to-br" | "bg-gradient-to-b" | "bg-gradient-to-bl" | "bg-gradient-to-l" | "bg-gradient-to-tl"
  // Background — Gradient Stops
  | `from-${string}` | `via-${string}` | `to-${string}`
  // Background — Size / Position / Repeat
  | "bg-auto" | "bg-cover" | "bg-contain"
  | "bg-center" | "bg-top" | "bg-bottom" | "bg-left" | "bg-right"
  | "bg-repeat" | "bg-no-repeat" | "bg-repeat-x" | "bg-repeat-y" | "bg-repeat-round" | "bg-repeat-space"
  | "bg-fixed" | "bg-local" | "bg-scroll"
  // Border — Width
  | "border" | "border-0" | "border-2" | "border-4" | "border-8"
  | "border-x" | "border-x-0" | "border-x-2" | "border-x-4" | "border-x-8"
  | "border-y" | "border-y-0" | "border-y-2" | "border-y-4" | "border-y-8"
  | "border-t" | "border-t-0" | "border-t-2" | "border-t-4" | "border-t-8"
  | "border-r" | "border-r-0" | "border-r-2" | "border-r-4" | "border-r-8"
  | "border-b" | "border-b-0" | "border-b-2" | "border-b-4" | "border-b-8"
  | "border-l" | "border-l-0" | "border-l-2" | "border-l-4" | "border-l-8"
  // Border — Color
  | `border-${string}`
  // Border — Style
  | "border-solid" | "border-dashed" | "border-dotted" | "border-double" | "border-hidden" | "border-none"
  // Border — Radius
  | "rounded-none" | "rounded-sm" | "rounded" | "rounded-md" | "rounded-lg" | "rounded-xl" | "rounded-2xl" | "rounded-3xl" | "rounded-full"
  | "rounded-t-none" | "rounded-t-sm" | "rounded-t" | "rounded-t-md" | "rounded-t-lg" | "rounded-t-xl" | "rounded-t-2xl" | "rounded-t-3xl" | "rounded-t-full"
  | "rounded-r-none" | "rounded-r-sm" | "rounded-r" | "rounded-r-md" | "rounded-r-lg" | "rounded-r-xl" | "rounded-r-2xl" | "rounded-r-3xl" | "rounded-r-full"
  | "rounded-b-none" | "rounded-b-sm" | "rounded-b" | "rounded-b-md" | "rounded-b-lg" | "rounded-b-xl" | "rounded-b-2xl" | "rounded-b-3xl" | "rounded-b-full"
  | "rounded-l-none" | "rounded-l-sm" | "rounded-l" | "rounded-l-md" | "rounded-l-lg" | "rounded-l-xl" | "rounded-l-2xl" | "rounded-l-3xl" | "rounded-l-full"
  // Effects
  | "shadow-sm" | "shadow" | "shadow-md" | "shadow-lg" | "shadow-xl" | "shadow-2xl" | "shadow-inner" | "shadow-none"
  | "opacity-0" | "opacity-5" | "opacity-10" | "opacity-15" | "opacity-20" | "opacity-25" | "opacity-30" | "opacity-35" | "opacity-40" | "opacity-45" | "opacity-50" | "opacity-55" | "opacity-60" | "opacity-65" | "opacity-70" | "opacity-75" | "opacity-80" | "opacity-85" | "opacity-90" | "opacity-95" | "opacity-100"
  | "ring-0" | "ring-1" | "ring-2" | "ring" | "ring-4" | "ring-8" | "ring-inset"
  | `ring-${string}`
  | "blur-none" | "blur-sm" | "blur" | "blur-md" | "blur-lg" | "blur-xl" | "blur-2xl" | "blur-3xl"
  // Z-Index
  | "z-0" | "z-10" | "z-20" | "z-30" | "z-40" | "z-50" | "z-auto"
  // Inset / Position Offsets (TW4: inset-* 替代 top-*/right-*/bottom-*/left-*)
  | "inset-0" | "inset-auto" | "inset-x-0" | "inset-x-auto" | "inset-y-0" | "inset-y-auto"
  | `inset-${string}` | `inset-x-${string}` | `inset-y-${string}`
  | `top-${string}` | `right-${string}` | `bottom-${string}` | `left-${string}`
  // Transform
  | "transform" | "transform-none" | "transform-gpu"
  | "origin-center" | "origin-top" | "origin-top-right" | "origin-right" | "origin-bottom-right" | "origin-bottom" | "origin-bottom-left" | "origin-left" | "origin-top-left"
  | `scale-${string}` | `scale-x-${string}` | `scale-y-${string}`
  | `rotate-${string}`
  | `translate-x-${string}` | `translate-y-${string}`
  | `skew-x-${string}` | `skew-y-${string}`
  // Transition / Animation
  | "transition" | "transition-none" | "transition-all" | "transition-colors" | "transition-opacity" | "transition-shadow" | "transition-transform"
  | "duration-0" | "duration-75" | "duration-100" | "duration-150" | "duration-200" | "duration-300" | "duration-500" | "duration-700" | "duration-1000"
  | "delay-0" | "delay-75" | "delay-100" | "delay-150" | "delay-200" | "delay-300" | "delay-500" | "delay-700" | "delay-1000"
  | "ease-linear" | "ease-in" | "ease-out" | "ease-in-out"
  | "animate-none" | "animate-spin" | "animate-ping" | "animate-pulse" | "animate-bounce"
  // Cursor
  | "cursor-auto" | "cursor-default" | "cursor-pointer" | "cursor-wait" | "cursor-text" | "cursor-move" | "cursor-not-allowed" | "cursor-none" | "cursor-help" | "cursor-progress" | "cursor-grab" | "cursor-grabbing"
  // Pointer Events / User Select
  | "pointer-events-none" | "pointer-events-auto"
  | "select-none" | "select-text" | "select-all" | "select-auto"
  // Resize
  | "resize" | "resize-none" | "resize-x" | "resize-y"
  // Aspect Ratio
  | "aspect-auto" | "aspect-square" | "aspect-video"
  // Columns
  | "columns-1" | "columns-2" | "columns-3" | "columns-4" | "columns-5" | "columns-6" | "columns-7" | "columns-8" | "columns-9" | "columns-10" | "columns-11" | "columns-12" | "columns-auto"
  // Scroll
  | "scroll-auto" | "scroll-smooth"
  | "snap-none" | "snap-x" | "snap-y" | "snap-both" | "snap-start" | "snap-end" | "snap-center" | "snap-align-none" | "snap-normal" | "snap-always"
  // Touch
  | "touch-auto" | "touch-none" | "touch-pan-x" | "touch-pan-left" | "touch-pan-right" | "touch-pan-y" | "touch-pan-up" | "touch-pan-down" | "touch-pinch-zoom" | "touch-manipulation"
  // Will Change
  | "will-change-auto" | "will-change-scroll" | "will-change-contents" | "will-change-transform"
  // Contain
  | "contain-none" | "contain-strict" | "contain-content" | "contain-size" | "contain-inline-size" | "contain-layout" | "contain-style" | "contain-paint"
  // Arbitrary values: w-[1200px], text-[#ff0000], bg-[rgb(0,0,0)] etc.
  | (string & {})
  // Variant prefixes: sm:, md:, lg:, xl:, 2xl:, dark:, hover:, focus:, etc.
  | `${"sm" | "md" | "lg" | "xl" | "2xl" | "dark" | "hover" | "focus" | "active" | "disabled" | "first" | "last" | "odd" | "even" | "group-hover" | "peer-hover" | "focus-within" | "focus-visible" | "motion-safe" | "motion-reduce" | "portrait" | "landscape" | "print" | "rtl" | "ltr" | "empty" | "before" | "after" | "placeholder" | "selection" | "marker" | "file" | "first-line" | "first-letter" | "backdrop" | "aria" | "data" | "open" | "checked" | "indeterminate" | "default" | "required" | "valid" | "invalid" | "in-range" | "out-of-range" | "placeholder-shown" | "autofill" | "read-only" | "enabled" | "optional" | "target" | "not-"}:${string}`;

interface CSSProperties {
  [key: string]: string | number | undefined;
}

interface HTMLAttributes {
  tw?: TailwindCSS;
  className?: string;
  class?: string;
  id?: string;
  style?: CSSProperties | string;
  title?: string;
  role?: string;
  tabIndex?: number;
  slot?: string;
  lang?: string;
  dir?: "ltr" | "rtl" | "auto";
  hidden?: boolean;
  draggable?: boolean;
  contentEditable?: boolean | "true" | "false" | "inherit";
  spellCheck?: boolean;
  accessKey?: string;
  // ARIA
  "aria-label"?: string;
  "aria-labelledby"?: string;
  "aria-describedby"?: string;
  "aria-hidden"?: boolean | "true" | "false";
  "aria-live"?: "off" | "assertive" | "polite";
  "aria-expanded"?: boolean | "true" | "false";
  "aria-controls"?: string;
  "aria-current"?: "page" | "step" | "location" | "date" | "time" | "true" | "false";
  "aria-disabled"?: boolean | "true" | "false";
  "aria-selected"?: boolean | "true" | "false";
  "aria-checked"?: boolean | "true" | "false" | "mixed";
  "aria-valuemin"?: number;
  "aria-valuemax"?: number;
  "aria-valuenow"?: number;
  "aria-valuetext"?: string;
  "aria-required"?: boolean | "true" | "false";
  "aria-invalid"?: boolean | "true" | "false" | "grammar" | "spelling";
  "aria-errormessage"?: string;
  "aria-roledescription"?: string;
  // Events
  onClick?: (event: any) => void;
  onInput?: (event: any) => void;
  onChange?: (event: any) => void;
  onSubmit?: (event: any) => void;
  onLoad?: (event: any) => void;
  onError?: (event: any) => void;
  onFocus?: (event: any) => void;
  onBlur?: (event: any) => void;
  // Data / custom attributes
  [key: `data-${string}`]: string | number | boolean | undefined;
  // Children
  children?: ReactNode;
  dangerouslySetInnerHTML?: { __html: string };
  // Ref
  ref?: any;
  key?: string | number;
}

interface AnchorHTMLAttributes extends HTMLAttributes {
  href?: string;
  target?: "_blank" | "_self" | "_parent" | "_top" | string;
  rel?: string;
  download?: string | boolean;
  hrefLang?: string;
  type?: string;
}

interface ImgHTMLAttributes extends HTMLAttributes {
  src?: string;
  alt?: string;
  width?: number | string;
  height?: number | string;
  loading?: "lazy" | "eager";
  decoding?: "async" | "auto" | "sync";
  crossOrigin?: "anonymous" | "use-credentials" | "";
}

interface InputHTMLAttributes extends HTMLAttributes {
  type?: "text" | "password" | "email" | "number" | "tel" | "url" | "search" | "date" | "time" | "datetime-local" | "month" | "week" | "color" | "file" | "range" | "checkbox" | "radio" | "submit" | "reset" | "button" | "image" | "hidden";
  name?: string;
  value?: string | number | string[];
  defaultValue?: string | number | string[];
  placeholder?: string;
  disabled?: boolean;
  readOnly?: boolean;
  required?: boolean;
  autoFocus?: boolean;
  autoComplete?: "on" | "off" | string;
  min?: number | string;
  max?: number | string;
  step?: number | string;
  pattern?: string;
  multiple?: boolean;
  accept?: string;
  checked?: boolean;
  defaultChecked?: boolean;
  form?: string;
  maxLength?: number;
  minLength?: number;
  size?: number;
}

interface ButtonHTMLAttributes extends HTMLAttributes {
  type?: "submit" | "reset" | "button";
  name?: string;
  value?: string | number | string[];
  disabled?: boolean;
  form?: string;
  formAction?: string;
  formMethod?: string;
  formTarget?: string;
  autoFocus?: boolean;
}

interface FormHTMLAttributes extends HTMLAttributes {
  action?: string;
  method?: "get" | "post" | string;
  encType?: "application/x-www-form-urlencoded" | "multipart/form-data" | "text/plain";
  target?: string;
  noValidate?: boolean;
  autoComplete?: "on" | "off";
  name?: string;
}

interface SelectHTMLAttributes extends HTMLAttributes {
  name?: string;
  value?: string | number | string[];
  defaultValue?: string | number | string[];
  disabled?: boolean;
  required?: boolean;
  multiple?: boolean;
  size?: number;
  form?: string;
  autoComplete?: "on" | "off" | string;
}

interface OptionHTMLAttributes extends HTMLAttributes {
  value?: string | number;
  selected?: boolean;
  disabled?: boolean;
  label?: string;
}

interface TextareaHTMLAttributes extends HTMLAttributes {
  name?: string;
  value?: string;
  defaultValue?: string;
  placeholder?: string;
  disabled?: boolean;
  readOnly?: boolean;
  required?: boolean;
  rows?: number;
  cols?: number;
  wrap?: "hard" | "soft";
  form?: string;
  maxLength?: number;
  minLength?: number;
  autoFocus?: boolean;
}

interface LabelHTMLAttributes extends HTMLAttributes {
  htmlFor?: string;
  form?: string;
}

interface TableHTMLAttributes extends HTMLAttributes {
  cellPadding?: number | string;
  cellSpacing?: number | string;
  summary?: string;
}

interface TdHTMLAttributes extends HTMLAttributes {
  colSpan?: number;
  rowSpan?: number;
  headers?: string;
}

interface ThHTMLAttributes extends TdHTMLAttributes {
  scope?: "row" | "col" | "rowgroup" | "colgroup";
  abbr?: string;
}

interface ColgroupHTMLAttributes extends HTMLAttributes {
  span?: number;
}

interface ColHTMLAttributes extends HTMLAttributes {
  span?: number;
}

interface IframeHTMLAttributes extends HTMLAttributes {
  src?: string;
  srcDoc?: string;
  name?: string;
  width?: number | string;
  height?: number | string;
  allow?: string;
  allowFullScreen?: boolean;
  loading?: "lazy" | "eager";
  sandbox?: string;
  referrerPolicy?: string;
}

interface VideoHTMLAttributes extends HTMLAttributes {
  src?: string;
  poster?: string;
  width?: number | string;
  height?: number | string;
  controls?: boolean;
  autoPlay?: boolean;
  loop?: boolean;
  muted?: boolean;
  playsInline?: boolean;
  preload?: "auto" | "metadata" | "none";
}

interface AudioHTMLAttributes extends HTMLAttributes {
  src?: string;
  controls?: boolean;
  autoPlay?: boolean;
  loop?: boolean;
  muted?: boolean;
  preload?: "auto" | "metadata" | "none";
}

interface SourceHTMLAttributes extends HTMLAttributes {
  src?: string;
  srcSet?: string;
  type?: string;
  media?: string;
  width?: number | string;
  height?: number | string;
  sizes?: string;
}

interface LinkHTMLAttributes extends HTMLAttributes {
  rel?: string;
  href?: string;
  type?: string;
  media?: string;
  crossOrigin?: "anonymous" | "use-credentials" | "";
  sizes?: string;
}

interface MetaHTMLAttributes extends HTMLAttributes {
  name?: string;
  content?: string;
  httpEquiv?: string;
  charSet?: string;
  property?: string;
}

interface ScriptHTMLAttributes extends HTMLAttributes {
  src?: string;
  type?: string;
  async?: boolean;
  defer?: boolean;
  crossOrigin?: "anonymous" | "use-credentials" | "";
  noModule?: boolean;
  integrity?: string;
}

interface BaseHTMLAttributes extends HTMLAttributes {
  href?: string;
  target?: string;
}

interface SlotHTMLAttributes extends HTMLAttributes {
  name?: string;
}

interface SVGAttributes extends HTMLAttributes {
  xmlns?: string;
  viewBox?: string;
  fill?: string;
  stroke?: string;
  strokeWidth?: number | string;
  strokeLinecap?: "butt" | "round" | "square";
  strokeLinejoin?: "miter" | "round" | "bevel";
  strokeDasharray?: string;
  strokeDashoffset?: number | string;
  strokeOpacity?: number | string;
  opacity?: number | string;
  d?: string;
  cx?: number | string;
  cy?: number | string;
  r?: number | string;
  rx?: number | string;
  ry?: number | string;
  x?: number | string;
  y?: number | string;
  x1?: number | string;
  y1?: number | string;
  x2?: number | string;
  y2?: number | string;
  width?: number | string;
  height?: number | string;
  points?: string;
  transform?: string;
  xlinkHref?: string;
  preserveAspectRatio?: string;
  clipPath?: string;
  mask?: string;
  gradientUnits?: string;
  offset?: number | string;
  stopColor?: string;
  stopOpacity?: number | string;
}
