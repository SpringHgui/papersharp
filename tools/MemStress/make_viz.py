import io, json, os, statistics as st

OUT = os.path.join(os.path.dirname(__file__), 'out')
d = json.load(io.open(os.path.join(OUT, 'data.json'), encoding='utf-8'))

CHART = ['L_dense_same', 'K_dense_vary', 'E_vary600_p400', 'M_serial2000', 'N_par1000d8', 'P_server_par8']
LABEL = {
    'L_dense_same': '内容相同 · 串行 120 份',
    'K_dense_vary': '每份不同 · 串行 120 份（密采）',
    'E_vary600_p400': '每份不同 400 汉字 · 串行 600 份',
    'M_serial2000': '每份不同 800 汉字 · 串行 2000 份',
    'N_par1000d8': '每份不同 800 汉字 · 8 路并发 1000 份',
    'P_server_par8': 'Server GC · 8 路并发 400 份',
    'I_p2000': '每份不同 2000 汉字 · 串行 200 份',
    'F_vary600_p3000': '每份不同 3000 汉字 · 串行 600 份',
    'H_p50': '每份不同 50 汉字 · 串行 200 份',
    'G_par400d4': '每份不同 400 汉字 · 4 路并发 400 份',
    'O_server_serial': 'Server GC · 串行 400 份',
}
NOTE = {
    'L_dense_same': '对照组：200 份同一 HTML',
    'K_dense_vary': '每份文字都不同',
    'E_vary600_p400': '字形子集缓存 34 份内即填满',
    'M_serial2000': '量级最大的一轮',
    'N_par1000d4': '',
    'N_par1000d8': '并发不额外抬高平台',
    'P_server_par8': 'Server GC 提交更多托管段',
    'I_p2000': '字符集最宽的串行轮',
    'F_vary600_p3000': '3000 个不同汉字，平台反而更低',
    'H_p50': '50 个不同汉字',
    'G_par400d4': '4 路并发',
    'O_server_serial': 'Server GC 串行',
}


def q(a, f):
    return a[min(len(a) - 1, int(len(a) * f))]


rows, series = [], {}
for k, v in d.items():
    s = sorted(v['series'], key=lambda r: r['docs'])
    last = s[-1]['docs']
    steady = [r for r in s if r['docs'] >= last * 0.25]
    p = sorted(r['priv'] for r in steady)
    w = sorted(r['ws'] for r in steady)
    marg = (steady[-1]['priv'] - steady[0]['priv']) / max(1, steady[-1]['docs'] - steady[0]['docs']) * 1000
    pk = v['meta'].get('peak')
    ag = v['meta'].get('afterGc')
    rows.append(dict(key=k, label=LABEL[k], docs=last,
                     p50=round(q(p, .5)), p95=round(q(p, .95)), wp95=round(q(w, .95)),
                     peak=round(float(pk[0])) if pk else None,
                     peakws=round(float(pk[1])) if pk else None,
                     aftergc=round(float(ag[0])) if ag else None,
                     retained=round(float(ag[1])) if ag else None,
                     marg=round(marg), msd=round(s[-1]['msd']),
                     tps=float(v['meta'].get('tps') or 0), note=NOTE.get(k, '')))
    if k in CHART:
        series[k] = [[r['docs'], round(r['priv']), round(r['ws']), round(r['heap']),
                      r.get('sub') or 0, r.get('fb') or 0, round(r['alloc'])] for r in s]

table = json.dumps(rows, ensure_ascii=False)
curves = json.dumps({'order': CHART, 'label': {k: LABEL[k] for k in CHART}, 'data': series}, ensure_ascii=False)

