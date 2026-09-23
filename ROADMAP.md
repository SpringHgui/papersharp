# PaperSharp 路线图

> 对标：**takumi**（<https://github.com/kane50613/takumi>，Rust，JSX/HTML → OG 图 + 分页 PDF，多运行时）。
> 我们的定位：**.NET 库，HTML/CSS → 文档（发票/报表/合同）+ 图卡**，不做多运行时与前端构建期能力。
>
> M1–M4 全部完成、M5 完成前半、M7（复杂文字）与并发安全化已完成；M8（伪元素 `::before/::after` + 计数器、`background-clip: text` 渐变文字、`backdrop-filter` 毛玻璃）与 M9（`@keyframes`/`animation` 确定性取帧）已交付（验收样例 `assets/m1–m5/m7–m9.html`，全清单见 README「能力速览」）。本文只记录**剩余差距与下一步**。

## 差距清单（takumi 有 / 我们没有的，全量）

| 领域 | 缺口 | 成本 | 备注 |
|---|---|---|---|
| 性能/架构 | **AngleSharp 层叠超线性**：2000 元素文档 buildDoc 21–74s | **高（但已量化收益）** | → **M6，待拍板** |
| 分页/CSS | `@media`、`overflow: hidden`、`transform` | 低–中 | 伪元素 `::before/::after` + 计数器、`background-clip: text`、`backdrop-filter` 已于 M8 交付；`transform` 仍未做（依赖它的动画停在基线态） |
| 定位 | `fixed`/`sticky` 视口语义 | 中 | 静态文档场景价值有限 |
| 布局 | flex `align-content`（现简化为 flex-start） | 低 | |
| 文本 | `text-wrap: pretty`、`lang` 汉字分形（zh/ja/ko） | 低–中 | locale 传 SKFont 即可分形 |
| 视觉 | `corner-shape` bevel/scoop/notch、SVG 滤镜图 `filter: url()`、`offset-path` 路径文字 | 中 | Skia 有原语，缺 CSS 侧解析 |
| 图片 | 网络图片 URL | 低 | 当前刻意不抓（离线 + 确定性） |
| PDF | Tagged PDF / PDF/A / PDF/UA、附件（Factur-X XML） | 高 | Skia 后端不出结构树/XMP；`PdfEnhancer` 增量尾部已可挂载 → M5 剩余 |
| 文字系统 | 嵌套双向（RTL 中整句引语）的完整 UAX#9；国旗 emoji（regional indicator 成对合成）；shaped run 的 letter-spacing | 中 | M7 已交付连写/bidi run/字素/字形回退，这些是其已知边界 |
| 字体 | `@font-face` 的 `format()` 校验、`local()` | 低 | |
| 输入 | 网络字体外的 JSX / Tailwind / 动画 GIF·APNG / WASM 多运行时 | — | JSX/Tailwind/WASM 绑定已有；动画 GIF·APNG **明确不做**（与静态文档定位冲突）——`@keyframes`/`animation` 改以「确定性取帧」交付（见 M9） |

## 里程碑

### M6 · 自研级联替换 AngleSharp.Css —— ⏳ 待拍板

**依据（PERF_REPORT §5.1，Release 净跑实锤）**：`buildDoc` 逐元素 `GetComputedStyle` 随元素数 **~O(n^2.5)** 增长——table 2000 行 74.4s、flexlist 2000 条 21.3s（与元素类型无关，是层叠本身）；同规模自研布局仅 76/162ms。**>800 元素的文档即显著受益**；常规小文档不受影响（buildDoc 9–58ms）。

六步计划（每步独立可回退，AngleSharp 核心保留做 DOM+选择器）：

1. CSS tokenizer/parser（我们已有 `CssValueParser`/`AtPageParser` 的解析底子）
2. 特异度 + key-selector 分桶匹配（沿用 `element.Matches`，把每元素全表扫描变按 key 命中）
3. 属性元数据表：继承性 / initial / 简写展开
4. 值解析直接接入现有 `CssValueParser` → `ComputedStyle`
5. 换掉 `StyleResolver` 里 `Get/Spec/Val/Raw` 的内部实现（对外零改动）
6. **双跑对账**：14 样例逐元素声明值 + 全量渲染像素一致，绿了才移除 AngleSharp.Css 引用

附带收益：一次性消掉 README「坑位备忘」里 6 类 AngleSharp bug（NRE、% 折算、calc 预解析、简写序列化、UA 表、text-overflow 兜底）。

### M5 剩余 · PDF 合规 —— 🟡 需求驱动

已完成：书签 / 链接注释 / metadata（自写 `PdfEnhancer` 增量尾部，未换 PDF 后端）。待补：Tagged PDF 结构树 → PDF/UA → PDF/A、附件（Factur-X）。增量尾部机制已就位，加对象即可，但结构树/XMP 工程量大——等真实企业/政务需求触发。

### M7 · 复杂文字系统 —— ✅ 已完成（2026-09-22）

