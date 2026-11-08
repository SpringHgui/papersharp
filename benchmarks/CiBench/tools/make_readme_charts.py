#!/usr/bin/env python3
"""Generate compact, theme-aware SVG charts from a CiBench results snapshot.

The root README lays these out four per row. GitHub renders Mermaid blocks as
full-width, fixed-height images that cannot be scaled or placed side by side,
so the README uses small standalone SVGs instead (click any chart for its
full-size source). Everything is derived from the checked-in results JSON.

Usage:
    python benchmarks/CiBench/tools/make_readme_charts.py \
        benchmarks/CiBench/results/v1.0.3.json \
        benchmarks/CiBench/results/charts
"""

from __future__ import annotations

import json
import math
import sys
from pathlib import Path
from xml.sax.saxutils import escape

PALETTE = {
    "papersharp": "#1f6feb",
    "puppeteer": "#d97706",
    "playwright": "#2da44e",
    "itext": "#8250df",
    "ironpdf": "#cf222e",
}
LABELS = {
    "papersharp": "PaperSharp",
    "puppeteer": "Puppeteer",
    "playwright": "Playwright",
    "itext": "iText",
    "ironpdf": "IronPdf",
}

W, H = 240, 160          # logical drawing space (kept for label geometry)
SCALE = 2                # render at 2x so charts stay legible when shown large
RW, RH = W * SCALE, H * SCALE
X0, X1, Y0, Y1 = 36, 232, 34, 118

STYLE = """
    text { font-family: -apple-system,BlinkMacSystemFont,"Segoe UI",Helvetica,Arial,sans-serif }
    .bg { fill:#ffffff }
    .ttl { fill:#1f2328; font-size:13px; font-weight:600 }
    .unit { fill:#656d76; font-size:9px }
    .grid { stroke:#d8dee4; stroke-width:.7 }
    .axis { fill:#8c959f; font-size:8px }
    .val  { fill:#1f2328; font-size:10px; font-weight:600 }
    .cat  { fill:#57606a; font-size:9px }
    .note { fill:#8c959f; font-size:8px }
    .na   { fill:#8c959f; font-size:9px }
    @media (prefers-color-scheme: dark) {
      .bg { fill:#0d1117 }
      .ttl, .val { fill:#e6edf3 }
      .unit, .cat, .note, .na { fill:#8b949e }
      .axis { fill:#6e7681 }
      .grid { stroke:#30363d }
    }
"""

# Presentation attributes are duplicated on every element as a fallback: some
# README renderers strip <style> out of SVGs. CSS wins when it survives, so the
# dark-theme override still works; without CSS the light theme stays readable.
FONT_ATTR = 'font-family="Arial,Helvetica,sans-serif"'
TEXT_ATTRS = {
    "ttl": 'fill="#1f2328" font-size="13" font-weight="600"',
    "unit": 'fill="#656d76" font-size="9"',
    "axis": 'fill="#8c959f" font-size="8"',
    "val": 'fill="#1f2328" font-size="10" font-weight="600"',
    "cat": 'fill="#57606a" font-size="9"',
    "note": 'fill="#8c959f" font-size="8"',
    "na": 'fill="#8c959f" font-size="9"',
}


def txt(cls: str, x: float, y: float, content: str, anchor: str = "") -> str:
    a = f' text-anchor="{anchor}"' if anchor else ""
    return (f'<text class="{cls}" {TEXT_ATTRS[cls]} {FONT_ATTR} x="{x}" y="{y}"{a}>'
            f'{escape(content)}</text>')


def batch300(engine: dict) -> dict | None:
    for b in engine.get("batches") or []:
        if b.get("documents") == 300:
            return b
    return None


def get(engine: dict, path: str) -> float | None:
    cur: object = engine
    for part in path.split("."):
        if isinstance(cur, dict):
            cur = cur.get(part)
        elif isinstance(cur, list) and part.isdigit():
            idx = int(part)
            cur = cur[idx] if idx < len(cur) else None
        else:
            return None
        if cur is None:
            return None
    return float(cur) if isinstance(cur, (int, float)) else None


def batch_get(engine: dict, key: str) -> float | None:
    b = batch300(engine)
    return get(b or {}, key)


CHARTS = [
    ("01-cold-start", "Cold start - 1 PDF", "ms", "lower is better",
     lambda e: get(e, "cold.meanMs")),
    ("02-warm-start", "Warm start - 1 PDF", "ms", "lower is better",
     lambda e: get(e, "warm.meanMs")),
    ("03-avg-per-doc", "300 docs - ms / doc", "ms", "lower is better",
     lambda e: batch_get(e, "avgMsPerDoc")),
    ("04-latency-p95", "300 docs - latency P95", "ms", "lower is better",
     lambda e: batch_get(e, "latencyP95Ms")),
    ("05-throughput", "300 docs - throughput", "docs/s", "higher is better",
     lambda e: batch_get(e, "throughputDps")),
    ("06-rss-peak", "300 docs - RSS peak", "MB", "process tree; lower is better",
     lambda e: batch_get(e, "rssPeakMb")),
    ("07-managed-alloc", "300 docs - managed alloc", "MB", "lower is better",
     lambda e: batch_get(e, "allocMbTotal")),
    ("08-pdf-size", "PDF size", "KB", "single invoice; smaller is better",
     lambda e: get(e, "warm.sizeKb") or get(e, "cold.sizeKbMean")),
]


