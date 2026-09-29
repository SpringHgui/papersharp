# Chrome 对齐 · 待修复清单

> 目标：PaperSharp 的渲染结果与 Chrome 一致。本文件是**唯一**的待修复台账——每条都带 Chrome 真值、引擎现状、取证方式与验收口径；修完直接划掉（`~~~~` + 提交号），不另开分支文档。
>
> 分级口径：**爆炸半径 × 可逆性**。P0 = 真实页面上肉眼可见且会算错外观/几何；P1 = 特定写法下丢内容或丢装饰；P2 = 观测盲区与亚像素残差。

## 本轮基线（2026-09-28，`bash tools/sweep.sh` @ 1284px）

24 个样例可测：**布局几何基本对齐**——合计漂移盒 31 / 2300+，最坏 2.1px，全部集中在 m7/inline/table/challenge。`login-page` **0 漂移**，但页面上有肉眼可见的绘制错误（见 P0-1）——说明现有对拍通道只量盒矩形，绘制层完全无门（见 P1-4）。

**同日三波修复后的账**：P0-1 `cd54e4d`、P1-3 `faa1264`、P1-9 `4c8e7bd` 已落（三处都是 `login-page` 一页引出的独立根因）；三波累计只改动 `login-page` / `pricing-page` / `pricing-top` 三张 2x 光栅，其余 23 张逐字节不变。绘制层的门（P1-4）已补成 `tools/pixelsweep.sh`，此后每样例给「2x 强差 % / 1x 可见重影 % / 重影评分」一行硬指标，不再靠肉眼截图对比。

**2026-09-29 重定基线**：绘制层折整（P0-4）落地后，`bash tools/pixelsweep.sh` 的口径换成了「参照物关 LCD + 引擎绘制原点折整 CSS px」，与 9-28 那份数字**不可直接比**。新均值：2x 强差 **6.51%**、1x 可见重影 **10.95%**（26 张）。`long` / `flex` / `invoice` 三张 2x 强差归零。此后每波都以这份为基线。

---

## P0-1 · 伪元素声明漏到宿主元素（`border-radius` 等 30+ 属性）—— ✅ 已修（波次1）

**症状**：`assets/login-page.html` 顶部横幅（`.hero`，CSS 里**没写** `border-radius`）被画成四角半径 64px 的大圆角胶囊；Chrome 里 `.hero` 是直角矩形，只有贴着卡片的那两个上角被 `.card{border-radius:24px;overflow:hidden}` 裁成 24px。

**实测**（截图与引擎 2x 光栅同一量法，逐行扫蓝区左边界求弧长）：

| | Chrome | PaperSharp |
|---|---|---|
| 左上角半径 | 24 CSS px（≈48 设备 px 弧） | ≈57 CSS px（弧 115 设备 px，被卡片 24px 裁剪切掉角尖后的可见值） |
| 左下/右下角 | 直角 | 同样 64px 圆角 |
| 盒几何 | — | 引擎 0 漂移（`boxdiff` 看不见此错） |

**根因**：`Css/StyleResolver.cs` 的「原始 CSS 兜底表」`ScanUnsupported`（约 231–255 行）按**选择器末段简单 token** 建键，剥键用的正则是 `[:\[].*$`——从第一个冒号起全剥，于是 `.hero::after` 与 `.hero` 归并成同一个键 `.hero`。`ReadStyle` 里 `border-radius` 这条走 `Lookup("border-radius", element, "", ctx)`（约 1612 行，刻意传空 `current` 强制查表），就把伪元素的 `64px` 读到了宿主身上。同一条规则里的 `box-shadow: inset 0 0 0 1px rgba(255,255,255,.45)` 也一并漏了过去（引擎给 `.hero` 画了一圈 Chrome 没有的白色内描边）。

**波及面**：`UnsupportedProps`（约 96–107 行）里全部 30+ 属性都可能漏，已用最小样例逐个证实会漏到宿主的有：`border-radius`、`box-shadow`、`background`/`background-image`（渐变）、`clip-path`、`mix-blend-mode`、`background-clip`。同表还挂着 `overflow`、`flex`、`vertical-align`、`align-*`/`justify-*`/`place-*`、`filter`、`corner-shape`、`counter-*`、`animation`、`font-variant*`、`text-*`——同一机理，未逐一取证但同源。

**触发条件**（决定这不是长尾）：伪元素规则的末复合选择器是**裸** `.class` / `#id` / `tag`。写成 `.a.b::after`、`.a:hover::after` 的不会漏（键变成 `.a.b` 命不中）——即漏不漏取决于作者有没有再叠一层伪类，纯装饰件最常见的 `.hero::after` 恰好必漏。

**修复方向**：兜底表建键时把伪元素标记单独存一列（`::before`/`::after`/`::placeholder`/`::marker` 与宿主互不同名），宿主层叠只查无伪元素的那份；伪元素层叠只查自己那份。顺带把「同一条规则里前一个属性值覆盖后一个」的粗粒度（`Regex.Match` 取首个命中）一并收紧。

**验收**：① `login-page` 的 `.hero` 计算样式 `border-radius=0`、`Shadows` 为空，蓝区左上角弧实测 48±2 设备 px、左下角直角；② 新增受跟踪探针 `assets/_probe-pseudoleak.html`（`.a::after{border-radius:40px;box-shadow:...;background:linear-gradient(...)}` + 裸 `.a` 宿主），配 xunit 断言宿主四属性全为初值；③ `tools/sweep.sh` 与既有金标零回归。

---

## P0-2 · 自定义属性从伪元素漏到**全页其它元素** —— ✅ 已修（波次1）

**症状 / 实测**：`.a::after{--pad:30px}` 与 `.b{padding:var(--pad,0px)}` 同页时，引擎里 `.b` 的宽度从 400 变成 340（吃到 30px 内边距）；Chrome 里 `--pad` 只存在于 `.a` 的伪元素上，`.b` 取不到 → 回退 `0px`，宽度 400。

