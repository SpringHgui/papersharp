import io, json, os, re

OUT = os.path.join(os.path.dirname(__file__), 'out')
SCEN = [
    ('L_dense_same',    '内容相同 · 串行 120 份'),
    ('K_dense_vary',    '每份不同 · 串行 120 份'),
    ('I_p2000',         '每份不同(2000 汉字) · 串行 200 份'),
    ('E_vary600_p400',  '每份不同(400 汉字) · 串行 600 份'),
    ('F_vary600_p3000', '每份不同(3000 汉字) · 串行 600 份'),
    ('H_p50',           '每份不同(50 汉字) · 串行 200 份'),
    ('G_par400d4',      '每份不同(400 汉字) · 4 路并发 400 份'),
    ('M_serial2000',    '每份不同(800 汉字) · 串行 2000 份'),
    ('N_par1000d8',     '每份不同(800 汉字) · 8 路并发 1000 份'),
    ('P_server_par8',   'Server GC · 8 路并发 400 份'),
    ('O_server_serial', 'Server GC · 串行 400 份'),
]

ROW = re.compile(r"""^(\d+)\s+([\d.]+)\s+([\d.]+)\s+([\d.]+)\s+([\d.]+)"""
                 r"""(?:\s+(\d+)\s*/\s*(\d+))?"""          # fb/lf
                 r"""(?:\s+(\d+))?"""                          # sub
                 r"""\s+(\d+)\s*/\s*(\d+)\s*/\s*(\d+)"""   # gc
                 r"""\s+(\d+)\s+([\d.]+)$""", re.X)


def parse(line):
    m = ROW.match(line.strip())
    if not m:
        return None
    d = m.groups()
    return dict(docs=int(d[0]), ws=float(d[1]), priv=float(d[2]), heap=float(d[3]), alloc=float(d[4]),
                fb=int(d[5]) if d[5] else None, lf=int(d[6]) if d[6] else None, sub=int(d[7]) if d[7] else None,
                thr=int(d[11]), msd=float(d[12]))


def grab(txt, pat, key, meta):
    m = re.search(pat, txt)
    if m:
        meta[key] = m.groups() if m.re.groups > 1 else m.group(1)


data = {}
for key, label in SCEN:
    p = os.path.join(OUT, key + '.log')
    if not os.path.exists(p):
        continue
    txt = io.open(p, encoding='utf-8', errors='replace').read()
    series = [r for r in (parse(l) for l in txt.splitlines()) if r]
    meta = {}
    grab(txt, r'GC=(\w+)', 'gc', meta)
    grab(txt, r'峰值: priv ([\d.]+) MB   ws ([\d.]+) MB   heap ([\d.]+) MB', 'peak', meta)
    grab(txt, r'托管累计分配 (\d+) KB/份', 'allocDoc', meta)
    grab(txt, r'Full GC 后: priv ([\d.]+) MB.*?托管堆滞留 ([\d.]+) MB', 'afterGc', meta)
    grab(txt, r'吞吐 ([\d.]+) docs/s', 'tps', meta)
    if series:
        data[key] = dict(label=label, series=series, meta=meta)

json.dump(data, io.open(os.path.join(OUT, 'data.json'), 'w', encoding='utf-8'), ensure_ascii=False)
for k, v in data.items():
    print(k, len(v['series']), v['meta'].get('peak'), v['meta'].get('afterGc'), v['meta'].get('tps'), v['series'][-1]['priv'])
