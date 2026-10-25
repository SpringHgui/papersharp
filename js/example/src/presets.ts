// Playground 预置样例。只喂引擎真正支持的 CSS 子集（flex、渐变、圆角、边框、表格、@page），
// 字体固定 sans-serif —— 浏览器 WASM 里没有系统字体，装上去的只有 Noto 子集。

import type { EngineFormat } from "./engine";

export interface Preset {
  id: string;
  name: { zh: string; en: string };
  /** 整页样例（如挑战页）不内联，构建前由 sync-engine.mjs 从仓内唯一真源拷进 public/samples/。 */
  src?: string;
  html: string;
  css: string;
  width: number;
  scale: number;
  format: EngineFormat;
  pageSize?: "a4" | "letter";
  icoSize?: number;
}

export const presets: Preset[] = [
  {
    // 默认样例：56 项特性的挑战页，A4 三页 PDF。真源 src/PaperSharpSamples/assets/challenge.html。
    id: "challenge",
    name: { zh: "挑战页", en: "Challenge" },
    src: "samples/challenge.html",
    width: 794, // 210mm @96dpi：iframe 视口与引擎布局同按页宽，两栏才能对齐
    scale: 1,
    format: "pdf",
    pageSize: "a4",
    html: "",
    css: "",
  },
  {
    id: "og-card",
    name: { zh: "OG 图卡", en: "OG card" },
    width: 1200,
    scale: 2,
    format: "png",
    html: `<div class="card">
  <div class="badge">PaperSharp</div>
  <h1>Compile HTML/CSS<br />into PDF</h1>
  <p>Managed C# drawing on Skia. No browser, no headless Chrome.</p>
  <div class="row">
    <span class="chip">PDF</span><span class="chip">PNG</span><span class="chip">SVG</span><span class="chip">ICO</span>
  </div>
</div>`,
    css: `.card {
  width: 100%; height: 630px;
  display: flex; flex-direction: column; justify-content: center;
  padding: 0 72px;
  background: linear-gradient(to bottom right, #eef2ff, #ffffff 55%, #fff7ed);
  font-family: sans-serif;
}
.badge { font-size: 20px; font-weight: 700; letter-spacing: 2.4px; color: #4338ca; text-transform: uppercase; }
h1 { margin-top: 18px; font-size: 84px; line-height: 1.05; font-weight: 800; color: #0f172a; }
p { margin-top: 22px; font-size: 28px; color: #475569; }
.row { margin-top: 40px; display: flex; gap: 12px; }
.chip { padding: 8px 18px; font-size: 20px; font-weight: 600; color: #1e293b;
        background: #ffffff; border-width: 1px; border-style: solid; border-color: #cbd5e1; border-radius: 9999px; }`,
  },
  {
    id: "invoice-cn",
    name: { zh: "中文发票", en: "CJK invoice" },
    width: 794,
    scale: 2,
    format: "pdf",
    pageSize: "a4",
    html: `<div class="doc">
  <div class="head">
    <h1>增值税普通发票</h1>
    <div class="meta">发票号码 20261007-0142<br />开票日期 2026 年 10 月 07 日</div>
  </div>
  <table>
    <thead><tr><th>项目</th><th>规格</th><th>数量</th><th>单价</th><th>金额</th></tr></thead>
    <tbody>
      <tr><td>渲染引擎授权</td><td>企业版 / 年</td><td>1</td><td>12,800.00</td><td>12,800.00</td></tr>
      <tr><td>并发吞吐扩容</td><td>4 线程</td><td>2</td><td>3,600.00</td><td>7,200.00</td></tr>
      <tr><td>中文子集字体包</td><td>Noto Sans SC</td><td>1</td><td>1,200.00</td><td>1,200.00</td></tr>
      <tr><td>报表模板定制</td><td>按页</td><td>36</td><td>80.00</td><td>2,880.00</td></tr>
    </tbody>
  </table>
  <div class="sum">
    <div><span>合计金额</span><b>¥ 24,080.00</b></div>
    <div><span>税额（6%）</span><b>¥ 1,444.80</b></div>
    <div class="total"><span>价税合计</span><b>¥ 25,524.80</b></div>
  </div>
  <p class="note">备注：本发票由 PaperSharp 直接从 HTML 模板生成，文字可选中、可检索。</p>
</div>`,
    css: `@page { size: A4; margin: 20mm 18mm; }
.doc { font-family: sans-serif; color: #0f172a; font-size: 13px; }
.head { display: flex; justify-content: space-between; align-items: flex-start;
        border-bottom-width: 2px; border-bottom-style: solid; border-bottom-color: #1e293b; padding-bottom: 10px; }
h1 { font-size: 22px; font-weight: 700; }
.meta { text-align: right; color: #475569; line-height: 1.6; }
table { width: 100%; border-collapse: collapse; margin-top: 14px; }
th, td { border-width: 1px; border-style: solid; border-color: #cbd5e1; padding: 7px 9px; text-align: left; }
th { background: #f1f5f9; font-weight: 600; }
.sum { margin-top: 16px; display: flex; flex-direction: column; align-items: flex-end; gap: 6px; }
.sum div { display: flex; gap: 24px; width: 260px; justify-content: space-between; }
.total { border-top-width: 1px; border-top-style: solid; border-top-color: #0f172a; padding-top: 6px; font-size: 15px; }
.note { margin-top: 20px; color: #64748b; font-size: 11px; }`,
  },
  {
    id: "pricing",
    name: { zh: "定价卡片", en: "Pricing cards" },
    width: 1200,
    scale: 2,
    format: "png",
    html: `<div class="wrap">
  <h2>Pricing</h2>
  <div class="cards">
    <div class="plan">
      <div class="name">Starter</div>
      <div class="price">¥ 0</div>
      <ul><li>Single process</li><li>PNG / JPEG</li><li>Community support</li></ul>
    </div>
    <div class="plan hot">
      <div class="name">Server</div>
      <div class="price">¥ 399</div>
      <ul><li>Thread-safe fan-out</li><li>Vector PDF</li><li>Paged invoices</li></ul>
    </div>
    <div class="plan">
      <div class="name">Edge</div>
      <div class="price">¥ 899</div>
      <ul><li>Browser WASM</li><li>Offline templates</li><li>Font subsetting</li></ul>
    </div>
  </div>
</div>`,
    css: `.wrap { width: 100%; padding: 56px 64px; background: #f8fafc; font-family: sans-serif; }
h2 { font-size: 40px; font-weight: 800; color: #0f172a; }
.cards { margin-top: 32px; display: flex; gap: 20px; }
.plan { flex: 1; padding: 26px 24px; background: #ffffff; border-width: 1px; border-style: solid;
        border-color: #e2e8f0; border-radius: 16px; }
.plan.hot { border-color: #4338ca; background: linear-gradient(to bottom, #eef2ff, #ffffff); }
.name { font-size: 18px; font-weight: 700; letter-spacing: 1px; text-transform: uppercase; color: #475569; }
.price { margin-top: 10px; font-size: 44px; font-weight: 800; color: #0f172a; }
ul { margin-top: 18px; }
li { font-size: 16px; color: #334155; padding: 5px 0; border-bottom-width: 1px; border-bottom-style: solid; border-bottom-color: #f1f5f9; }`,
  },
  {
    id: "report-long",
    name: { zh: "多页报表", en: "Multi-page report" },
    width: 794,
    scale: 1,
    format: "pdf",
    pageSize: "a4",
    html: `<div class="doc">
  <h1>季度渲染性能报告</h1>
  <p class="lead">本报告的全部版式 —— 包括页眉、页脚页码与跨页表格 —— 由同一份 HTML 模板生成。</p>
  <section>
    <h2>1. 结论摘要</h2>
    <p>单页 PDF 中位耗时约 30 毫秒，单线程吞吐约每秒 40 页。字体子集化后，纯文字单页体积从 2.5 MB 降到 50–120 KB，文字仍然可选中、可检索。</p>
    <p>层叠阶段改为整篇枚举一次后，同一进程对拍下净层叠提速 19 至 25 倍，表格两千行的真实入口从 74.4 秒降到 5.29 秒。</p>
  </section>
  <section>
    <h2>2. 样例分布</h2>
    <table>
      <thead><tr><th>样例</th><th>中位 ms</th><th>体积 KB</th><th>页数</th></tr></thead>
      <tbody>
        <tr><td>发票 invoice</td><td>31.0</td><td>94</td><td>1</td></tr>
        <tr><td>报表 report</td><td>38.4</td><td>112</td><td>2</td></tr>
        <tr><td>长表 m1</td><td>126.7</td><td>268</td><td>9</td></tr>
        <tr><td>文档 fluent-docs</td><td>135.0</td><td>188</td><td>4</td></tr>
        <tr><td>合同 contract</td><td>29.6</td><td>88</td><td>1</td></tr>
        <tr><td>物流单 waybill</td><td>27.1</td><td>76</td><td>1</td></tr>
        <tr><td>标签 label-grid</td><td>22.4</td><td>61</td><td>1</td></tr>
        <tr><td>简历 resume</td><td>33.8</td><td>101</td><td>2</td></tr>
        <tr><td>财报 annual</td><td>64.2</td><td>176</td><td>5</td></tr>
        <tr><td>工单 ticket</td><td>25.9</td><td>70</td><td>1</td></tr>
        <tr><td>菜单 menu-card</td><td>21.7</td><td>58</td><td>1</td></tr>
        <tr><td>证书 certificate</td><td>24.3</td><td>66</td><td>1</td></tr>
        <tr><td>周报 weekly</td><td>41.5</td><td>119</td><td>3</td></tr>
        <tr><td>手册 manual</td><td>97.8</td><td>231</td><td>7</td></tr>
      </tbody>
    </table>
    <p>跨页时表头每页自动重放，单行不被折断：thead 使用 table-header-group，tr 使用 break-inside: avoid。</p>
  </section>
  <section>
    <h2>3. 口径说明</h2>
    <p>数字取自本机 Release 构建的热态中位，非冷启动；负载会整体平移表格，但不改变相对结论。</p>
    <p>如需复核，请在干净克隆中重跑同一套语料，并把跳过项与失败项一并计入。</p>
  </section>
  <section>
    <h2>4. 复现步骤</h2>
    <p>第一步，取干净克隆，确认工作区不含任何未提交的产物；本机全绿不等于 CI 全绿。</p>
    <p>第二步，按同一顺序交错跑 A/B 两组，避免把机器升温误读成引擎提速。</p>
    <p>第三步，逐页记录耗时与体积，中位数与散布一起给出，只报均值会被一次离群值带走。</p>
    <p>第四步，把视觉对照附上：同一份 HTML 分别交给引擎与无头浏览器出图，按盒叠差，残差超过半像素的样例单独列出。</p>
    <p>以上任何一步省略，结论都只是参考，不是可复核的测量。</p>
  </section>
  <section>
    <h2>5. 已知边界</h2>
    <p>引擎不做浏览器兼容层：它要的是一份能确定地折成页面的 HTML/CSS，而不是在任何设备上都不塌的自适应界面。媒体查询里的视口单位按打印口径解释，滚动相关的行为没有意义。</p>
    <p>字体是这条链上最容易被低估的一环。子集化把整嵌字体的 2.5 MB 压到几十 KB，但代价是字形覆盖范围随文档变化——同一份模板换一批数据，缺字的表现可能从不会出现变成成片豆腐块。</p>
    <p>因此发布产物里只随包携带采用 SIL OFL 授权的字体子集。系统字体可以本机渲染，却不能作为分发时的默认答案。</p>
    <p>分页本身由块容器的断裂点决定：标题与紧随其后的段落应当绑在一起，否则页尾会出现孤零零的一行标题。这类规则靠人工排版能补，靠 CSS 写出来才能批量稳定。</p>
    <p>最后一件事：一致比快更难。同一份输入跑两次得到两张不同的图，任何性能数字都会失去意义，所以差值表与墨迹原点都进了回归门禁。</p>
  </section>
  <section>
    <h2>6. 附录：逐页明细</h2>
    <p>下表刻意排得比一页长，用来验证跨页时表头重放与行不被折断；页号来自 counter(page)，不是手写常量。</p>
    <table>
      <thead><tr><th>页</th><th>元素数</th><th>层叠 ms</th><th>绘制 ms</th><th>编码 ms</th></tr></thead>
      <tbody>
        <tr><td>1</td><td>184</td><td>3.1</td><td>6.4</td><td>2.2</td></tr>
        <tr><td>2</td><td>207</td><td>3.4</td><td>7.0</td><td>2.5</td></tr>
        <tr><td>3</td><td>196</td><td>3.2</td><td>6.7</td><td>2.3</td></tr>
        <tr><td>4</td><td>233</td><td>3.9</td><td>7.8</td><td>2.9</td></tr>
        <tr><td>5</td><td>211</td><td>3.5</td><td>7.1</td><td>2.6</td></tr>
        <tr><td>6</td><td>198</td><td>3.3</td><td>6.9</td><td>2.4</td></tr>
        <tr><td>7</td><td>226</td><td>3.7</td><td>7.5</td><td>2.8</td></tr>
        <tr><td>8</td><td>241</td><td>4.1</td><td>8.2</td><td>3.1</td></tr>
        <tr><td>9</td><td>205</td><td>3.4</td><td>7.0</td><td>2.5</td></tr>
        <tr><td>10</td><td>190</td><td>3.0</td><td>6.3</td><td>2.1</td></tr>
        <tr><td>11</td><td>218</td><td>3.6</td><td>7.3</td><td>2.7</td></tr>
        <tr><td>12</td><td>229</td><td>3.8</td><td>7.6</td><td>2.8</td></tr>
        <tr><td>13</td><td>199</td><td>3.3</td><td>6.8</td><td>2.4</td></tr>
        <tr><td>14</td><td>212</td><td>3.5</td><td>7.2</td><td>2.6</td></tr>
        <tr><td>15</td><td>236</td><td>4.0</td><td>8.0</td><td>3.0</td></tr>
        <tr><td>16</td><td>188</td><td>3.1</td><td>6.5</td><td>2.2</td></tr>
        <tr><td>17</td><td>203</td><td>3.4</td><td>6.9</td><td>2.5</td></tr>
        <tr><td>18</td><td>221</td><td>3.6</td><td>7.4</td><td>2.7</td></tr>
        <tr><td>19</td><td>244</td><td>4.2</td><td>8.3</td><td>3.2</td></tr>
        <tr><td>20</td><td>197</td><td>3.3</td><td>6.8</td><td>2.4</td></tr>
        <tr><td>21</td><td>209</td><td>3.5</td><td>7.1</td><td>2.6</td></tr>
        <tr><td>22</td><td>231</td><td>3.8</td><td>7.7</td><td>2.9</td></tr>
      </tbody>
    </table>
  </section>
</div>`,
    css: `@page {
  size: A4;  margin: 24mm 20mm;
  @top-center    { content: "季度渲染性能报告"; font-size: 9px; color: #94a3b8; }
  @bottom-center { content: "第 " counter(page, cjk-ideographic) " 页 / 共 " counter(pages) " 页"; font-size: 9px; color: #94a3b8; }
}
.doc { font-family: sans-serif; color: #0f172a; font-size: 12.5px; line-height: 1.75; }
h1 { font-size: 24px; font-weight: 800; }
.lead { margin-top: 8px; color: #475569; font-size: 13px; }
h2 { margin-top: 22px; font-size: 16px; font-weight: 700; color: #1e293b; }
section { break-inside: auto; }
table { width: 100%; border-collapse: collapse; margin-top: 10px; }
th, td { border-width: 1px; border-style: solid; border-color: #cbd5e1; padding: 6px 8px; text-align: left; }
th { background: #f1f5f9; }
tr { break-inside: avoid; }`,
  },
  {
    id: "favicon",
    name: { zh: "Favicon", en: "Favicon" },
    width: 512,
    scale: 1,
    format: "ico",
    icoSize: 64,
    html: `<div class="box">
  <div class="mark">P</div>
</div>`,
    css: `.box { width: 512px; height: 512px; display: flex; align-items: center; justify-content: center;
       background: linear-gradient(to bottom right, #4f46e5, #0ea5e9); }
.mark { width: 300px; height: 300px; display: flex; align-items: center; justify-content: center;
        background: #ffffff; border-radius: 64px; font-family: sans-serif;
        font-size: 200px; font-weight: 800; color: #4f46e5; }`,
  },
];