**根因**：`ScanVars`（约 300–321 行）用的 `SelectorScopeKey`（约 324–333 行）与 P0-1 是**同一条 `[:\[]` 剥离正则**，键退化成 `.a`；而 `ExpandVars` 的查找是「沿祖先链按 class/id/tag 命中」，于是伪元素上声明的变量被当成宿主乃至后代可见。

**为什么单独一条**：P0-1 只污染宿主自身的外观；这条会污染**任意引用该变量的兄弟/后代**的盒几何——是布局级错误，且设计系统里 `::after` 上挂 `--size`/--`color` 的写法不少见。

**修复方向**：与 P0-1 同一个键方案一起改（两处共用 `SelectorScopeKey`，一次改对）。注意 CSS 真实语义是**伪元素继承宿主的变量**（宿主 `.a{--pad:5px}` 时 `::after` 可见），反向不可见——改完要按这条立断言。

**验收**：上述最小样例 `.b` 宽 400；`.a{--pad:5px}` + `.a::after{margin-left:var(--pad)}` 的伪元素仍拿到 5px。

---

## P1-3 · 绝对定位的装饰伪元素整块不出现 —— ✅ 已修（波次2，根因与初判不同）

**症状**：`login-page` 的 `.hero::before`（260×260、`rgba(255,255,255,.35)`、`rotate(24deg)`）与 `.hero::after`（320×320 白色渐变 + `rotate(-18deg)`）在 Chrome 里是横幅右侧两块可见的浅色装饰；引擎整页光栅里**一块都没有**（盒树里 `header` 只有 `h1`、`p` 两个孩子）。

**根因（实测修正，与初判不同）**：不是「`content:""` 被当成无内容」。AngleSharp 的 computed 分得开：未写 `content` → 空串，写了 `content:""` → 两字符的 `""""`，`PseudoContent.Parse` 据此已建出 `runs=[Literal:""]`。真凶是 **伪元素的 display 归一发生在块级化之后**：`ReadStyle` 末尾按规范把 `position:absolute/fixed` 的 display 块级化（真元素靠它才成盒，见 fluent-docs 滑杆拇指），可 `BuildPseudoGen` 在那之后又按「作者有没有显式写 display」把伪元素打回 `inline`（AngleSharp 对伪元素恒注入 `display:block`，不可信，故必须问作者）。空内容的行内伪元素在 `Materialize` 里 `InlinePieces.Count == 0 → return`，整块装饰就这么没了。修法：抽出 `Blockified(display)` 单一口径，伪元素归一之后再走一次。

**修后实测**：`login-page` 的 `header` 盒树多出 `<> 260×260 R=44` 与 `<> 320×320 R=64` 两个块盒（半径各归各的，正是 P0-1 修完后的样子），2x 光栅上横幅右侧两块装饰出现，差异包围盒 x1992–2449 / y120–515、15742 像素；对照组 `pricing-mid`/`m8`/`table`/`report` 逐字节不变。

**依赖项**：`transform: rotate()` 引擎未实现（ROADMAP 已记），故本条补齐后这两块装饰的**角度**仍与 Chrome 有差；本条只验收「装饰件出现且尺寸/落位正确」，旋转跟随 `transform`。

**验收**：`content:"";position:absolute;width:50px;height:50px;background:#f00` 的 `.a::after` 在引擎盒树里成盒并画出 50×50 红块（`PseudoScopeTests.Absolute_empty_content_pseudo_paints_its_box` 逐像素钉住），且非绝对定位的空内容行内伪元素仍不成盒（`Inline_empty_content_pseudo_still_does_not_box` 反向钉住，防扩面）。

---

## P1-9 · 多类名 / 带 transform 的伪元素规则整条消失 —— ✅ 已修（波次3，实为两个独立根因）

**症状**：`login-page` 选中页签那条蓝条 `.tab.active::after{content:"";position:absolute;left:50%;bottom:0;transform:translateX(-50%);width:104px;height:4px;border-radius:2px;background:#1a5cf0}` 在 Chrome 里画得出来，引擎里整条规则当它不存在。

**根因 A（闸门 token 不同名）**：`ScanPseudoDisplay` 建 `ctx.PseudoScope` 存的是末段整串 **`.tab.active`**，而 `PseudoMayMatch` 的探测式是按元素 class 列表逐个拼的 **`.tab`** / **`.active`** → 永不同名 → 闸门按「这个元素不可能有伪元素」直接跳过（闸门确实生效：`StyleResolver.cs:922` `if (ctx.HasPseudoRules && PseudoMayMatch(...))`）。同一个 token 口径也用于 `LookupPseudoMap`（伪元素显式 display、`::placeholder` 颜色、`::marker`），所以带叠加类名的伪元素规则整体处于「看不见」状态。
修法：闸门候选摊成末段复合里的**每个**简单 token（tag / 每个 `.class` / `#id`）——闸门只做「宁可多放行」的超集筛选；**取值键仍按整串存**，不会因此把 A 规则的 display 发给 B 元素（那正是 P0-1 那类串味）。

**根因 B（AngleSharp computed 抛异常把伪元素判成「没写 content」）**：`BuildPseudoGen.Content()` 用 `ComputeDeclarations(el, "::after")` 取 content，而 AngleSharp 在解析该伪元素规则的 computed 时，值里带 `transform: translateX(-50%)` **必抛 NullReferenceException**（`transform:none`、未知属性、只写 content 都不抛，declared 那条路也不抛——实测钉住）。旧写法 `catch { return ""; }` 把异常咽成空串，于是「一条无关声明」判掉「整个伪元素」。
修法：computed 抛了退到 declared（同样做真选择器匹配，只是取作者原样值，而 content 本来就由 `PseudoContent.Parse` 自己解析）。

