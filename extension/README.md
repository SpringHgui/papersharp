# PaperSharp 浏览器插件（MV3）

在**任意网页**右上角放一个「导出」按钮，把当前页面导出成 PaperSharp 引擎支持的全部格式：
**PDF / PNG / JPEG / WebP / SVG / ICO**。渲染 100% 发生在本机 .NET WASM 里——不截图、不上传、不需要服务端。

```
任意网页 ──[content script]──> 快照（DOM + 生效样式 + 图片 data URI）
                                    │ 分片 sendMessage
                                    ▼
              [offscreen 隐藏扩展页]  PaperSharp.Wasm（AngleSharp + Skia + HarfBuzz）
                                    │ Blob → blob: URL
                                    ▼
              [service worker]  chrome.downloads → 浏览器下载文件夹
```

## 装起来

```bat
:: 1) 生成 wasm 产物 + 中文字体（首次或改了 C# 之后）
node extension/build.mjs --publish

:: 2) 浏览器加载
::    Edge/Chrome → 扩展页 → 开发者模式 → 加载已解压的扩展程序 → 选 extension 目录
```

`extension/papersharp/wasm/` 与 `extension/fonts/` 是构建输入，已在 `.gitignore` 里（与 `js/papersharp/wasm` 同策略）。
运行时约 18 MB wasm + 3.5 MB 字体子集，首次导出会冷启动 2–5 秒，之后常驻在 offscreen 文档里（空闲 3 分钟后自动关闭）。

## 用法

页面右上角点「导出」→ 选格式 → 调参数 → 导出。

| 参数 | 生效格式 | 含义 |
| --- | --- | --- |
| 输出宽度 px | 全部 | 默认 = 页面视口宽度。改小/改大是**整页等比缩放**（把浏览器算好的 px 按比例下发），不重新回流、不裁切 |
| 像素倍率 | PNG/JPEG/WebP/ICO | 设备缩放（2× ≈ retina），布局仍按 CSS px |
| 质量 | JPEG/WebP | 编码器质量 30–100 |
| 分页比例 | PDF | 页宽 = 输出宽度，页高按 A4(1:1.414) / Letter(1:1.294) 推出 |
| 动画取帧秒数 | 全部 | 留空 = 元素自身样式；填 `1` = 取 `@keyframes` 第 1 秒那一帧（引擎 M9 的确定性取帧） |
| 内联图片 | 全部 | 关掉则所有图片留白，快照体积和耗时大幅下降 |
| 文件名 | 全部 | 默认取 `document.title` |

也可以在页面里程序化触发（同一个入口也是端到端测试用的）：

```js
addEventListener("message", (e) => e.data?.type === "papersharp:export-result" && console.log(e.data));
postMessage({ type: "papersharp:export", format: "png", width: 1200, scale: 2 }, "*");
```

## 为什么是「计算样式展平」

PaperSharp 是离线渲染引擎，只实现 CSS 的一个子集（无 `@layer` / `@container` / CSS 嵌套 / `@media` /
`overflow` / `transform` / 网络图）。把真实网站的 CSS 原样丢进去，现代语法会被**静默丢弃**，结果是整页裸样式。

所以快照器反过来做：让浏览器把级联、媒体查询、`@layer`、`var()`/`calc()`、阴影 DOM 全部解析完，
我们只把**每个元素最终生效的那点属性**写成扁平的 `.ps-N{...}` 规则——正好落在引擎吃得下的子集上。
相对 URL、`currentColor`、used 宽高也都由浏览器算好了。可继承属性与父值相同就不重复下发，
再按「样式签名」去重，一个几千元素的真实页面通常压到 40–60 组规则、200–300 KB。

几个具体取舍：

