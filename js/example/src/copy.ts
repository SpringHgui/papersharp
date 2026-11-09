// 站点文案。zh 是形状来源，en 必须与之同构（类型约束在这里兜底）。
// 数字口径全部取自 README.md / PERF_REPORT.md §11，别在这儿随手改数。

export const zh = {
  meta: {
    title: "PaperSharp — C#/.NET 高性能 HTML 转 PDF 与图片引擎（无需浏览器）",
    description:
      "纯托管 C# + Skia 自绘的 PDF/图片渲染引擎：把 HTML/CSS 直接编译成矢量 PDF、PNG、SVG 等，无需浏览器、无 headless Chrome。300 张并发 15.6 ms/张、64 docs/s、常驻 148 MB；同一颗引擎跑在 .NET、Node、Bun 与浏览器 WASM 里。",
  },
  nav: {
    playground: "Playground",
    bench: "性能",
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
    status: {
      booting: "正在加载 .NET WASM 引擎…",
      rendering: "渲染中…",
      ready: "就绪",
      error: "引擎报错",
      pdfNote: "预览是整幅位图，下载得到分页 PDF",
    },
    stats: {
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
  bench: {
    title: "性能实测（可复现）",
    intro:
      "五方同机实测：300 张并发中文票据、固定 4 worker、Release 构建。数字取自可核查的 v1.0.4 快照，不是挑出来最好的一次；三档 100/200/300 全部成功，无跳过、无补零。",
    kpis: [
      { value: "15.6 ms", label: "平均每张 · 300 张并发" },
      { value: "64.0 docs/s", label: "吞吐 · 约 Chromium 系 3.3×" },
      { value: "61.6 ms", label: "P95 延迟" },
      { value: "148 MB", label: "RSS 峰值 · 约 Chromium 系 1/9" },
    ],
    columns: {
      name: "引擎（300 张并发档）",
      runtime: "内核",
      metrics: ["冷启动 ms", "热启动 ms", "冷/热", "平均/张 ms", "吞吐 docs/s", "P95 ms", "RSS 峰值 MB", "产物 KB"],
    },
    charts: [
      { file: "01-cold-start.svg", alt: "冷启动平均耗时（ms，越低越好）" },
      { file: "03-avg-per-doc.svg", alt: "300 张并发平均每张耗时（ms，越低越好）" },
      { file: "05-throughput.svg", alt: "300 张并发吞吐（docs/s，越高越好）" },
      { file: "06-rss-peak.svg", alt: "300 张并发 RSS 峰值（MB，越低越好）" },
    ],
    note: "环境：Windows 10 x64 · 16 逻辑核 · .NET 10.0.10 · Chrome 156；语料为 4.8 KB 中文发票，冷/热各 20 次取平均。IronPdf 为 trial 授权（输出含水印），仅作对照。",
    source: "数字与口径：",
    allCharts: "全部 8 张图",
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
    title: "PaperSharp — C#/.NET HTML-to-PDF engine, no browser required",
    description:
      "A PDF/image engine written in managed C# on Skia: compile HTML/CSS straight into vector PDF, PNG, SVG and more — no browser, no headless Chrome. 15.6 ms per document at 300 concurrent, 64 docs/s, 148 MB RSS; one engine for .NET, Node, Bun and browser WASM.",
  },
  nav: {
    playground: "Playground",
    bench: "Benchmarks",
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
    status: {
      booting: "Loading the .NET WASM engine…",
      rendering: "Rendering…",
      ready: "Ready",
      error: "Engine error",
      pdfNote: "Preview is one flat bitmap; the download is a paginated PDF",
    },
    stats: {
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
  bench: {
    title: "Measured performance, reproducible",
    intro:
      "Five engines on one machine: 300 concurrent Chinese invoices, 4 workers, Release builds. Numbers come from the auditable v1.0.4 snapshot — not a best-of run; all three batches (100/200/300) finished with zero skips and zero filler.",
    kpis: [
      { value: "15.6 ms", label: "per document · 300 concurrent" },
      { value: "64.0 docs/s", label: "throughput · ~3.3× the Chromium stack" },
      { value: "61.6 ms", label: "P95 latency" },
      { value: "148 MB", label: "RSS peak · ~1/9 of the Chromium stack" },
    ],
    columns: {
      name: "Engine (300 concurrent)",
      runtime: "Runtime",
      metrics: ["Cold ms", "Warm ms", "Cold/warm", "ms/doc", "docs/s", "P95 ms", "RSS peak MB", "Output KB"],
    },
    charts: [
      { file: "01-cold-start.svg", alt: "Cold start mean latency (ms, lower is better)" },
      { file: "03-avg-per-doc.svg", alt: "Average ms per document at 300 concurrent (lower is better)" },
      { file: "05-throughput.svg", alt: "Throughput in documents per second at 300 concurrent (higher is better)" },
      { file: "06-rss-peak.svg", alt: "Peak process-tree RSS at 300 concurrent (MB, lower is better)" },
    ],
    note: "Environment: Windows 10 x64 · 16 logical cores · .NET 10.0.10 · Chrome 156; a 4.8 KB Chinese invoice, cold and warm each averaged over 20 runs. IronPdf runs under a trial license (watermarked output) and is shown for reference only.",
    source: "Numbers and methodology:",
    allCharts: "all eight charts",
  },
  footer: {
    license: "MIT license",
    nuget: "NuGet",
    npm: "npm",
    note: "Every artifact on this page is rendered live by the in-browser .NET WASM engine.",
  },
};