**修后实测**：蓝条画出来了（2x 光栅上 207×7 设备 px = 104×4 CSS px，落在页签下方）。

**残留（另立一条，见 P1-10）**：蓝条的**水平落位**仍与 Chrome 差半个条宽——`left:50%` 生效而 `transform: translateX(-50%)` 未实现，条子右偏 52px。

---

## P1-10 · `transform` 未实现 —— 🟡 2D 已落（本波），3D 与两条旁路待做

**原来**：引擎完全不读 `transform`，凡靠它定位/旋转的装饰一律停在基线态——`login-page` 页签蓝条
（`left:50%` + `translateX(-50%)`）右偏半条宽、横幅两块圆角装饰件不转、`pricing-*` 的 `.qa.open .plus` 不转。

**本波落地下来什么**（`Css/TransformParser.cs` + `Paint/TransformSpace.cs`）：

- 函数集：`translate(X|Y|3d)` / `scale(X|Y)` / `rotate` / `rotateZ` / `skew(X|Y)` / `matrix`，
  按书写顺序复合；`transform-origin` 支持 px/%/关键字（`left/center/right/top/bottom`），默认 50% 50%。
- 三条不变量：① transform **不参与布局**（盒矩形与兄弟排布一律按未变换几何）；② 祖先链逐层后乘，
  子盒各自算累计矩阵，绘制时不把父变换留在画布上下传（否则乘两次）；③ 溢出裁剪矩形同样被矩阵带走，
  「圆角卡片旋转后裁剪跟着转」天然成立。
- 3D 分两类：`translateZ`/`scaleZ`/`translate3d` 的 z 在**无透视时不改变外观**，按 2D 等价收下
  （前端拿它们做图层提升是常态，判废反而离 Chrome 更远）；`rotateX/rotateY/rotate3d/scale3d/matrix3d/perspective`
  会真改变形状 → **整条 transform 作废**（CSS 口径：列表里一个函数解析失败整条失效）。
- 矩阵乘法自己实现（行向量约定）。踩过的坑：借 Skia 的 `PostConcat` 时 e/f 恰好是反的，
  旋转后的方块整个飞出画布——**症状是「元素凭空消失」而不是「位置不对」**，最难查的一类。
  另一处：`(p−o)·F+o` 展开时漏掉 F 自带平移 `t_F`，于是 rotate/scale/skew 全对而 `translate` 被吞成 0。

**与 Chrome 的对账**（探针 `assets/_probe-tf.html`，7 个用例，逐色量外接矩形与面积）：
`rotate(45deg)`、`transform-origin:0 0` + `rotate(90deg)`、`translateX(-50%)`、`scale(2)`、
`skewX(45deg)`、**父旋转带动子条** —— 6 条与无头浏览器 2x 截图逐像素一致（面积差 ≤7 px 属 AA）；
第 7 条 `rotateX(45deg) rotate(45deg)` 是有意不等（引擎整条不画，Chrome 做真 3D 投影）。
回归钉在 `tests/PaperSharp.Tests/TransformTests.cs`（9 条，含「transform 不改布局」与两类 3D 判据）。

**全样例影响**：26 张里只有 `login-page` 用到 transform（3 条规则），逐张对比 22 张逐字节不变、
金标失败集合与改动前**逐条同名**（零新增红）。`login-page` 聚合指标只从 16.2% 降到 16.1%——
这页的大头是字形栅格化与那块不执行 JS 的 canvas 二维码，装饰件旋转在面积上占比很小。

**还没做的三件**：
1. **SVG 导出旁路**（`Paint/SvgWriter.cs`）没接 transform → 同一段 HTML 走光栅/PDF 会转、走 SVG 不转。
   接法是给每盒的 `<g>` 加 `transform="matrix(a b c d e f)"`（CSS matrix 与 SVG 的字段一一对应）。
2. **`@keyframes` 里的 transform 仍不插值**（`Css/Animation.cs` 显式忽略）——m9 的 `spin` 关键帧现在
   能取到静态 transform 了，但按 `animationTime` 逐帧插值还没接，动画样例仍停在基线态。
3. **真 3D**（rotateX/Y/3d、perspective、matrix3d）不支持，且 `backdrop-filter` 与 transform 同盒时
   毛玻璃取的是设备坐标的身后像素，变换后源矩形对不上。

## P1-4 · 对拍通道只量盒矩形，绘制层没有任何门 —— ✅ 已补门（波次4：`tools/pixelsweep.sh`）

**症状**：`tools/sweep.sh` 上 `login-page 引擎 33 盒 / 浏览器 33 矩形 · 漂移 0`——几何全绿、页面明显画错。圆角、阴影、渐变、blend、伪元素装饰、边框描边这些「盒矩形不变但像素变了」的类别，此前**全靠肉眼**。

**现状盘点（修前）**：`tools/boxdiff.mjs`（几何对拍）+ `dotnet run -- pixdiff <name> <browser2x.png>`（像素重影，但要人工先出浏览器截图）+ `tests/PaperSharp.Tests/goldens`（自比金标，不是 Chrome 真值）。缺的是「一条命令跑全样例的 Chrome 像素基线」。

**已交付**：`bash tools/pixelsweep.sh [样例…]` 把 boxes → 无头浏览器 2x 截图 → pixdiff 串成一行硬指标（2x 强差 % / 1x 可见重影 % / 真位移带条数 / 判定），逐样例原始输出与差异三联图落在 `.scratch/pixelsweep/`。

**2026-09-28 首份全样例基线（1284px，三波修复之后）**：26 样例，2x 强差均值 **10.5%**。最差五张：`m7 24.4% · m9 23.7% · long 21.2% · login-page 16.2% · pricing-mid 15.4%`；最干净 `fontvs 0.3% · flex 1.5% · m2 1.4% · invoice 1.8%`。