def fmt(v: float) -> str:
    if v >= 1000:
        return f"{v:,.0f}"
    if v >= 100:
        return f"{v:.0f}"
    if v >= 10:
        return f"{v:.1f}"
    return f"{v:.2f}".rstrip("0").rstrip(".")


def nice_max(v: float) -> float:
    if not v or v <= 0 or math.isnan(v):
        return 1.0
    exp = math.floor(math.log10(v))
    f = v / (10 ** exp)
    for s in (1, 1.2, 1.5, 2, 2.5, 3, 4, 5, 6, 8, 10):
        if s >= f:
            return s * (10 ** exp)
    return 10 ** (exp + 1)


def svg_chart(title: str, unit: str, note: str, engines: list[dict], read) -> str:
    names = [LABELS.get(e.get("name", ""), e.get("display", e.get("name", "?"))) for e in engines]
    values = [None if e.get("status") != "ok" else read(e) for e in engines]
    values = [None if v is None or math.isnan(v) or math.isinf(v) else v for v in values]
    good = [v for v in values if v is not None]
    y_max = nice_max(max(good) * 1.18) if good else 1.0

    desc = "; ".join(
        f"{n}: {fmt(v) if v is not None else 'N/A'} {unit}" for n, v in zip(names, values)
    )
    out = [
        f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 {RW} {RH}" width="{RW}" height="{RH}" '
        'role="img" aria-label="' + escape(title) + '">',
        f"<title>{escape(title)} ({escape(unit)})</title>",
        f"<desc>{escape(desc)}</desc>",
        f"<style>{STYLE}</style>",
        f'<rect class="bg" fill="#ffffff" x="0" y="0" width="{RW}" height="{RH}" rx="{8 * SCALE}"/>',
        '<g transform="scale(2)">',
        txt("ttl", 8, 19, title),
        txt("unit", X1, 19, unit, "end"),
    ]

    for g in range(5):
        y = Y1 - (Y1 - Y0) * g / 4
        out.append(f'<line class="grid" stroke="#d8dee4" stroke-width=".7" x1="{X0}" y1="{y:.1f}" x2="{X1}" y2="{y:.1f}"/>')
        out.append(txt("axis", X0 - 5, round(y + 3, 1), fmt(y_max * g / 4), "end"))

    n = max(1, len(names))
    group_w = (X1 - X0) / n
    bar_w = min(28.0, group_w * 0.56)
    for i, (name, value, engine) in enumerate(zip(names, values, engines)):
        cx = X0 + group_w * (i + 0.5)
        if value is None:
            out.append(f'<rect fill="none" stroke="#d8dee4" stroke-width=".7" x="{cx - bar_w / 2:.1f}" y="{Y1 - 12}" width="{bar_w:.1f}" height="12" rx="2"/>')
            out.append(txt("na", round(cx, 1), Y1 - 17, "N/A", "middle"))
        else:
            h = max(2.0, min(Y1 - Y0, value / y_max * (Y1 - Y0)))
            y = Y1 - h
            color = PALETTE.get(engine.get("name", ""), "#6e7781")
            out.append(f'<rect x="{cx - bar_w / 2:.1f}" y="{y:.1f}" width="{bar_w:.1f}" height="{h:.1f}" rx="2.5" fill="{color}"/>')
            out.append(txt("val", round(cx, 1), round(max(Y0 + 8, y - 5), 1), fmt(value), "middle"))
        out.append(txt("cat", round(cx, 1), Y1 + 16, name, "middle"))

    out.append(txt("note", 8, H - 7, note))
    out.append("</g>")
    out.append("</svg>")
    return "\n".join(out) + "\n"


def main() -> int:
    if len(sys.argv) != 3:
        print(__doc__, file=sys.stderr)
        return 2
    src, dst = Path(sys.argv[1]), Path(sys.argv[2])
    data = json.loads(src.read_text(encoding="utf-8"))
    engines = data.get("engines") or []
    if not engines:
        print("no engines in snapshot", file=sys.stderr)
        return 3
    dst.mkdir(parents=True, exist_ok=True)
    for stem, title, unit, note, read in CHARTS:
        target = dst / f"{stem}.svg"
        target.write_text(svg_chart(title, unit, note, engines, read), encoding="utf-8")
        print(f"wrote {target}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
