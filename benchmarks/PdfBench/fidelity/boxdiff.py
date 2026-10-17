#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""全盒对拍：把引擎布局出的每个 border-box 与无头 Chrome 的 getBoundingClientRect 逐个相比，
按文档顺序找出**第一处**开始漂移的元素。

cells.py 只有 109 个特性格，且一格 N 往往是上游累积的连带（第 3 页表格整体低 12px，
于是十几格一起掉分），从格子本身查不到根因。这个脚本换成整棵 DOM 树对表：
定位到「哪个元素的 y 第一次和 Chrome 不一致」，那才是根因元素。

用法：
  python fidelity/boxdiff.py                     # 默认 challenge.html
  python fidelity/boxdiff.py --html x.html --tol 0.6 --first 20 --prefix body[1]/div[3]

前置：PdfBench.Runner.exe rects 能跑（引擎侧），系统 Chrome 在默认路径。
产物：out/boxdiff.csv（path, tag, 引擎/真值 x y w h + 四个差值），stdout 打前 N 处偏差。
"""
from __future__ import annotations

import argparse
import csv
import io
import json
import re
import subprocess
import sys
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent          # benchmarks/PdfBench
SPEC = ROOT / "spec"
OUT = ROOT / "out"
RUNNER = ROOT / "PdfBench.Runner" / "bin" / "Debug" / "net10.0" / "PdfBench.Runner.exe"
CHROME = Path(r"C:\Program Files\Google\Chrome\Application\chrome.exe")

# 注入脚本：给每个元素编出与引擎同口径的 DomPath（tag[nth-of-type]，不成盒的 script/style 也占号），
# 并把坐标归一到最近的 .page 祖先（引擎侧同样减去页框，见 norm_engine）。
JS = """
document.addEventListener('DOMContentLoaded', function () {
  // 等图片解码完再量：auto 尺寸的 img 在 naturalWidth=0 时会塌成 0，真值就假高了
  Promise.all([...document.images].map(function (im) {
    return im.complete ? null : new Promise(function (r) { im.onload = im.onerror = r; });
  })).then(dump);
});
function dump() {
  function path(el) {
    if (el === document.body) return 'body[1]';
    if (!el.parentElement) return null;
    var tag = el.tagName.toLowerCase(), k = 1, s = el;
    while ((s = s.previousElementSibling)) if (s.tagName === el.tagName) k++;
    var p = path(el.parentElement);
    return p === null ? null : p + '/' + tag + '[' + k + ']';
  }
  var out = [];
  document.querySelectorAll('body *').forEach(function (el) {
    var pg = el.closest('.page') || document.body;
    if (el === pg) return;
    var r = el.getBoundingClientRect(), b = pg.getBoundingClientRect();
    out.push({p: path(el), t: el.tagName.toLowerCase(),
              x: r.left - b.left, y: r.top - b.top, w: r.width, h: r.height});
  });
  var pre = document.createElement('pre');
  pre.id = '__boxdump';
  pre.textContent = '<<BOXDUMP>>' + JSON.stringify(out) + '<</BOXDUMP>>';
  document.documentElement.appendChild(pre);
}
"""

RECT_RE = re.compile(r'^(?P<p>\S+) <(?P<t>[^>]+)> x=(?P<x>-?[\d.]+) y=(?P<y>-?[\d.]+) w=(?P<w>-?[\d.]+) h=(?P<h>-?[\d.]+)$')


def chrome_boxes(html: Path) -> list[dict]:
    """跑一次无头 Chrome，拿整棵树的页相对矩形。"""
    src = html.read_text(encoding='utf-8')
    stage = html.parent / f".boxdiff-{html.name}"
    # 脚本塞进 head 末尾；注入的 <pre> 挂在 documentElement 上，不参与 .page 统计。
    needle = '</head>'
    assert needle in src, '没有 </head>，无法注入测量脚本'
    stage.write_text(src.replace(needle, '<script>' + JS + '</script>' + needle, 1), encoding='utf-8')
    try:
        r = subprocess.run(
            [str(CHROME), '--headless=new', '--disable-gpu', '--virtual-time-budget=4000',
             '--dump-dom', stage.resolve().as_uri()],
            capture_output=True, text=True, encoding='utf-8', errors='replace', timeout=180)
    finally:
        stage.unlink(missing_ok=True)
    m = re.search(r'&lt;&lt;BOXDUMP&gt;&gt;(.*?)&lt;&lt;/BOXDUMP&gt;&gt;', r.stdout, re.S)
    if not m:
        m = re.search(r'<<BOXDUMP>>(.*?)<</BOXDUMP>>', r.stdout, re.S)
    if not m:
        raise SystemExit('Chrome 没吐出盒 dump——注入脚本没执行（检查页面报错）')
    return json.loads(m.group(1))


def engine_boxes(html: Path) -> list[dict]:
    """跑引擎 rects 子命令，解析成同一份记录（原点在 body，页相对稍后归一）。"""
    r = subprocess.run([str(RUNNER), 'rects', '', html.name], capture_output=True,
                       text=True, encoding='utf-8', errors='replace', cwd=str(ROOT))
    rows = []
    for line in r.stdout.splitlines():
        m = RECT_RE.match(line.strip())
        if m:
            g = m.groupdict()
            rows.append({'p': g['p'], 't': g['t'], **{k: float(g[k]) for k in 'xywh'}})
    if not rows:
        raise SystemExit('引擎 rects 没输出：\n' + (r.stderr or '')[-800:])
    return rows


def to_page_rel(rows: list[dict], pages: dict[str, dict]) -> dict[str, dict]:
    """按路径里的 body[1]/div[K] 归一到「所在页相对」，与 Chrome 侧同一口径。"""
    res = {}
    for e in rows:
        m = re.match(r'^body\[1\]/div\[(\d+)\]', e['p'])
        if not m:
            continue
        pg = pages.get('body[1]/div[' + m.group(1) + ']')
        if pg is None:
            continue
        res[e['p']] = {'t': e['t'], 'x': e['x'] - pg['x'], 'y': e['y'] - pg['y'], 'w': e['w'], 'h': e['h']}
    return res


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument('--html', default='challenge.html')
    ap.add_argument('--tol', type=float, default=0.6, help='任一维度超过这么多 px 记偏差')
    ap.add_argument('--first', type=int, default=20, help='打印按文档顺序的前 N 处偏差')
    ap.add_argument('--prefix', default='body[1]', help='只看这些路径前缀')
    ap.add_argument('--no-chrome', action='store_true', help='复用 out/boxes_chrome_<html>.json 缓存')
    ap.add_argument('--steps', action='store_true', help='按页打「dy 台阶 + 尺寸离群」归因表，代替逐行清单')
    args = ap.parse_args()

    html = SPEC / args.html
    cache = OUT / f'boxes_chrome_{html.stem}.json'
    if args.no_chrome and cache.exists():
        truth = json.loads(cache.read_text(encoding='utf-8'))
    else:
        truth = chrome_boxes(html)
        OUT.mkdir(exist_ok=True)
        cache.write_text(json.dumps(truth), encoding='utf-8')
    mine = engine_boxes(html)

    pages = {e['p']: e for e in mine if re.fullmatch(r'body\[1\]/div\[\d+\]', e['p'])}
    eng = to_page_rel(mine, pages)
    tru = {e['p']: e for e in truth}

    rows, misses = [], 0
    for p in sorted(eng, key=lambda k: (k.split('/').__len__(), k)):
        if not p.startswith(args.prefix):
            continue
        b = tru.get(p)
        if b is None:
            misses += 1
            continue
        e = eng[p]
        d = {k: e[k] - b[k] for k in 'xywh'}
        worst = max(abs(v) for v in d.values())
        rows.append({'path': p, 'tag': e['t'], 'ex': e['x'], 'ey': e['y'], 'ew': e['w'], 'eh': e['h'],
                     'bx': b['x'], 'by': b['y'], 'bw': b['w'], 'bh': b['h'],
                     **{('d' + k): v for k, v in d.items()}, 'worst': worst, 'off': worst > args.tol})

    OUT.mkdir(exist_ok=True)
    with io.open(OUT / 'boxdiff.csv', 'w', newline='', encoding='utf-8-sig') as f:
        w = csv.DictWriter(f, fieldnames=list(rows[0].keys()))
        w.writeheader()
        w.writerows(rows)

    bad = [r for r in rows if r['off']]
    # 文档顺序 = 页号 + 页内 y + 深度，第一处偏差就是根因候选
    page_of = lambda r: r['path'].split('/')[2]
    bad.sort(key=lambda r: (page_of(r), r['ey'], len(r['path'])))
    print(f'引擎盒 {len(eng)} / 真值盒 {len(tru)} / 可比 {len(rows)}（真值缺 {misses}）→ out/boxdiff.csv')
    print(f'偏差 > {args.tol}px 的盒：{len(bad)}')
    if args.steps:
        # 「dy 台阶」归因：一页里 dy 恒定 = 上游某处整体偏移，台阶发生处才是根因元素。
        # 逐页按文档顺序扫 dy，只打印 dy 变化超过 0.15px 的那些点，另附尺寸（w/h）离群盒。
        for pg in sorted({page_of(r) for r in rows}):
            seq = sorted([r for r in rows if page_of(r) == pg], key=lambda r: (r['ey'], len(r['path'])))
            steps, last = [], None
            for r in seq:
                dy = float(r['dy'])
                if last is None or abs(dy - last) > 0.15:
                    steps.append((dy, r))
                    last = dy
            outs = [r for r in seq if abs(float(r['dw'])) > 2 or abs(float(r['dh'])) > 1.5]
            if not steps and not outs:
                continue
            print(f'— {pg}：')
            for dy, r in steps:
                print(f'    dy{dy:+7.2f} 起于 y={r["ey"]:8.2f}  {r["path"]}   (真值 y={r["by"]:8.2f})')
            for r in sorted(outs, key=lambda r: -max(abs(float(r['dw'])), abs(float(r['dh']))))[:args.first]:
                print(f'    dw{float(r["dw"]):+7.2f} dh{float(r["dh"]):+7.2f}  '
                      f'引擎 {float(r["ew"]):7.2f}×{float(r["eh"]):7.2f} / 真值 {float(r["bw"]):7.2f}×{float(r["bh"]):7.2f}  {r["path"]}')
        return 0
    for r in bad[:args.first]:
        print(f"  dy{r['dy']:+7.2f} dx{r['dx']:+7.2f} dh{r['dh']:+7.2f} dw{r['dw']:+7.2f}  "
              f"y{r['ey']:8.2f}/{r['by']:8.2f}  {r['path']}")
    if len(bad) > args.first:
        print(f'  …另有 {len(bad) - args.first} 个，见 boxdiff.csv')
    return 0


if __name__ == '__main__':
    sys.exit(main())