**这份数字怎么读（关键）**：`位移带 X/Y` 是**对齐后仍可修的整段位移**，绝大多数样例是 `0/N`——也就是说全局对齐之后，残余差异的主要成分不是几何错位，而是**字形光栅化口径**（hinting / 抗锯齿 / 次像素），pixdiff 自己把它单列成「光栅差 / 半像素噪声」。这直接给「重影」类问题定了性：**继续调盒几何不会再降这个数**，要降得去对字形栅格化策略。例外是 `challenge 6/122`、`m2 6/34`、`pricing-page 4/130`、`m9 2/13`、`pct 2/22`——这几张有真位移带，属可修目标。

**最差三张的定性（2026-09-28 逐张看过差异图并量过）**：

- `long 21.2%`：把引擎与浏览器两层按 90px 高的纵向窗口逐窗搜最佳整数平移，**y=60→960 全程偏移 +0** —— 垂直方向没有累积漂移，26 行列表的行高/行距是对的。差异全在**行内水平方向**：越往行尾越明显，是逐字符推进宽度（advance/kerning）与栅格化的差，不是布局。
- `m7 24.4%`：红色强差**集中在 RTL 段**（阿拉伯语/希伯来语整行、含 bidi 混排的表格右列、emoji/ZWJ 那一行），同页的拉丁与 CJK 行只剩轮廓级弱差。这张的大头是 **bidi 落位**，属 M7 已知边界（嵌套双向、shaped run 的 letter-spacing），不是栅格化。
- `m9 23.7%`：`位移带 2/13` 是全场真位移带占比最高的一张，先看那两条带再谈栅格化。

**已知把数字顶高的非缺陷项**：`login-page` 的二维码是 `<canvas>` + 页内 `<script>` 画的，引擎不执行 JS → 那块区域必然 100% 差（约占画面 14%）。要么给样例换静态 `<img>`，要么在指标里把 `canvas` 区域剔除，否则这张的百分比不可比。

**验收**：新门能在不改代码的前提下把已知绘制错报成红（首跑即把 login-page 报 `16.2% / 严重`，与三波修复前的肉眼结论一致），修完转绿。


---

## P0-3 · 宿主的盒尺寸声明漏给伪元素（AngleSharp 第二条漏值路）—— ✅ 已修（波次6）

**症状**（Playground 现场，`pricing-page`/`pricing-top`）：四张定价卡里各有一颗撑满整卡的「蛋形」轮廓，Chrome 里只是卡片右上角一小段圆环弧线。

**根因**：波次2 让绝对定位的装饰伪元素开始成盒之后才暴露出来的下一层——**AngleSharp 的伪元素视图会把宿主的盒尺寸声明原样漏进 computed 与 declared 两条路**。实测（`.plan{min-height:574px}` + `.plan::before{width:200px;height:200px;border:26px}`）：伪元素的 `min-height` 在两条读取路上都是 `574px`，而作者从没为 `::before` 写过它；`min-width`/`max-*` 不漏，`width`/`height`/`border-*` 取的是伪元素自己的值。`min-height`/`max-height`/`width`… 按 CSS 永不继承 → 圆环被撑成 148×522 的蛋。

**改法**：`ScanPseudoDisplay` 顺带登记「伪元素规则里作者亲自写过哪条尺寸属性」（`ctx.PseudoSizing`），`ReadStyle` 里六处尺寸取值改走 `OwnSize`。判漏用**两条同时成立**：作者没为该伪元素写过 **且** 取值与宿主逐字相同（漏值的签名）。只写「索引里没有」会把 token 索引没覆盖到的真声明（如 `ul.tags li::after`）误杀——这条边界由 `Pseudo_own_min_height_is_kept` 反向钉住。

**实测**：`pricing-top` 2x 强差 **12.3% → 7.4%**、`pricing-page` **14.4% → 12.0%**，其余 24 张样例逐字节不变；卡片自己的 `min-height:574px` 未被误伤（`Host_box_sizing_does_not_leak_into_pseudo` 双向断言）。

**同类风险（未修，待挖）**：这条「宿主→伪元素」漏值路既然在 `min-height` 上成立，其它非继承属性也可能有各自的漏法（margin/padding 早先已单独用作者源桶裁决过，见 `StyleResolver.cs` 该处注释）。`PseudoScopeTests` 目前只钉住尺寸与 margin/padding 两族，其余属性按样例撞见再补。

---

## P0-4 · 满屏重影的两个根因：参照物带 LCD 彩边 + 绘制原点没折整 CSS px —— ✅ 已修（2026-09-29）

**症状**：26 张样例 19 张判「严重」，1x 可见重影均值 **14.1%**，而盒级对拍全绿（`long` 35 盒 0 漂移）。用户口径就是「大面积重影」。

### 根因 A · 参照物本身带次像素彩边（测量口径，不是引擎画错）

Windows 上 Chrome/Edge 默认按 LCD 子像素排布渲染文字，字干两侧带 `R≠B` 的彩边；引擎（以及任何 PDF/打印输出）是灰度 AA。`pixdiff` 的差取三通道最大值，于是**每一根字干边缘都被记成强差**。实测 `long` 的墨像素里 `|R−B|>24` 占比：**浏览器 51.5% · 引擎 16.0%**。

`tools/boxdiff.mjs` 加 `NO_LCD=1`（`--disable-lcd-text`），`tools/pixelsweep.sh` 默认开。关掉后 `long` 的 1x 可见重影 **44.8% → 24.4%**，两侧彩边率 16.26% / 16.33% 对齐——剩下的才是可比的。本引擎的目标介质是 PDF/PNG，那里根本没有次像素彩边。

### 根因 B · Blink 把绘制原点折到整 CSS px，引擎按分数落笔（本条主体）

**浏览器真值**（探针 `assets/_probe-snap.html`：20 行 `top=40.1k+0.1k`，左列文字右列实心条、**每行文本完全相同**以免字面内容差污染质心；`dotnet run -- snap` 逐行量墨迹纵向质心）：