html = """<section id="mem-dashboard" style="font-family:var(--font-sans);font-size:13px;color:var(--seed-fg)">
  <style>
    #mem-dashboard .wrap{display:flex;flex-direction:column;gap:14px}
    #mem-dashboard .row{display:flex;flex-wrap:wrap;gap:10px 18px;align-items:center}
    #mem-dashboard label{display:inline-flex;gap:6px;align-items:center;font-size:12px;cursor:pointer}
    #mem-dashboard select{font:inherit;padding:2px 6px}
    #mem-dashboard .sw{width:12px;height:12px;border-radius:3px;display:inline-block}
    #mem-dashboard svg{display:block;width:100%;height:auto}
    #mem-dashboard .ax{fill:var(--seed-muted);font-size:11px}
    #mem-dashboard .grid{stroke:var(--border-subtle);stroke-width:1}
    #mem-dashboard .tt{position:absolute;pointer-events:none;background:var(--card-bg);border:1px solid var(--border-standard);border-radius:6px;padding:6px 8px;font-size:12px;display:none;white-space:nowrap}
    #mem-dashboard .chartbox{position:relative}
    #mem-dashboard table{border-collapse:collapse;width:100%;font-size:12px}
    #mem-dashboard th,#mem-dashboard td{text-align:right;padding:4px 6px;border-bottom:1px solid var(--border-subtle)}
    #mem-dashboard th:first-child,#mem-dashboard td:first-child{text-align:left}
    #mem-dashboard .num{font-family:var(--font-code)}
    #mem-dashboard .ap{font-size:12px;color:var(--seed-muted);line-height:1.6}
  </style>
  <div class="wrap">
    <div>
      <div style="font-weight:500">批量生成 PDF 时的内存曲线</div>
      <div class="row" style="margin-top:6px">
        <label>度量
          <select id="md-metric">
            <option value="1">私有字节 priv（MB，已提交，不受系统 trim）</option>
            <option value="2">工作集 ws（MB，任务管理器口径）</option>
            <option value="3">托管堆 heap（MB）</option>
            <option value="6">回退缓存条目数（条）</option>
            <option value="5">子集字面缓存条目数（条，上限 48）</option>
          </select>
        </label>
        <label>横轴
          <select id="md-x">
            <option value="abs">文档数（份）</option>
            <option value="pct">批量进度（%）</option>
          </select>
        </label>
      </div>
      <div class="row" id="md-series" style="margin-top:2px"></div>
      <div class="chartbox">
        <svg id="md-svg" viewBox="0 0 760 320" role="img" aria-label="内存随文档数变化曲线"></svg>
        <div class="tt" id="md-tt" role="tooltip"></div>
      </div>
    </div>

    <div class="card" style="padding:10px 12px">
      <div class="viz-stat-label">一句话结论</div>
      <div class="viz-stat-value" style="font-size:13px;font-weight:500">内容各异的批量：前 ~35 份冲到 ~300MB，再缓涨到 ~340MB 封顶，2000 份不再变</div>
      <div class="text-small text-muted">相同内容的批量全程 ~75MB；Full GC 后托管堆只剩 9.4MB，多出来的是 Skia/DWrite 原生侧不归还</div>
    </div>

    <div>
      <div style="font-weight:500;margin-bottom:6px">各轮实测汇总（单位 MB，稳态取后 75% 采样点）</div>
      <div class="table-responsive"><table id="md-table">
        <thead><tr><th>场景</th><th>份数</th><th>priv 中位</th><th>priv p95</th><th>priv 峰值</th><th>ws 峰值</th><th>Full GC 后</th><th>边际 MB/千份</th><th>ms/份</th></tr></thead>
        <tbody></tbody>
      </table></div>
    </div>

    <div class="card" style="padding:10px 12px">
      <div class="viz-stat-label" style="margin-bottom:4px">口径附录</div>
      <div class="ap">
        模板 <span class="num">benchmarks/PdfBench/spec/challenge.html</span>（3 页 A4，Release 构建，.NET 10.0.10 / Win x64 / 16 核，单进程）。
        <span class="num">priv</span>=Process.PrivateMemorySize64（已提交私有内存，含 Skia/DWrite 原生分配，不受 OS 工作集 trim）；
        <span class="num">ws</span>=WorkingSet64；<span class="num">heap</span>=GC.GetTotalMemory(false)；
        每个采样点同时刷新 GC 累计分配与 gen0/1/2 计数。
        跑 5 份 warmup 不计入；串行每 2–25 份采一点，并发每 1–2s 采一点；「Full GC 后」= 3×Collect+WaitForPendingFinalizers 再 Collect，然后等 300ms。
        「每份不同」= 页脚注入按序号确定的文字（不改变页数与版面），<span class="num">pool</span> = 这批文字可选的汉字种数；产物留在内存不落盘。
        边际 MB/千份 = 稳态段 priv 对文档数的最小二乘斜率 ×1000，样本短的运行噪声大（±500 量级），以 600/2000 份两轮为准；<span class="num">†</span> = 该轮不足 600 份，斜率被前段爬升主导。
      </div>
    </div>
  </div>
  <script>
  (function () {
    var root = document.getElementById('mem-dashboard');
    var ROWS = __TABLE__;
    var CUR = __CURVES__;
    var COLORS = ['--viz-series-1','--viz-series-2','--viz-series-3','--viz-series-4','--viz-series-5','--viz-series-6'];
    var on = {}; CUR.order.forEach(function (k, i) { on[k] = (k !== 'P_server_par8'); });

    var box = root.querySelector('#md-series');
    CUR.order.forEach(function (k, i) {
      var l = document.createElement('label');
      var c = getComputedStyle(root).getPropertyValue(COLORS[i]).trim() || '#888';
      l.innerHTML = '<span class="sw" style="background:' + c + '"></span>';
      var cb = document.createElement('input'); cb.type = 'checkbox'; cb.checked = on[k];
      cb.addEventListener('change', function () { on[k] = cb.checked; draw(); });
      l.appendChild(cb); l.appendChild(document.createTextNode(CUR.label[k]));
      box.appendChild(l);
    });

    var svg = root.querySelector('#md-svg'), tt = root.querySelector('#md-tt');
    var W = 760, H = 320, P = { l: 46, r: 12, t: 12, b: 26 };
    var N = { docs: 0, priv: 1, ws: 2, heap: 3, sub: 4, fb: 5, alloc: 6 };

    function draw() {
      var mi = +root.querySelector('#md-metric').value, pct = root.querySelector('#md-x').value === 'pct';
      var xs = {}, ys = [], ymax = 0, xmax = 0;
      CUR.order.forEach(function (k) {
        if (!on[k]) return;
        var tot = CUR.data[k][CUR.data[k].length - 1][0];
        xs[k] = CUR.data[k].map(function (r) { return pct ? Math.round(r[0] / tot * 100) : r[0]; });
        CUR.data[k].forEach(function (r) { ymax = Math.max(ymax, r[mi]); xmax = Math.max(xmax, pct ? 100 : r[0]); });
      });
      ymax = ymax * 1.08 || 1; xmax = xmax || 1;
      var sx = function (v) { return P.l + (P_W()) * v / xmax; };
      var sy = function (v) { return H - P.b - (P_H()) * v / ymax; };
      function P_W() { return W - P.l - P.r; }
      function P_H() { return H - P.t - P.b; }
      var g = ['<line class="grid" x1="' + P.l + '" y1="' + (H - P.b) + '" x2="' + (W - P.r) + '" y2="' + (H - P.b) + '"/>'];
      for (var t = 0; t <= 4; t++) {
        var yv = ymax * t / 4, yy = sy(yv);
        g.push('<line class="grid" x1="' + P.l + '" y1="' + yy + '" x2="' + (W - P.r) + '" y2="' + yy + '" opacity="' + (t ? 1 : 0) + '"/>');
        g.push('<text class="ax" x="' + (P.l - 6) + '" y="' + (yy + 4) + '" text-anchor="end">' + Math.round(yv) + '</text>');
        var xv = xmax * t / 4;
        g.push('<text class="ax" x="' + sx(xv) + '" y="' + (H - P.b + 16) + '" text-anchor="' + (t === 4 ? 'end' : 'middle') + '">' + Math.round(xv) + (pct ? '%' : '') + '</text>');
      }
      CUR.order.forEach(function (k, i) {
        if (!on[k]) return;
        var pts = CUR.data[k].map(function (r, j) { return sx(xs[k][j]) + ',' + sy(r[mi]); }).join(' ');
        g.push('<polyline fill="none" stroke="var(' + COLORS[i] + ')" stroke-width="1.8" points="' + pts + '"/>');
      });
      g.push('<text class="ax" x="' + P.l + '" y="' + (H - 4) + '" text-anchor="start">横轴：' + (pct ? '批量进度 %' : '文档数（份）') + '　纵轴：' + (mi >= 5 ? '缓存条目数（条）' : 'MB') + '</text>');
      svg.innerHTML = g.join('');
      svg._xs = xs; svg._mi = mi; svg._pct = pct; svg._xmax = xmax;
    }

    svg.addEventListener('pointermove', function (e) {
      var r = svg.getBoundingClientRect(), f = (e.clientX - r.left) / r.width * W;
      var xs = svg._xs || {}; var key = Object.keys(xs); if (!key.length) return;
      var at = (f - P.l) / (W - P.l - P.r) * (svg._pct ? 100 : (svg._xmax || 1));
      if (at < 0) return;
      var unit = svg._mi >= 5 ? ' 条' : ' MB';
      var lines = ['第 ' + Math.round(at) + (svg._pct ? '% 进度' : ' 份')];
      key.forEach(function (k) {
        var best = 0, bd = 1e9;
        xs[k].forEach(function (x, i) { var d = Math.abs(x - at); if (d < bd) { bd = d; best = i; } });
        var over = at > xs[k][xs[k].length - 1] * 1.02;
        lines.push(CUR.label[k] + ' ' + CUR.data[k][best][svg._mi] + unit + (over ? ' ＊末点' : ''));
      });
      tt.textContent = lines.join('\\n'); tt.style.whiteSpace = 'pre'; tt.style.display = 'block';
      tt.style.left = Math.min(r.width - 210, Math.max(0, e.clientX - r.left + 12)) + 'px';
      tt.style.top = '8px';
    });
    svg.addEventListener('pointerleave', function () { tt.style.display = 'none'; });
    root.querySelector('#md-metric').addEventListener('change', draw);
    root.querySelector('#md-x').addEventListener('change', draw);

    var tb = root.querySelector('#md-table tbody');
    ROWS.forEach(function (r) {
      var tr = document.createElement('tr');
      function cell(v, cls) { return '<td class="' + (cls || 'num') + '">' + (v === null || v === undefined ? '—' : v) + '</td>'; }
      tr.innerHTML = '<td title="' + (r.note || '') + '">' + r.label + '</td>' + [r.docs, r.p50, r.p95, r.peak, r.peakws, r.aftergc,
        r.marg + (r.docs < 600 ? ' †' : ''), r.msd].map(function (v) { return cell(v); }).join('');
      tb.appendChild(tr);
    });
    draw();
  })();
  </script>
</section>
"""
html = html.replace('__TABLE__', table).replace('__CURVES__', curves)
target = r"C:\Users\Sprin\.qoder-cn\app\chat-session-visualizations\5a0d46bd-6e17-476e-a780-1bcdc4e02c0b\visualizations"
os.makedirs(target, exist_ok=True)
path = os.path.join(target, 'batch-pdf-memory-curves.html')
io.open(path, 'w', encoding='utf-8').write(html)
print(len(html), path)
