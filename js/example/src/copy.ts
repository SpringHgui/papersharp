// 站点文案。zh 是形状来源，en 必须与之同构（类型约束在这里兜底）。
// 数字口径全部取自 README.md / PERF_REPORT.md §11，别在这儿随手改数。

export const zh = {
  meta: {
    title: "PaperSharp — 把 HTML/CSS 直接编译成 PDF 与高清图片",
    description:
      "纯托管 C# + Skia 自绘的 PDF/图片渲染引擎：无需浏览器、无 headless Chrome。同一颗引擎跑在 .NET、Node、Bun 与浏览器 WASM 里。",
  },
  nav: {
    playground: "Playground",
    features: "能力",
    usage: "用法",
    github: "GitHub",
  },
  hero: {
    eyebrow: "HTML + CSS → PDF · PNG · JPEG · WebP · SVG · ICO",
    title: "把 HTML/CSS 直接编译成 PDF 与高清图片",
    subtitle:
      "纯托管 C# 加 Skia 自绘，不装 Chromium、不起子进程、不开调试端口。发票、报表、合同这类财务级文档要的是一致与可预测。",
    ctaPrimary: "试 Playground",
    ctaSecondary: "看能力清单",
    installTitle: ".NET",
    installCmd: "dotnet add package PaperSharp",
    npmTitle: "JS / 浏览器",
    npmCmd: "npm i papersharp",
  },
  playground: {
    title: "Playground",
    subtitle: "左边改，右边就是浏览器里这颗 WASM 引擎的真实产物 —— 没有任何服务端参与。",
    sample: "样例",
    html: "HTML",
    css: "附加 CSS",
    controls: {
      width: "宽度",
      scale: "倍率",
      format: "格式",
      quality: "质量",
      paper: "纸张",
    },
    actions: {
      render: "渲染",
      download: "下载",
      reset: "还原样例",
    },
    view: {
      engine: "引擎",
      side: "并排浏览器",
      overlay: "叠加",
    },
    status: {
      booting: "正在加载 .NET WASM 引擎（首次约 25 MB）…",
      rendering: "渲染中…",
      ready: "就绪",
      error: "引擎报错",
      browserLabel: "浏览器排版",
      engineLabel: "PaperSharp 排版",
      pdfNote: "预览是整幅位图，下载得到分页 PDF",
      overlayNote:
        "重影是屏幕字体与打印字体每行约 0.5px 的行盒度量差随页深累积，不是渲染缺陷；把不透明度拉满即只看引擎层",
    },
    stats: {
      time: "引擎耗时",
      size: "体积",
      dims: "尺寸",
    },
  },
  features: {
    title: "为什么选它",
    items: [
      {
        name: "零浏览器依赖",
        desc: "不装 Chromium、不起子进程、不开调试端口。一个类库嵌进 .NET 进程就能渲染，容器镜像不用塞 300 MB 的无头浏览器。",
      },
      {
        name: "打印级分页语义",
        desc: "天生面向分页媒体：@page 尺寸与页边距、页眉页脚 margin box、超页自动回流、表格跨页续且 thead 每页重放、break-inside / orphans / widows。",
      },
      {
        name: "中文原生排版",
        desc: "CJK 逐字折行、用字级字体子集嵌入 —— 纯文字单页 PDF 只有 50–120 KB，文字可选中可搜索，页码支持 counter(page, cjk-ideographic)。",
      },
      {
        name: "服务端吞吐",
        desc: "单页 PDF 中位约 30 ms，单线程约 40 页/秒；线程安全，多请求可并发进同一进程，常驻只是一个类库的体量。",
      },
      {
        name: "确定性输出",
        desc: "默认离线渲染，同一输入恒定产物。财务文档要的是可复算，不是每次像素抖动一点。",
      },
      {
        name: "浏览器里也是它",
        desc: "npm 包内置 .NET WASM 产物，Node、Bun、浏览器共用同一颗引擎 —— 你现在在这个页面上看到的每一张图，都是本机 WASM 算出来的。",
      },
    ],
  },
  usage: {
    title: "30 秒上手",
    tabs: {
      csharp: "C# / .NET",
      jsx: "JSX（Satori 兼容）",
      browser: "浏览器 WASM",
    },
    notes: {
      csharp: "一个静态入口，四种产物。width 是 CSS 像素，scale 是倍率。",
      jsx: "tw 属性吃 Tailwind 工具类子集，产物与 .NET 端逐像素同源。",
      browser: "npm 包已带 wasm/_framework，静态托管即可，无需服务端。",
    },
  },
  formats: {
    title: "一套管线，六种产物",
    items: [
      { name: "PDF", desc: "矢量、可选中文字、书签与链接注释、自动元数据" },
      { name: "PNG", desc: "透明通道，2× 高清出 OG 图" },
      { name: "JPEG", desc: "质量可调，照片型卡片" },
      { name: "WebP", desc: "同画质更小体积" },
      { name: "SVG", desc: "渐变与裁剪映射为 SVG 原生图元" },
      { name: "ICO", desc: "favicon，多尺寸打包" },
    ],
  },
  footer: {
    license: "MIT 授权",
    nuget: "NuGet",
    npm: "npm",
    note: "本页所有产物由浏览器内的 .NET WASM 引擎实时渲染。",
  },
};

