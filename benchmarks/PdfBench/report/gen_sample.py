# -*- coding: utf-8 -*-
"""按 CONTRACT.md 生成 report/sample/ 三份假 CSV（7 库全指标，数值合理且可区分）。
真数据到位后不需要再跑本脚本；report.py --data-dir ../out 即可。"""
import csv
import json
import os
import random

random.seed(42)
HERE = os.path.dirname(os.path.abspath(__file__))
SAMPLE = os.path.join(HERE, "sample")
os.makedirs(SAMPLE, exist_ok=True)

ADAPTERS = ["papersharp", "puppeteer", "wkhtmltopdf", "itext7",
            "pdfsharp", "questpdf", "aspose"]
CONTRACT_COLS = ["puppeteer", "wkhtmltopdf", "itext7", "pdfsharp",
                 "questpdf", "aspose", "papersharp"]

# (latency_med ms, rss MB, size KB, alloc KB / None=外部进程, thr_serial, thr_par4,
#  coldstart ms, ssim_base, pages)
BASE = {
    "papersharp":  (138.0, 218.0, 61.5,  8200.0, 6.4,  17.2, 610.0, 0.93, 3),
    "puppeteer":   (1840.0, 462.0, 96.3, None,   0.52, 1.18, 2480.0, 0.995, 3),
    "wkhtmltopdf": (915.0, 176.0, 108.7, None,   1.02, 2.35, 820.0, 0.86, 3),
    "itext7":      (352.0, 296.0, 44.8,  15400.0, 2.6,  8.4,  1180.0, 0.78, 3),
    "pdfsharp":    (48.0,  92.0,  31.2,  3100.0, 18.5, 52.3, 355.0, 0.42, 4),
    "questpdf":    (612.0, 341.0, 79.4,  21700.0, 1.6,  4.1,  1520.0, 0.63, 3),
    "aspose":      (447.0, 388.0, 70.1,  18900.0, 2.2,  5.9,  2050.0, 0.72, 3),
}
N = "NaN"


def jit(x, rel=0.05):
    return round(x * (1 + random.uniform(-rel, rel)), 3)


def build_results():
    rows = []
    for a in ADAPTERS:
        med, rss, size, alloc, ts, tp, cold, _, pages = BASE[a]
        lat_min, lat_med = jit(med * 0.71), jit(med)
        lat_mean, lat_p95, lat_max = jit(med * 1.08), jit(med * 1.52), jit(med * 2.3)
        managed = alloc is not None
        rows += [
            [a, "latency_min", lat_min, "ms"],
            [a, "latency_med", lat_med, "ms"],
            [a, "latency_mean", lat_mean, "ms"],
            [a, "latency_p95", lat_p95, "ms"],
            [a, "latency_max", lat_max, "ms"],
            [a, "alloc_med", jit(alloc) if managed else N, "KB"],
            [a, "gen0", jit(med * 0.9) if managed else N, "count"],
            [a, "gen1", jit(med * 0.12) if managed else N, "count"],
            [a, "gen2", jit(med * 0.01) if managed else N, "count"],
            [a, "rss_peak", jit(rss, 0.03), "MB"],
            [a, "size", jit(size, 0.02), "KB"],
            [a, "pages", pages, "count"],
            [a, "throughput_serial", jit(ts), "docs/s"],
            [a, "throughput_par4", jit(tp), "docs/s"],
            [a, "coldstart", jit(cold), "ms"],
            [a, "threadsafe", 1 if a != "questpdf" else 0, "bool"],
        ]
    return rows


def build_fidelity():
    rows = []
    for a in ADAPTERS:
        _, _, _, _, _, _, _, ssim_base, pages = BASE[a]
        page_adj = {1: 0.035, 2: -0.045, 3: -0.075, 4: None}
        for p in range(1, pages + 1):
            adj = page_adj.get(p)
            if adj is None:  # 超出 truth 页数 → NaN
                rows.append([a, p, N, N, N, jit(random.uniform(8, 25), 0.2)])
                continue
            ssim = min(0.999, max(0.05, ssim_base + adj + random.uniform(-0.02, 0.02)))
            mse = round((1 - ssim) * 380 + random.uniform(0, 12), 2)
            pdp = round((1 - ssim) * 26 + random.uniform(0, 2.5), 3)
            ink = jit(random.uniform(14, 38), 0.15)
            rows.append([a, p, round(ssim, 4), mse, pdp, ink])
    return rows