- **`overflow:hidden`** → 引擎没有 overflow，改发已实现的 `clip-path: inset(0px)` 近似（实测带 `round` 的 inset 解析不了，所以不带圆角）。
- **`background-image: url()`** → 引擎**完全没有**这条路径（`ComputedStyle.Background` 只存 `GradientSpec`，实测只画渐变）。快照器把 url 层合成成绝对定位的 `<img>` 铺在内容下面，尺寸用父盒的 used 值（不用 `100%`：父高若是 `min-height`，`%` 会退化成图片原始尺寸而溢出）。
- **块容器高度**（仅 PDF 的流内路径）→ 浏览器算出的值当 `min-height`（下限）而不是 `height`（钉死）。钉死后一旦换行数不同，后面内容会整片重叠。只有替换元素、绝对定位、需要裁切的盒子才钉死。
- **阴影 DOM** → 开放 shadow root 直接展平其内部元素（浏览器已解析完 `::slotted` 与宿主样式），`<slot>` 用 `assignedNodes()` 取真实内容。
- **中文字体** → wasm 里没有系统字体，offscreen 启动时先把打包的雅黑子集装成引擎的字形级兜底（`InstallSubsetFont`），否则网页里任何未注册 `font-family` 的中文都是豆腐块。

## 钉死（absolute freeze）——真实网站保真度的主开关

只展平样式还不够：引擎的 flex/grid 是近似实现，`fixed`/`sticky` 一律当 `static`，
真实网站（Bootstrap 控制台这类）因此侧栏压正文、卡片整片错位。解法是把**浏览器算好的几何**直接搬过去：

- 每个块级盒按 `getBoundingClientRect()` 相对「父盒 padding box」发 `position:absolute; left/top/width/height; box-sizing:border-box`，
  父盒若不是定位元素，就补一条后发的 `.{父类}{position:relative}`（同类名同属性，后者覆盖，且 `relative` 不挪动父盒本身）。
- `position:fixed` 的元素要补滚动量：它锚在视口上，而 DOM 父盒的文档坐标 = 视口 rect + `scrollY`。
  不补这一刀，用户滚动后再导出就会把固定顶栏钉到滚动位置那么远（`--scroll 400` 已覆盖该场景）。
- **表格只钉位置**（`left/top/width`，`display:table` 与高度留给引擎）：完全不钉会让整张表从父盒顶部开始流（实测表跳到页首压住标题），
  钉死高度又会废掉自动行高。
- 行内片段不钉（多行内联钉成一块会把文字挤叠）。
- **PDF 不深钉**：`deepFreeze:false` → 只在 `fixed/sticky/absolute` 与 flex/grid 子女处钉住，其余留在常规流里，
  这样长文才能跨页回流（实测长文样张仍 10 页）。PNG/JPEG/WebP/SVG/ICO 是单张长图，走整页钉死 = 版式快照。

钉死的代价：盒内文字若被引擎排得比浏览器高（拉丁字形差异，见下），会溢出到钉死的下一个盒子；这是残差的唯一来源。

## 速度

导出耗时按段拆开（面板与 `e2e-browser.mjs` 都会打印），实测 Bootstrap 控制台样张：

| 段 | 优化前 | 现在 |
| --- | --- | --- |
| 引擎渲染 | 10.9 s | **3.0–3.5 s** |
| 展平（含 `getComputedStyle` 562 次） | 0.4 s | 0.1 s |
| 传快照 / 冷启动 | 0.3 s / 0.7 s | 0.1 s / 0.6 s |

引擎那一段的大头是 AngleSharp 每个元素跑 2–4 轮层叠（实测 `GetComputedStyle` 1.67 ms/元素、`GetDeclarations` 1.42 ms/元素）。
两处改动把它压下来：快照里的值全是浏览器算好的 used px（没有 `%`/`calc()`/层叠歧义），
所以导出请求带 `precomputed:true` 让引擎**跳过「声明值」那一轮**；同时伪元素规则先扫一遍 CSS 建 class/id/tag 作用域索引，
匹配不上当前元素的直接跳过。定位工具：`tools/SnapshotProfile`（原生分段计时 + 单价基准）、`extension/test/bench-snapshot.mjs`。

## 已知限制（如实）

引擎侧（见根目录 `README.md` / `ROADMAP.md` 的完整清单）：