| top (css px) | 0.0 | 40.1 | 80.2 | 120.3 | 160.4 | 200.5 | 240.6 | 320.8 |
|---|---|---|---|---|---|---|---|---|
| 引擎落位（设备 px） | 20 | 100 | 180 | **261** | **341** | **421** | **501** | 662 |
| 浏览器落位（设备 px） | 20 | 100 | 180 | **260** | **340** | **422** | **502** | 662 |

浏览器侧**只出现在偶数设备行**（= 整 CSS px × 2），且 200.5→201、120.3→120 → 四舍五入、`.5` 远离零；引擎侧按分数设备行连续走（文字另外被 Skia 折到整数**设备**行，与浏览器的整数**CSS** px 是两个网格）。实心条列与文字列同一口径 → 折整是**绘制层的通用规则**，不是文字专属。

**取整点**（探针 `assets/_probe-snap2.html`：0~9 层 `padding-top:.3px` 的嵌套链，绝对位置 = 40k+0.3k）：k=2..9 浏览器全部落在 `round(绝对位置)` 那一格（k=4 的 120+0.9 → 设备行 242；「逐层各自折」会给 240）→ **绝对位置折一次**，不是逐层折。唯一分歧在 k=5：Blink 的绝对位置是 201.484375（0.3px 进 `LayoutUnit` = 19/64），引擎是 201.5 → 折整翻面。那是长度单位没折到 1/64 的账，另立 **P2-10**。

**像素侧的签名**（修前，`PGHOST=1` 分数平移取证）：绝大多数文本带 `dx≈0` 而 `dy=±1.0` 设备像素，且挪这一格之后 SAD 从 100% 掉到 **13~21%**——即「整页文字上下差半格」，而水平方向挪不动。旧的位移带判据只搜**整数**设备像素且要求 ≥2 才算数，这一整类落在判据之外，却被 `vis%` 计入，于是看起来像「固有的光栅差」。

**修**：`Paint/PaintSnap.cs` 单一口径，两条绘制路径各接一处——光栅 `Painter.PaintSelf` 折文档绝对原点、分页 `PagePainter.PaintItem` 折页内原点（碎片化后同一盒在不同页各折一次）。**只折绘制、不动布局**：盒矩形、换行点、分页一律按分数算，`tools/sweep.sh` 与 `boxdiff` 不受影响（`PaintSnapTests.Snapping_does_not_move_the_layout` 钉住）。`PS_SNAP=0` 整机关掉做 A/B。

**排除的假设**（都做了反证，别再走一遍）：① **字形提示档位**——临时给 `NewTextPaint` 挂上 `HintingLevel`（`none|slight|normal|full` 四档 A/B），对 `long` 的指标分毫不动（只有 `none` 那档让 2x 强差从 31,146 变 31,148，即 7 个像素）——打包的雅黑/思源子集没有 TrueType 字节码，FreeType 无从提示，故**没留开关**；② **逐字符推进宽度漂移**——分数平移搜索里 dx 恒 ≈0；③ **双重落笔**——逐设备行墨量引擎 vs 浏览器逐行相等（±1%）。

**全样例净影响**（同一批浏览器真值图，只切 `PS_SNAP`；口径 = NO_LCD；1x 可见重影 %）：

| 修好 | long 24.39→**0.24** · m8 24.35→4.36 · inline 18.54→4.60 · fluent-docs 17.04→8.39 · pricing-mid 24.57→15.38 · pricing-page 17.12→12.67 · m7 41.22→34.13 · m5 9.19→1.45 · img 6.78→1.38 · flex 2.35→**0.09** · m4 10.04→7.76 · pricing-top 10.66→8.57 · m3 9.69→8.18 · m2 2.37→1.00 · m9 25.11→22.40 · pct 15.21→14.84 |
|---|---|
| 回退 | pricing-foot 18.35→28.37 · report 12.33→19.09 · m1 17.39→23.52 · table 12.10→16.55 · framer-motion 7.77→10.08 · polish 4.71→6.33 · fontvs 0.29→0.74 |
| 均值 | 2x 强差 **8.06% → 6.51%** · 1x 可见 **14.09% → 10.95%** |

`long` 的 2x 强差是 **0/202,344 = 0.0%**（整页一个强差像素都没有）——规则本身对到位的最硬证据。

**7 张回退的定性（下一批的靶子，不是本条的遗留）**：折整会把「布局本来就差 0.1~0.4 px」的站点从小半格（看不出来）放大成整整一格（2 设备像素）。

- `report` / `table` / `m1` / `polish`：逐带 `dy=±2.0` 设备像素、挪一格 SAD 掉到 9~16% → 纯落位翻转，根子在亚像素布局残差（P2-6 的 0.5~0.6px 贴边项 + P2-7 只折了 margin 一处）。这几张同时都挂着「高度不可比 / 对齐未收敛」两面旗，本来就不是可比页。
- `pricing-foot` 的 +10.02 **不是落位问题**：社交图标那一带 `dx0 dy0 SAD 100%` 而**墨量比 1.82**，引擎画纯黑、Chrome 是中灰 → 独立的着色 bug，另立 **P1-14**。
- `fontvs` / `framer-motion`：+0.45 / +2.31，未查。

**验收**：`tests/PaperSharp.Tests/PaintSnapTests.cs` 4 条（`PS_SNAP=0` 下 3 条立刻变红、第 4 条「布局不动」两边都绿——已实测）；`tools/pixelsweep.sh` 默认 NO_LCD；`PGHOST=1` 的分数平移与彩边率两个新指标进同一命令输出。

---

## P2-10 · 作者长度没折到 Blink 的 1/64 px 网格（P2-7 的未做完部分）

