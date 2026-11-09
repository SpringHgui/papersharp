// 基准数字的唯一来源：README 表与 benchmarks/CiBench/results/v1.0.4.{json,csv}。
// 改数时三处一起改；这里只放数，文案与表头在 copy.ts。

export const SNAPSHOT = "v1.0.4";

export type BenchRow = {
  name: string;
  runtime: { zh: string; en: string };
  coldMs: number;
  warmMs: number;
  coldWarm: string;
  perDocMs: number;
  docsPerSec: number;
  p95Ms: number;
  rssPeakMb: number;
  sizeKb: number;
  ours?: boolean;
};

/** 300 张并发档，同机同语料；IronPdf 为 trial（输出含水印），仅作对照。 */
export const BENCH_ROWS: BenchRow[] = [
  {
    name: "PaperSharp",
    runtime: { zh: "纯托管自绘（无浏览器）", en: "Managed rasterizer (no browser)" },
    coldMs: 1045.9,
    warmMs: 73.0,
    coldWarm: "14.3×",
    perDocMs: 15.6,
    docsPerSec: 64.05,
    p95Ms: 61.6,
    rssPeakMb: 148.2,
    sizeKb: 170.8,
    ours: true,
  },
  {
    name: "PuppeteerSharp 25.8.0",
    runtime: { zh: "Chromium / Blink（CDP）", en: "Chromium / Blink (CDP)" },
    coldMs: 1190.0,
    warmMs: 120.2,
    coldWarm: "9.9×",
    perDocMs: 51.7,
    docsPerSec: 19.35,
    p95Ms: 222.5,
    rssPeakMb: 1319.4,
    sizeKb: 140.3,
  },
  {
    name: "Microsoft.Playwright 1.64.1",
    runtime: { zh: "Chromium / Blink（driver）", en: "Chromium / Blink (driver)" },
    coldMs: 1358.6,
    warmMs: 117.5,
    coldWarm: "11.6×",
    perDocMs: 51.3,
    docsPerSec: 19.5,
    p95Ms: 207.0,
    rssPeakMb: 1822.6,
    sizeKb: 108.9,
  },
  {
    name: "iText 7 pdfHTML 6.3.4",
    runtime: { zh: "解析型布局（无浏览器）", en: "Parsing layout (no browser)" },
    coldMs: 1367.0,
    warmMs: 171.3,
    coldWarm: "8.0×",
    perDocMs: 46.2,
    docsPerSec: 21.67,
    p95Ms: 233.6,
    rssPeakMb: 367.2,
    sizeKb: 191.0,
  },
  {
    name: "IronPdf 2026.11.0.1 (trial)",
    runtime: { zh: "Chromium / Blink（自带构建）", en: "Chromium / Blink (bundled)" },
    coldMs: 6353.2,
    warmMs: 244.9,
    coldWarm: "25.9×",
    perDocMs: 58.8,
    docsPerSec: 16.99,
    p95Ms: 254.4,
    rssPeakMb: 2536.6,
    sizeKb: 110.3,
  },
];

export const CHART_FILES = [
  "01-cold-start.svg",
  "03-avg-per-doc.svg",
  "05-throughput.svg",
  "06-rss-peak.svg",
] as const;

/** 表格列与小数位：数字取自 BENCH_ROWS，渲染时统一补千分位。 */
type NumCol = Exclude<keyof BenchRow, "name" | "runtime" | "ours" | "coldWarm">;
export type Col = { key: NumCol; digits: number } | { key: "coldWarm" };

export const COLS: Col[] = [
  { key: "coldMs", digits: 1 },
  { key: "warmMs", digits: 1 },
  { key: "coldWarm" },
  { key: "perDocMs", digits: 1 },
  { key: "docsPerSec", digits: 2 },
  { key: "p95Ms", digits: 1 },
  { key: "rssPeakMb", digits: 1 },
  { key: "sizeKb", digits: 1 },
];

export function fmt(v: number, digits: number): string {
  return v.toLocaleString("en-US", {
    minimumFractionDigits: digits,
    maximumFractionDigits: digits,
  });
}
