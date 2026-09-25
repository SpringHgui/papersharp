#!/usr/bin/env python3
# 临时：把浏览器实测宽度 与 (a) hmtx 裸和 (b) hmtx+kern 对齐，判断每个面是否需要字距表。
import json, sys
from fontTools.ttLib import TTFont

browser = json.load(open("tools/_kern.out.json"))
strings = [("TAVoWyA!", 40), ("calc(20px + 0.5rem)", 40), ("yesterday & today", 40),
           ("TAVoWyA!", 14), ("calc(20px + 0.5rem)", 14)]
faces = [("YaHei", r"C:\Windows\Fonts\msyh.ttc", 0), ("Noto", r"C:\Windows\Fonts\NotoSansSC-VF.ttf", None),
         ("NSimSun", r"C:\Windows\Fonts\simsun.ttc", 1), ("Georgia", r"C:\Windows\Fonts\georgia.ttf", None)]
LIMIT = int(sys.argv[1]) if len(sys.argv) > 1 else 0x2000


def xa(v):
    if v is None:
        return 0
    x = getattr(v, "XAdvance", 0)
    if isinstance(x, list):
        x = x[0] if x else 0
    try:
        return int(x)
    except Exception:
        return 0


def kernmap(f):
    """码位对 -> 字距（font units）。只收双码位 < LIMIT 的对（拉丁/标点），够用且快。"""
    rev = {}
    for cp, gid in f.getBestCmap().items():
        if cp < LIMIT:
            rev.setdefault(gid, cp)
    out = {}
    if "kern" in f:
        for kt in f["kern"].kernTables:
            for (g1, g2), v in kt.kernTable.items():
                c1, c2 = rev.get(g1), rev.get(g2)
                if c1 is not None and c2 is not None:
                    out[(c1, c2)] = out.get((c1, c2), 0) + v
    if "GPOS" in f:
        g = f["GPOS"].table
        lks = set()
        for fr in g.FeatureList.FeatureRecord:
            if fr.FeatureTag in ("kern", "dist"):
                lks.update(fr.Feature.LookupListIndex)
        for i in sorted(lks):
            L = g.LookupList.Lookup[i]
            if L.LookupType != 2:
                continue
            for st in L.SubTable:
                if st.Format == 1:
                    for first, pset in zip(st.Coverage.glyphs, st.PairSet):
                        c1 = rev.get(first)
                        if c1 is None:
                            continue
                        for rec in pset.PairValueRecord:
                            c2 = rev.get(rec.SecondGlyph)
                            v = xa(rec.Value1)
                            if c2 is not None and v:
                                out[(c1, c2)] = out.get((c1, c2), 0) + v
                else:
                    d1 = st.ClassDef1.classDefs
                    d2 = st.ClassDef2.classDefs
                    items = list(rev.items())
                    for gid1, c1 in items:
                        k1 = d1.get(gid1, 0)
                        if k1 >= st.Class1Count:
                            continue
                        row = st.Class1Record[k1].Class2Record
                        for gid2, c2 in items:
                            k2 = d2.get(gid2, 0)
                            if k2 >= st.Class2Count:
                                continue
                            v = xa(row[k2].Value1)
                            if v:
                                out[(c1, c2)] = out.get((c1, c2), 0) + v
    return out


print("face\tstring\tsize\tbrowser\tkernless\t+kern\tb-bare\tb-kerned\tnpairs")
for (fname, path, idx), off in zip(faces, [0, 5, 10, 15]):
    try:
        f = TTFont(path, fontNumber=idx) if idx is not None else TTFont(path)
    except Exception as e:
        print(fname, "skip", e)
        continue
    upm = f["head"].unitsPerEm
    cmap = f.getBestCmap()
    hmtx = f["hmtx"]
    K = kernmap(f)
    for j, (s, size) in enumerate(strings):
        b = browser[off + j]
        bare = sum(hmtx[cmap[ord(c)]][0] for c in s if ord(c) in cmap) * size / upm
        kn = sum(K.get((ord(a), ord(z)), 0) for a, z in zip(s, s[1:])) * size / upm
        print(f"{fname}\t{s[:20]}\t{size}\t{b['w']:.3f}\t{bare:.3f}\t{bare + kn:.3f}\t{b['w'] - bare:+.3f}\t{b['w'] - bare - kn:+.3f}\t{len(K)}\t[{b['face']}]")