`_probe-snap2` 的 k=5 行（每层 `padding-top:.3px` × 5）：Blink 的绝对位置 = 200 + 5×19/64 = **201.484375**，引擎 = 200 + 1.5 = **201.5** → 折整后差 1 CSS px。P2-7 只在 **margin 生成处**折了 1/64，`padding`/`top`/`left`/`width` 等一律还是 float 全精度。这条从「看不出来」升级成「看得出来」以后才有价值：绘制层一折整，0.0156px 的单位差在 `.5` 边界上就是整整一格。修法与 P2-7 同法（`CssValue.ToPixels` 出口按属性逐个对换算点），但要按 P2-7 的教训**逐属性取证**，不再一把 `SnapM` 包打天下。

---

## P1-14 · `pricing-foot` 的社交图标画成纯黑（Chrome 是中灰）

`pixdiff pricing-foot` 修后最差的一条带（CSS 行 263..290）：`dx0 dy0 SAD 100%`（挪不动）而**墨量比 1.82**——引擎那半边墨量近两倍。裁图肉眼对照（`.scratch/pf-e2.png` / `pf-b2.png`）：X / Discord / YouTube / LinkedIn / Reddit 五个图标引擎画成**纯黑**，Chrome 是**中灰**。同页这一条把 pricing-foot 的 1x 可见重影从 18.35% 顶到 28.37%（P0-4 折整之后才暴露成独立信号）。待查方向：图标的灰来自 `color` 继承 / `opacity` / SVG `fill`，还是 P1-12 那张「多键命中按 class 书写序取值」的兜底表把某条规则判给了错的声明。

---

## P1-7 · 逗号选择器组只索引末段，整组一起丢 —— ✅ 已修（`813c3cb`）

原始 CSS 兜底表（`ScanUnsupported`）对 `.a,.b { overflow:clip }` 这种分组选择器**只取整串末段**建键；逗号组里没有空白可切，末段就是带逗号的整串 `.a,.b` —— 这把键谁都命不上，于是**两段一起丢**（比初判的「只丢 .a」更糟）。这张表是 30+ 条 AngleSharp 读不稳的属性唯一的值来源，键丢了声明就静默失效。

改法：按逗号拆开逐段建键，伪元素分键口径不变（`.a::after,.b{…}` 依然只有 `.b` 命中宿主，由 `Grouped_parts_keep_their_own_pseudo_scopes` 钉住）。

**顺带纠正本台账的一条假账**：波次1 当时记「逐段建键会掀 26 个样例里 9 张像素」，本轮实测**全 26 张逐字节不变、金标失败集合 39→39 逐条同名**——当前样例集里没有任何一条分组选择器带这些兜底属性，根本没有可观测影响。那个「9 张」是并发会话互踩量测出来的（同一批假账还有 framer-motion 的 5 像素差、以及 P2-5「页面打挂浏览器」，根因都是 `boxdiff` 调试端口写死 9411，见 P2-5）。**教训**：任何「改动会掀 N 张金标」的判断，都要在独占端口 + 独占构建下复算一遍再据此排期，否则会把零成本的事排成高成本。

判别式测试用 `overflow:clip` / `mix-blend-mode` 而不是 `border-radius`：长写圆角 AngleSharp 还读得回来，键丢了断言看不出来——第一版测试就是这么写结果两边都绿、白测一场。

## P1-8 · 宿主的自定义属性喂不到伪元素的 margin/padding 上 —— ✅ 已修（波次5）

**症状**：`.a{--pad:5px} .a::after{margin-left:var(--pad,0px)}`：Chrome 里伪元素继承宿主的变量 → 5px；引擎里那条边吃初值 0。

**根因（实测修正）**：不是「作者源索引不认伪元素作用域键」。`AuthorBoxSpacing.Parse` 在**建索引时就把值里含 `var()` 的声明整条跳过**（`if (val.Length == 0 || val.Contains("var(")) continue;`），于是这条边在作者源桶里根本不存在；而伪元素的八条边只能由这张桶裁决（AngleSharp 的 computed/declared 两条路都会把宿主声明漏进伪元素结果，见该处注释）→ 结果恒为初值 0。

**改法**：桶里照收带 `var()` 的值，展开交给消费侧——宿主走 `AuthorEdge` → `ExpandVars(mv, element, ctx)`，伪元素走 `PseudoEdge` → `ExpandVars(pv, element, ctx, pseudo)`（波次1 已把 pseudo 上下文接进 FindVar：先查伪元素自己的作用域键，再按继承回落宿主链）。变量查不到又没写回退值时留下 `var(--x)` 原串，`ParseSize` 判非法 → 0，与 Chrome 的 invalid-at-computed-value-time 一致。

**影响面**：`InlineUniqueVars` 那条预内联已把「全局唯一定值」的变量折成字面值，所以只有**同名变量多作用域取值**的 margin/padding 才会经过这条路——首版全样例像素对拍见下。

## P2-7 · `challenge` 整页逐块下坠：Chrome 把长度折到 1/64 px（Blink LayoutUnit）—— ✅ 已修（`e18e79a`）

**症状**（`boxdiff challenge --tol 0.05`）：229 个盒的 `dy` 沿文档序**单调累积**（`div[2]` −0.053 → 页尾 −0.226），而 `dh` 几乎全为 0 —— 每个块自己的尺寸对，**块间距**每跳一个兄弟多约 0.011–0.014px。

**根因**：该样例间距全是物理单位（`margin:3.5mm 0 2mm`）。逐条对账 Chrome：

| 作者值 | 引擎（float） | Chrome | `floor(px×64)/64` |
|---|---|---|---|
| 2.5mm | 9.4488 | 9.4375 | 9.4375 ✓ |
| 3.5mm | 13.2284 | 13.2188 | 13.2188 ✓ |
| 2mm | 7.5591 | 7.5469 | 7.5469 ✓ |
| 1mm | 3.7795 | 3.7656 | 3.7656 ✓ |

