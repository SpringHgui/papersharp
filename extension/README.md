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
- **块容器高度** → 浏览器算出的值当 `min-height`（下限）而不是 `height`（钉死）。钉死后一旦 flex/grid 换行数不同，后面内容会整片重叠（实测卡片行压住下一节）。只有替换元素、绝对定位、需要裁切的盒子才钉死。
- **flex/grid 子项** → 不烘焙宽高，交给引擎按 `flex-basis`/`grow` 自己分。
- **`position:fixed` / `sticky`** → 标成 `static`（引擎本来就把它们当 static），固定顶栏因此在文档流位置出现一次。
- **阴影 DOM** → 开放 shadow root 直接展平其内部元素（浏览器已解析完 `::slotted` 与宿主样式），`<slot>` 用 `assignedNodes()` 取真实内容。
- **中文字体** → wasm 里没有系统字体，offscreen 启动时先把打包的雅黑子集装成引擎的字形级兜底（`InstallSubsetFont`），否则网页里任何未注册 `font-family` 的中文都是豆腐块。

## 已知限制（如实）

引擎侧（见根目录 `README.md` / `ROADMAP.md` 的完整清单）：

- 无 `transform`：靠 `translate()` 做位移/居中的元素会回到未变换位置
- 无 `background-size` / `position` / `repeat`：合成出来的背景图按盒子尺寸拉伸
- 无 `font-style: italic`、无 `::marker`（列表符号丢失，`display:list-item` 退化为 block）
- `@media` 由浏览器在展平时解析掉，因此**不会**随输出宽度重新回流
- 只导出顶层文档：`<iframe>`、跨文件 SVG 雪碧图（`<use href="…#id">`）、视频帧不导出，保留占位盒
- 网页字体（woff2/woff）不加载——Skia 侧读不了，统一落到打包的雅黑子集，拉丁字形因此与网页原样有差异

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
node extension/test/e2e-browser.mjs --page long-article --formats pdf   :: 分页/书签
node extension/test/diag-browser.mjs edge headless                     :: 只查扩展能否注入
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