export type Copy = typeof zh;

export const en: Copy = {
  meta: {
    title: "PaperSharp — compile HTML/CSS straight into PDF and crisp images",
    description:
      "A PDF/image engine written in managed C# on Skia: no browser, no headless Chrome. The same engine runs in .NET, Node, Bun and browser WASM.",
  },
  nav: {
    playground: "Playground",
    features: "Capabilities",
    usage: "Usage",
    github: "GitHub",
  },
  hero: {
    eyebrow: "HTML + CSS → PDF · PNG · JPEG · WebP · SVG · ICO",
    title: "Compile HTML/CSS straight into PDF and crisp images",
    subtitle:
      "Managed C# drawing on Skia — no Chromium install, no child process, no debug port. Invoices, reports and contracts need output that is consistent and predictable.",
    ctaPrimary: "Open Playground",
    ctaSecondary: "See capabilities",
    installTitle: ".NET",
    installCmd: "dotnet add package PaperSharp",
    npmTitle: "JS / browser",
    npmCmd: "npm i papersharp",
  },
  playground: {
    title: "Playground",
    subtitle: "Edit on the left; the right side is this WASM engine's real output, running in your browser — no server involved.",
    sample: "Sample",
    html: "HTML",
    css: "Extra CSS",
    controls: {
      width: "Width",
      scale: "Scale",
      format: "Format",
      quality: "Quality",
      paper: "Paper",
    },
    actions: {
      render: "Render",
      download: "Download",
      reset: "Reset sample",
    },
    view: {
      engine: "Engine",
      side: "Next to browser",
      overlay: "Overlay",
    },
    status: {
      booting: "Loading the .NET WASM engine (about 25 MB on first run)…",
      rendering: "Rendering…",
      ready: "Ready",
      error: "Engine error",
      browserLabel: "Browser layout",
      engineLabel: "PaperSharp layout",
      pdfNote: "Preview is one flat bitmap; the download is a paginated PDF",
      overlayNote:
        "The ghosting is the ~0.5px-per-line line-box metric gap between screen and print fonts accumulating down the page, not a rendering defect; at 100% opacity you see the engine layer alone",
    },
    stats: {
      time: "Engine time",
      size: "Size",
      dims: "Dimensions",
    },
  },
  features: {
    title: "Why pick it",
    items: [
      {
        name: "No browser dependency",
        desc: "No Chromium, no child process, no debug port. One library inside your .NET process does the rendering — your container image stays free of a 300 MB headless browser.",
      },
      {
        name: "Print-grade paged media",
        desc: "Built for paged output: @page size and margins, header/footer margin boxes, automatic reflow across pages, tables that continue with thead replayed on every page, break-inside / orphans / widows.",
      },
      {
        name: "First-class CJK",
        desc: "Per-character CJK line breaking and glyph-level font subsetting — a text-only single page PDF is 50–120 KB, its text stays selectable and searchable, page numbers support counter(page, cjk-ideographic).",
      },
      {
        name: "Server throughput",
        desc: "Median single-page PDF around 30 ms, roughly 40 pages/second on one thread; thread-safe, so concurrent requests share one process with no V8 render-tree overhead.",
      },
      {
        name: "Deterministic output",
        desc: "Offline rendering by default: the same input always produces the same bytes. Financial documents need to be recomputable, not pixel-drifting.",
      },
      {
        name: "Same engine in the browser",
        desc: "The npm package ships the .NET WASM build, so Node, Bun and the browser share one engine — every image on this page is computed locally in your browser.",
      },
    ],
  },
  usage: {
    title: "Up and running in 30 seconds",
    tabs: {
      csharp: "C# / .NET",
      jsx: "JSX (Satori-compatible)",
      browser: "Browser WASM",
    },
    notes: {
      csharp: "One static entry point, four outputs. width is CSS pixels, scale is the multiplier.",
      jsx: "The tw attribute takes a Tailwind utility subset; pixels match the .NET side.",
      browser: "The npm package already contains wasm/_framework — static hosting is enough, no server.",
    },
  },
  formats: {
    title: "One pipeline, six outputs",
    items: [
      { name: "PDF", desc: "Vector, selectable text, bookmarks and link annotations, automatic metadata" },
      { name: "PNG", desc: "Alpha channel, 2× crisp OG images" },
      { name: "JPEG", desc: "Adjustable quality for photographic cards" },
      { name: "WebP", desc: "Smaller files at the same quality" },
      { name: "SVG", desc: "Gradients and clipping mapped to native SVG primitives" },
      { name: "ICO", desc: "Favicons, multi-size packing" },
    ],
  },
  footer: {
    license: "MIT license",
    nuget: "NuGet",
    npm: "npm",
    note: "Every artifact on this page is rendered live by the in-browser .NET WASM engine.",
  },
};