四条全中——Chrome 把长度折到 Blink 的 `LayoutUnit`（1 CSS px = 1/64），引擎走 float 全精度。

**三种落点全样例实测（2x 强差 %）**：

| 落点 | challenge | m7 | m1 | framer-motion | fluent-docs | 结论 |
|---|---|---|---|---|---|---|
| 基线（不折） | 12.5 | 24.4 | 12.4 | 6.8 | 14.5 | — |
| 全局 floor（`ToPixels` 五出口） | 12.2（位移带 6→1） | 24.1 | 15.7 ✗ | 10.9 ✗ | 22.7 ✗ | 否决 |
| 全局 round | 12.8（位移带反升到 14） | 24.4 | 12.6 | 10.3 ✗ | 22.6 ✗ | 否决 |
| **只在 margin 生成处 floor** | **12.1**（位移带 6→3） | **24.1** | 12.4 | 6.8 | 14.5 | ✅ 已采用 |

**金标怎么刷的**：只刷 snap 真正影响的 14 个文件（m1/m4/m5/m7/m8 的 boxes+png+raster）。做法是在 HEAD 干净工作树里分别以「无 snap / 有 snap」各重刷一遍全金标，取两者 md5 之差当本改动的净足迹——这样刷出来的基线只编码「HEAD+本改动」，不夹带并行会话那 20 个未提交引擎文件的在途像素。自洽验证：该工作树 HEAD+snap+新金标 = 74 绿 1 红，而那条（`PdfStructure/polish`）在纯 HEAD 下本来就红。

**两个已知不完美（后续再收）**：

1. 像素指标改善，但 `boxdiff` 的**最坏单盒 dy 从 0.226 涨到 0.4**（漂移盒数 229→115）。Chrome 并非处处 floor——有的站点四舍五入、有的站点不折。要贴死得按属性逐个对换算点，不是一个 `SnapM` 包打天下。
2. 取整方向只在正数上验过；负 margin 走同一条 floor 未取证。

## P2-8 · `m9` / `m2` 的「位移带」是测量口径问题，不是布局 bug（已给门加两面旗）

- `m9 2/13`：浏览器截图含视口留白（`body{min-height:100vh}` 一类），文档高 972 CSS px vs 引擎 645.5 —— 比的是两份不同高度的文档；灰带上下边在 2x 下只差 1 设备 px，盒几何 `boxdiff` 全绿。
- `m2 6/34`：全局整数平移没收敛（`SAD 72%`，引擎 body 原点 (0,0) vs 浏览器 (84,1)），残余的整体 1px 被逐条边重复计成 6 条「位移带」——六条全是 `dy-1`，正是那个残差。

`tools/pixelsweep.sh` 现在对每张额外打两面旗：`高度不可比(引擎vs浏览器)` 与 `对齐未收敛(SADx%)`；带旗的位移带数**不当 bug 追**。全样例重跑后新暴露 6 张带旗的（table / invoice / report / pct / m7 / m1 等），此前它们的百分比都是不可比的。

---


## P2-5 · 两个样例在浏览器侧压根量不到（观测盲区）—— ✅ 已定位并绕开（波次9）

`fluent-docs`、`framer-motion` 跑 `boxdiff` 报 `Error: {"code":-32000,"message":"Execution context was destroyed."}`，此前判成「页面里有脚本/重定向把 CDP 打挂」。

**复核结论是否**：`fluent-docs.html` 里 `<script>` / `location` / `reload` 命中数为 **0**；换私有端口单跑立刻正常（`153 盒 / 浏览器 153 矩形 · 漂移 0 · 最坏 0.0px`，截图 341 KB 正常落盘）。真因是 **`boxdiff.mjs` 的调试端口写死 9411**，同机多个会话并发量测时互踩 CDP 会话。`tools/pixelsweep.sh` 已默认按进程号取端口（`CDPPORT` 可覆盖），手跑 `boxdiff` 带 `--port` 即可。

**连带影响**：任何在并发环境下跑出的对拍数字都要先确认端口独占——本台账里 framer-motion / fluent-docs 的历史数字都按私有端口复测过两遍，两遍逐张一致。

## P2-6 · 亚像素盒漂移残差（几何侧，非本轮新发现但仍未清）

| 样例 | 漂移盒 | 最坏 | 备注 |
|---|---|---|---|
| m7 | 14 / 41 | 2.1px | 全样例里最大，均值=最坏说明是同一类系统性偏（疑似某类行盒/字素度量），优先挖 |
| inline | 3 / 39 | 0.8px | 行内盒 |
| challenge | 10 / 257 | 0.6px | 亚像素 |
| table | 4 / 66 | 0.5px | 表格边框合并处 |

阈值 `--tol 0.5` 之下仍有 0.5–0.6px 的贴边项，别把它们当噪声划掉——m7 那 2.1px 是同一根因的放大版。

---

## P1-11 · `white-space:nowrap` 在含元素子的行内通路里失效，且 ellipsis 只接纯文本块

**症状**：`assets/_ell.html` L 行：`width:150px;white-space:nowrap;text-overflow:ellipsis;overflow:hidden` 的 div 里写 `前面 <b>粗体片段</b> 后面还有很长一段文字要截断看看`。Chrome 一行、尾部省略号；引擎折成 3 行（盒高 66.5 vs 27.5），省略号没有。

**实测**（2026-09-28）：`dotnet run --project src/PaperSharpSamples -c Release --no-build -- boxes _ell 1284` 出引擎 2x 光栅 + 盒表；`node tools/boxdiff.mjs _ell --port <每次换号> --shot .scratch/ell_browser.png --dsf 2` 出浏览器侧；`.scratch/ell_crop.py` 按引擎盒表裁「上=浏览器 下=引擎」成对图（`.scratch/ell_pairs.png`）。判据用盒高（27.5 vs 66.5）即可，无需像素。