HarfBuzz shaping（阿拉伯连写）+ 轻量 bidi（grapheme 级强方向 run，RTL 基底反转 run 序）+ 字素安全分词 + 字形级字体回退。
验收样例 `assets/m7.html`（阿拉伯段落 / RTL 混排拉丁数字 / LTR 内嵌 RTL / 越南组合符 / 韩中混排 / emoji / RTL inline-block / RTL 表格）。
依赖：`SkiaSharp.HarfBuzz`（2.88 有绑定，此前"无绑定"判断有误）+ `HarfBuzzSharp.NativeAssets.Linux`。
已知边界：嵌套双向（单层 run 反转）、国旗 emoji、shaped run 内 letter-spacing、SVG 输出不做 shaping（由查看器重排）。

### M8 · 伪元素 + 渐变文字 + 毛玻璃 —— ✅ 已完成（2026-09-23）

`::before`/`::after` 生成内容（字符串 / `attr()` / 引号 / `url()` 图 / `counter()`·`counters()` 自动编号，含中文数字），行内与块级各自成盒、多级列表嵌套编号；`background-clip: text` 把背景铺进字形（渐变文字）；`backdrop-filter` 读取盒背后已绘像素做模糊（毛玻璃）。
验收样例 `assets/m8.html`（伪元素 + 计数器全谱）；`background-clip: text` / `backdrop-filter` 走像素级断言（`PseudoElementTests` 等）。
已知边界：`backdrop-filter` 仅位图路径生效（PDF/SVG 矢量路径跳过）；AngleSharp 对伪元素 UA 注入 `display:block`、吃掉 `counters()` 逗号、`counter-*`/`background-clip` computed 返回占位值——均按既有「原始 CSS 兜底」模式绕过（见 README「AngleSharp 坑位备忘」）。

### M9 · 动画确定性取帧 —— ✅ 已完成（2026-09-23）

解析 `@keyframes`（from/to、百分比、多选择器帧合并）+ `animation` 简写（duration/delay/timing 含 cubic-bezier·steps/iteration/direction/fill-mode）；渲染 API 加 `animationTime`（秒）参数，按该时刻求值插值覆盖样式，输出仍是**静态单帧、逐字节确定**。插值覆盖引擎已渲染的属性：opacity / color / background-color / font-size / letter-spacing / width / height / top·right·bottom·left / border-radius。Wasm `Api.Render`、JS `render(opts.animationTime)`、Tailwind `animate-pulse` 均已接线。
验收样例 `assets/m9.html`（淡入 / pulse / 颜色 / 宽度圆角 / transform 边界声明）；`AnimationTests` 用盒中心像素通道做连续量断言（单调插值、fill 末态、alternate 对称、同刻逐字节确定、transform 与无动画逐字节一致）。
已知边界：**`transform` 未实现** → `spin`/`ping`/`bounce` 等依赖变换的动画关键帧被解析但不生效（方块静止，不做假变换）；缓动按全局进度求值（多关键帧 + 非线性的严格 per-interval 语义是简化，两端点与常见单段动画正确）。

### 并发安全化（线程隔离）—— ✅ 已完成（2026-09-22）

`RenderScope`（AsyncLocal）承载每请求私有的 @font-face 注册表与 SKTypeface 缓存；`ImageLoader` 缓存键编入 BaseDir；`StyleResolver` 文档级态（title/author/vars/unsupported）收进 `DocContext` 随根样式下发；`GoogleFonts` 每请求独立 HttpRequestMessage。
回归哨兵：xunit 禁并行已移除，全套测试本身即并发压测；另有 `ConcurrencyTests`（16 线程字节一致 / 交错文档无污染 / 不同 BaseDir 图片不串台 / 并行 PDF 全有效）。

### 明确不做

- 动画 GIF/WebP/APNG、视频帧 —— takumi 主打 OG 动画，我们是文档渲染器；`@keyframes`/`animation` 改以「确定性取帧」交付（M9），输出恒为静态单帧
- Cloudflare Workers/WASM 多运行时 —— 我们本就是 .NET；Native AOT 另议

（JSX 输入与 Tailwind 原子类经 `js/papersharp` 层已支持，构建期预处理成 HTML+CSS 后喂给引擎。）

## 一页速览

| 里程碑 | 主题 | 状态 | 触发架构变更？ |
|---|---|---|---|
| M1 | 表格+分页（跨页续表/rowspan/break/中文页码） | ✅ | 否 |
| M2 | flex-wrap/float/Grid 子集/定位补全 | ✅ | 否 |
| M3 | vertical-align/balance/text-fit/conic/blend/mask/squircle/svg | ✅ | 否 |
| M4 | JPEG/WebP/ICO/SVG、Google Fonts、var/calc | ✅ | 否 |
| M5 | 书签/链接/metadata ✅ · Tagged/PDF-A/附件 ❌ | 🟡 | 否（增量尾部已解） |
| **M6** | **自研级联替换 AngleSharp.Css** | **⏳ 待拍板** | **是（移除一个依赖）** |
| M7 | Arabic/bidi/emoji（HarfBuzz shaping + 字素 + 字形回退） | ✅ | 是（引 SkiaSharp.HarfBuzz） |
| M8 | 伪元素 `::before/::after` + 计数器 / `background-clip: text` / `backdrop-filter` | ✅ | 否 |
| M9 | `@keyframes`/`animation` 确定性取帧（非 transform 属性） | ✅ | 否 |
| — | 并发安全化（RenderScope 每请求隔离） | ✅ | 否 |