# 每库默认值 + 显式覆盖；? 少量留未判定
FEAT_DEFAULT = {"puppeteer": "Y", "wkhtmltopdf": "Y", "itext7": "A",
                "pdfsharp": "N", "questpdf": "A", "aspose": "A",
                "papersharp": "Y"}
FEAT_OVERRIDE = {
    "wkhtmltopdf": {"text-shadow": "N", "border-groove-ridge": "N",
                    "box-shadow-inset": "N", "letter-spacing": "A",
                    "page-number-badge": "A", "word-break-all": "N"},
    "itext7": {"headings": "Y", "weights": "Y", "text-color": "Y",
               "border-solid": "Y", "border-width-2-4-8": "Y",
               "table-thead-tfoot": "Y", "table-cell-borders": "Y",
               "radial-gradient": "N", "box-shadow-inset": "N",
               "border-groove-ridge": "N", "text-shadow": "N",
               "letter-spacing": "N", "text-indent": "N",
               "flex-margin-auto": "N", "list-style-circle-none": "N",
               "inline-alpha-highlight": "N", "radius-plus-dashed": "N",
               "page-number-badge": "?"},
    "pdfsharp": {"headings": "Y", "text-color": "Y", "line-height": "Y",
                 "border-solid": "Y", "table-thead-tfoot": "A",
                 "table-cell-borders": "A", "list-ul-marker-color": "A",
                 "image-jpeg": "A", "page-header-footer": "A",
                 "text-align-justify": "?", "weights": "A"},
    "questpdf": {"headings": "Y", "text-color": "Y", "border-solid": "Y",
                 "box-shadow-soft": "N", "box-shadow-inset": "N",
                 "radial-gradient": "N", "opacity": "N",
                 "border-double": "N", "border-groove-ridge": "N",
                 "radius-circle-ellipse": "N", "word-break-all": "N",
                 "flex-nested-ratios": "N", "page-number-badge": "N",
                 "text-shadow": "N", "image-png-alpha": "?"},
    "aspose": {"headings": "Y", "weights": "Y", "text-color": "Y",
               "border-solid": "Y", "border-dashed": "Y", "border-dotted": "Y",
               "list-ul-marker-color": "Y", "table-thead-tfoot": "Y",
               "table-striped": "Y", "table-cell-borders": "Y",
               "table-colspan-footer": "Y", "image-jpeg": "Y",
               "page-header-footer": "Y", "page-number-badge": "Y",
               "radial-gradient": "Y", "linear-gradient-multi-stop": "Y",
               "box-shadow-multi": "N", "border-groove-ridge": "N",
               "text-shadow": "N", "flex-margin-auto": "?",
               "inline-alpha-highlight": "N", "radius-plus-dashed": "N"},
    "papersharp": {"text-shadow": "A", "border-groove-ridge": "A",
                   "letter-spacing": "A", "page-number-badge": "A",
                   "radius-plus-dashed": "A"},
}


def build_features(features):
    rows = []
    for f in features["features"]:
        fid, grp, page = f["id"], f["group"], f["page"]
        cells = []
        for a in CONTRACT_COLS:
            v = FEAT_OVERRIDE.get(a, {}).get(fid)
            if v is None:
                v = FEAT_DEFAULT[a]
                if v == "A" and random.random() < 0.15:
                    v = "N"
                if v == "Y" and random.random() < 0.06:
                    v = "A"
            cells.append(v)
        rows.append([fid, grp, page] + cells)
    return rows


def write_csv(path, header, rows):
    with open(path, "w", newline="", encoding="utf-8") as fh:
        w = csv.writer(fh)
        w.writerow(header)
        w.writerows(rows)


def main():
    with open(os.path.join(HERE, os.pardir, "spec", "features.json"),
              encoding="utf-8") as fh:
        features = json.load(fh)
    write_csv(os.path.join(SAMPLE, "results.csv"),
              ["adapter", "metric", "value", "unit"], build_results())
    write_csv(os.path.join(SAMPLE, "fidelity.csv"),
              ["adapter", "page", "ssim", "mse", "pixel_diff_pct",
               "ink_coverage_pct"], build_fidelity())
    write_csv(os.path.join(SAMPLE, "features_matrix.csv"),
              ["feature_id", "group", "pages"] + CONTRACT_COLS,
              build_features(features))
    print("sample CSVs written to", SAMPLE)


if __name__ == "__main__":
    main()