- 无 `transform`：靠 `translate()` 做位移/居中的元素会回到未变换位置
- 无 `background-size` / `position` / `repeat`：合成出来的背景图按盒子尺寸拉伸
- 无 `font-style: italic`、无 `::marker`（列表符号丢失，`display:list-item` 退化为 block）
- `@media` 由浏览器在展平时解析掉，因此**不会**随输出宽度重新回流
- 只导出顶层文档：`<iframe>`、跨文件 SVG 雪碧图（`<use href="…#id">`）、视频帧不导出，保留占位盒
- 网页字体（woff2/woff）不加载——Skia 侧读不了，统一落到打包的雅黑子集，网页自定义字形一律还原成子集样式
- **拉丁文本量宽比浏览器宽约 1%**（实测同一份 Microsoft YaHei @12px/500 量「SAVED REPORTS」：
  浏览器 canvas 94.89 px，Skia 95.96 px）。展平出来的盒宽是浏览器的 shrink-to-fit 值，于是这类盒子在引擎里
  会多出 1 行（实测 Bootstrap 侧栏 "SAVED REPORTS" 压到下一项上）。根因是 Skia 按 hinting 后的整数字距累加，
  而浏览器用线性（未 hint）度量；`SKPaint.SubpixelText` 实测对 `MeasureText` 无影响（已回退），
  需要另找 Skia 侧的线性度量开关。中文/CJK 两侧同字体同量宽，无此问题

工程侧：

- 品牌版 Chrome 会忽略 `--load-extension`（实测只注册内置组件扩展），端到端测试用 **Edge**（同一套 Chromium/MV3 代码）
- 单条 `chrome.runtime.sendMessage` 有大小上限，快照按 1 MB 分片传输；渲染产物只在 offscreen 里做成 Blob，SW 只接一个 `blob:` URL（实测 SW 里没有 `URL.createObjectURL`）
- offscreen 的 `reasons` 枚举各内核版本不一致（实测 Edge 153 已无 `WASM_SCRIPTING`），启动时按候选依次试到被接受

## 测试

```bat
:: 不起浏览器：验证 wasm 包能拉起、子集字体兜底、6 种格式都出字节
node extension/test/smoke-node.mjs

:: 起浏览器：装载扩展 → 打开本地测试页（含跨源 CSS/图片、阴影 DOM、canvas、iframe、@layer、媒体查询、动画）
::            → 逐个格式导出 → 核对 chrome.downloads 落地文件
node extension/test/e2e-browser.mjs --diag
node extension/test/e2e-browser.mjs --page long-article --formats pdf   :: 分页/书签（钉死不深传，仍 10 页）

:: 真实网站 + 把快照落盘（离线复现/基准的输入）：--url 直连站点，--dump 存 out/snapshot.{html,css}
::   --width 不给就是视口宽（k=1，与面板默认一致）；--scroll 400 验证滚动后导出的 fixed 元素摆位
node extension/test/e2e-browser.mjs --url https://getbootstrap.com/docs/5.3/examples/dashboard/ --formats png --dump
node extension/test/render-snapshot.mjs snapshot 对比图 1406           :: 把落盘快照单独渲染成 PNG 目视核对
node extension/test/bench-snapshot.mjs                                 :: 分段基准：整页/去 svg/去文本/截前 N 个节点
node extension/test/diag-browser.mjs edge headless                     :: 只查扩展能否注入
dotnet run -c Release --project tools/SnapshotProfile -- extension/test/out/snapshot.html extension/test/out/snapshot.css 1406 2 micro
```

测试页与静态资源在 `extension/test/fixtures/` 与 `serve.mjs`（两个端口 = 两个 origin，专门制造跨源场景；
PNG 由 `serve.mjs` 现算现发，不入库）。产物落在 `extension/test/out/`。

## 目录

```
extension/
├── manifest.json          # MV3：<all_urls> content script + offscreen/downloads/alarms
├── background.js          # service worker：offscreen 生命周期 + chrome.downloads
├── offscreen.html/.js     # .NET WASM 渲染宿主（唯一持有运行时）
├── build.mjs              # dotnet publish 产物 + 字体 → 本目录
├── papersharp/
│   ├── runtime.mjs        # 懒加载单例 + installSubsetFont（与 js/papersharp 同源，路径不同）
│   └── wasm/_framework/   # 构建输入（gitignore）
├── fonts/                 # YaheiSub 子集（构建输入，gitignore）
└── content/
    ├── capture.js         # 快照器：DOM 展平 + 图片内联 + 等比缩放
    ├── panel.js           # 右上角按钮与面板（closed shadow DOM）
    └── main.js            # 流水装配：capture → 分片 → render → 下载回执
```