**根因**：两处，都在行内通路。① `Layout/InlineLayout.cs` 约 330 行 `if (curW + pl + tw + pr0 > avail && cur.Frags.Count > 0 …) Roll(...)`——token 放不下就换行，**不看有没有断点**；`Tokenize` 收了 `WhiteSpace` 但「nowrap/pre 不得产生断点」没人执行，每个元素子边界都成了潜在断点。② `text-overflow` 的生效闸门只接在 `LayoutBuilder.MeasureBlock` 的纯文本通路上（约 928 行，2026-09-28 已改成本盒 `s.ClipsOverflow` 才截，见 `TextOverflowTests`）；`MeasureInline → BreakIntoLines` 的 IFC 通路没有这一步——Chrome 按**行**截，行内混排（b/i/a…）不影响。

**波及面**：任何「元素子 + nowrap/pre」的组合（单行省略的徽标行、面包屑、跑马灯文本），以及 IFC 里声明 ellipsis 的盒全部静默失效。已入库样例无一命中（唯一用 nowrap 的 polish `.trunc` 是纯文本块），金标零回归——也意味着这条目前没有任何门守着。

**修复方向**：① 断点模型补齐：Roll 前先判「自上一断点积累的不可断段」，nowrap/pre 下整行不可断，放不下就溢出、交给裁剪；② ellipsis 按 Chrome 语义落到 IFC 行尾：对超宽行做「按字素收缩 + …」时把行内各 frag 一并计宽，不挑纯文本串。

**验收**：L 行引擎盒高 27.5、行尾带 `…`；`TextOverflowTests` 增补混排用例（IFC 通路断言走盒树 `Lines`/行高而非 `WrappedLines`）。

---

## P1-12 · 原始 CSS 兜底表按 class 书写序取胜：源码序与特异性都不看

**症状**：`assets/_ell.html` E/N 两行：`.v{text-overflow:ellipsis}` 写在前、`.clip{text-overflow:clip}` 写在后（同特异性，源码序后者胜），元素 `class="v ovh clip"`。Chrome computed 是 `clip`（硬裁、无省略号）；引擎算出 `ellipsis`。**CSS 一字不动，只把 class 属性改写成 `class="clip ovh v"`，引擎立刻翻成 `clip`**——胜负由 class 的书写序决定，与层叠无关。

**实测**（2026-09-28）：E 行引擎加省略号、N 行引擎硬裁（`.scratch/ell_N_engine.png`，同一份 CSS 只换 class 序）；Chrome 两侧 computed 均为 `clip`（`node tools/boxdiff.mjs _ell --probe` 读 computed）。

**根因**：`Css/StyleResolver.cs` 的 `Lookup`（约 329 行起，P0-1/P0-2 改动后行号有漂移）：AngleSharp 值为空时按元素 `class` 属性的**书写序**逐个试键、返回**第一条命中**（约 338 行 `foreach (var c in cls.Split(...))`）。① 同特异性规则的源码序进不了这张表——`ScanUnsupported` 按键覆盖写，`.v`/`.clip` 各存各的，谁后声明无关；② 特异性是反的：class 先试、id 再试、tag 最后，代码上可读出 `#id{…}` 会被任意 `.cls{…}` 压住（这条未单独实测）。

**波及面**：`UnsupportedProps` 全部 30+ 属性（overflow / box-shadow / border-radius / flex / align-* / text-*…）凡「同一元素被 ≥2 条不同值规则命中」的写法都会取错——错不错取决于作者把类名排在第几位，是不可复现的外观错误。与 P0-1/P0-2 是**同一张表、不同的轴**：他们是键错（伪元素并进宿主），这条是多键命中时的**取值序**错；P0-1 的键方案落地后这条不会顺带好。

**修复方向**：表值改存 (值， 规则序号， 特异性)，`Lookup` 在元素命中的多个键里按「特异性 → 源码序」取最优，不再按 class 书写序取首个。改动集中在 `ScanUnsupported` 建键与 `Lookup` 取值两处，不动 AngleSharp 通路。

**验收**：E 行引擎 computed `clip`（硬裁不加省略号）且 N 行不再随 class 序翻转；补 `#id` vs `.class` 特异性用例的 xunit 断言；`tools/sweep.sh` 与金标零回归。

---

## 取证脚本与复现约定

- 圆角类数值来自：逐行扫描截图蓝区（`B−R ≥ 15 且 B > 200`）左右边界求弧长，2x 设备 px ÷ 实测比例 1.986 折 CSS px；引擎侧同法量 `dotnet run -- html login-page` 的 2x 光栅。
- 回归断言已固化成受跟踪测试 `tests/PaperSharp.Tests/PseudoScopeTests.cs`（23 条，含双向对照），不再依赖仓库外探针（`.scratch/radius/probe/` 只作临时取证）。
- 几何基线复跑：`bash tools/sweep.sh`；绘制层基线复跑：`bash tools/pixelsweep.sh [样例…]`。
- **并发量测必须错开 CDP 端口**：`boxdiff.mjs` 的调试端口默认写死 9411，同机多个会话同时量会互踩 CDP——表现为 `Execution context was destroyed`、或干脆量到半张图当真实差。`tools/pixelsweep.sh` 已改成默认按进程号取端口（`CDPPORT=…` 可覆盖）；手跑 `boxdiff` 时记得带 `--port`。P2-5 那两条「样例测不到」经复核就是这一条，不是页面问题。
- 待落刀补丁：`python .scratch/apply-layoutunit-margin-snap.py`（P2-7 的 margin 版，幂等、`--revert` 可撤）。已用私有端口复验两遍：challenge 12.5→12.1（位移带 6→3）、m7 24.4→24.1、framer-motion / fluent-docs / m1 三张守卫逐张不变；代价是 2 张 `PngGolden` 自比金标失效（40 红→42 红），等金标收敛后落。

