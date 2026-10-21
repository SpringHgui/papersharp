#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
构建期字体子集化工具。

裁剪三族系统字体到 assets/fonts/，供 PaperSharp 运行时 SKTypeface.FromFile 加载：
  * NotoSansSCSub-{Regular,Bold}.ttf ← Noto Sans SC 可变字体实例化 wght=400/700
  * YaheiSub-{Regular,Bold}.ttf      ← 微软雅黑 msyh.ttc
  * NSimSunSub-Regular.ttf           ← 中易宋体 simsun.ttc（只裁 ASCII，等宽正文用）

为什么打包**两族无衬线**：参考浏览器（本机 headless Edge）按 CSS family 关键字分流——
`"Microsoft YaHei"` 命中本机真装的雅黑（normal 行盒 16px→21、22px→29），generic `sans-serif`
命中 Noto Sans SC（16px→24、22px→32）。用一族替另一族会让每个文本盒恒差 1~3px 并按行往下
累积（inline/table/m3 声明 sans-serif，m1/m2/long/invoice/report/pricing-* 声明雅黑）。

为什么要第三族等宽：Blink 的 generic `monospace` 解析到 NSimSun（ascent+descent 恰为 1.0em、
Latin 字宽 0.5em），而 Consolas 是 1.17em/0.55em —— `<code>` 每行因此高 2px、宽多 3~11px。

为什么还要烘焙 `PRKT` 字距表：浏览器排版会按字体的字距表调整相邻字形前进量，Skia 的 MeasureText
只加 hmtx、完全不做字距。实测参考浏览器里雅黑 `TAVoWyA!`@40 = 206.074 − 8.418 = 197.656（正好等于
hmtx + legacy `kern`），Noto Sans SC 同理（它的字距在 GPOS）。缺这块每行窄 0.2~0.5px，窄盒的临界
折行会多算一行。子集化默认会丢掉 legacy `kern` 表、也不会把 GPOS pair-pos 转成任何统一形态，
所以这里把两种来源合并成一张自定义表 `PRKT`（码位对 → int16 font units）写进子集：
Skia 读未知表只是忽略，PDF 嵌字体时也只是丢掉，引擎侧 KernTable 只解析这一种极简格式。
只保留双码位 < 0x2E80 的对（拉丁/希腊/西里尔/标点）——CJK 是全宽字身，字距为 0 或交给 palt。

为什么在构建期做：SkiaSharp 2.88 的 PDF backend 对系统 CJK 字体不做子集化，会整份嵌入
（msyh.ttc / simsun.ttc / NotoSansSC-VF.ttf 各 17~21MB → PDF 20+MB）。

用法（在 papersharp 目录下）：
    python src/PaperSharpSamples/tools/subset-font.py        # 全量裁剪 + 烘焙字距表
    python src/PaperSharpSamples/tools/subset-font.py bake    # 只对现有子集重跑字距表烘焙

依赖：pip install fonttools
源字体路径可用环境变量 NOTO_SRC / YAHEI_SRC / SIMSUN_SRC 覆盖。
Noto 是 OFL 授权，子集必须随带许可证：脚本会把名字表 ID 13 的全文抽成 assets/fonts/OFL.txt。
"""
import os
import struct
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
OUT_DIR = os.path.join(ROOT, "assets", "fonts")
CHARSET_TXT = os.path.join(OUT_DIR, "_charset.txt")
ASCII_TXT = os.path.join(OUT_DIR, "_ascii.txt")

# Windows 系统字体源（子集化只在开发机做，产物跨平台通用）
SRC_VARIABLE = os.environ.get("NOTO_SRC", r"C:\Windows\Fonts\NotoSansSC-VF.ttf")
SRC_YAHEI = os.environ.get("YAHEI_SRC", r"C:\Windows\Fonts\msyh.ttc")
SRC_YAHEI_BOLD = os.environ.get("YAHEI_BOLD_SRC", r"C:\Windows\Fonts\msyhbd.ttc")
SRC_SIMSUN = os.environ.get("SIMSUN_SRC", r"C:\Windows\Fonts\simsun.ttc")

# (输出后缀, 可变轴实例值)
TARGETS = [("Regular", "400"), ("Bold", "700")]


def build_charset() -> str:
    """构造覆盖 ASCII + Latin-1 + GB2312 全部汉字/符号 + CJK 标点 + 全角的字符集。"""
    chars = set()

    # 可打印 ASCII
    for i in range(0x20, 0x7F):
        chars.add(chr(i))
    # Latin-1 Supplement + Latin Extended-A（¥ × é ü ß 等）
    for i in range(0xA0, 0x0180):
        chars.add(chr(i))
    # CJK 标点、假名区留白、全角
    for i in range(0x3000, 0x3040):
        chars.add(chr(i))
    for i in range(0xFF00, 0xFFF0):
        chars.add(chr(i))
    # 常用符号
    for i in [0x2013, 0x2014, 0x2018, 0x2019, 0x201C, 0x201D, 0x2022, 0x2026, 0x3001, 0x3002]:
        chars.add(chr(i))
    # GB2312 全部两字节码位（符号区 + 一级/二级汉字）
    for hi in range(0xA1, 0xF8):
        for lo in range(0xA1, 0xFF):
            try:
                chars.add(bytes([hi, lo]).decode("gb2312"))
            except (UnicodeDecodeError, ValueError):
                pass

    return "".join(sorted(chars))


def run(mod: str, args: list) -> None:
    subprocess.run([sys.executable, "-m", mod] + args, check=True)


# ── 字距表烘焙（PRKT）────────────────────────────────────────────────
# 只保留双码位都在拉丁/希腊/西里尔/通用标点区间的对：CJK 字形是全宽字身，字距为 0（挤紧由 palt 管）。
KERN_LIMIT = 0x2E80


def _xa(value_record) -> int:
    """GPOS ValueRecord 的 XAdvance（可变字体实例化后已解析成标量，理论上不再是 delta 列表）。"""
    if value_record is None:
        return 0
    v = getattr(value_record, "XAdvance", 0)
    if isinstance(v, list):
        v = v[0] if v else 0
    try:
        return int(v)
    except (TypeError, ValueError):
        return 0


def _class_map(classdef, nm) -> dict:
    """ClassDef → {字形名: class 序号}。fontTools 新旧版本 classDefs 的键可能是 glyph id 或字形名。"""
    m = {}
    for k, v in (getattr(classdef, "classDefs", None) or {}).items():
        m[nm(k)] = v
    return m


def _gpos_pairs(font, names: set) -> dict:
    """GPOS 'kern'/'dist' 的 pair positioning，键为 (字形名, 字形名)。含 format 1/2 两种子表。"""
    out = {}
    gpos = font.get("GPOS")
    if gpos is None or gpos.table.FeatureList is None:
        return out
    tab = gpos.table
    lookups = set()
    for fr in tab.FeatureList.FeatureRecord:
        if fr.FeatureTag in ("kern", "dist"):
            lookups.update(fr.Feature.LookupListIndex)
    order = font.getGlyphOrder()

    def nm(x):
        # fontTools 新版本有些地方直接给字形名，老接口给 glyph id
        return x if isinstance(x, str) else order[x]

    for i in sorted(lookups):
        lk = tab.LookupList.Lookup[i]
        if lk.LookupType != 2:
            continue
        for st in lk.SubTable:
            if st.Format == 1:
                for first, pset in zip(st.Coverage.glyphs, st.PairSet):
                    fn = nm(first)
                    if fn not in names:
                        continue
                    for rec in pset.PairValueRecord:
                        sn = nm(rec.SecondGlyph)
                        if sn not in names:
                            continue
                        v = _xa(rec.Value1)
                        if v:
                            out[(fn, sn)] = out.get((fn, sn), 0) + v
            else:
                # 类别式：先按类聚合我们要的字形，再只对非零单元做笛卡尔展开
                d1 = _class_map(st.ClassDef1, nm)
                d2 = _class_map(st.ClassDef2, nm)
                by1, by2 = {}, {}
                for gname in names:
                    by1.setdefault(d1.get(gname, 0), []).append(gname)
                    by2.setdefault(d2.get(gname, 0), []).append(gname)
                for k1, gs1 in by1.items():
                    if k1 >= st.Class1Count:
                        continue
                    row = st.Class1Record[k1].Class2Record
                    for k2, gs2 in by2.items():
                        if k2 >= st.Class2Count:
                            continue
                        v = _xa(row[k2].Value1)
                        if not v:
                            continue
                        for a in gs1:
                            for b in gs2:
                                out[(a, b)] = out.get((a, b), 0) + v
    return out


def _legacy_pairs(font, names: set) -> dict:
    """TrueType legacy `kern` 表（fontTools 反编译后键已是字形名）。"""
    out = {}
    if "kern" not in font:
        return out
    for kt in font["kern"].kernTables:
        for (a, b), v in kt.kernTable.items():
            if a in names and b in names and v:
                out[(a, b)] = out.get((a, b), 0) + v
    return out


def bake_kern(path: str, src: str | None = None, src_index: int | None = None) -> None:
    """把该子集能用到的字距对写成自定义表 `PRKT`（大端：u16 版本 / u16 unitsPerEm / u32 对数 / 对数×(u16,u16,i16)）。

    GPOS 从子集自己读（Noto 的字距在这里，且实例化后 delta 已解析成定值）；legacy `kern` 只能从源字体读
    （子集化默认丢掉它，雅黑的字距全在这一张表里）。两条通道不重叠，直接相加。
    """
    from fontTools.ttLib import TTFont, newTable

    sub = TTFont(path)
    cmap = sub.getBestCmap()                      # cp -> glyph name
    name2cp = {}
    for cp in sorted(cmap):
        name2cp.setdefault(cmap[cp], cp)          # 一名多码位取最小者，保证可复现
    names = set(name2cp)
    pairs = _gpos_pairs(sub, names)
    if src and os.path.exists(src):
        f = TTFont(src, fontNumber=src_index) if src_index is not None else TTFont(src)
        for k, v in _legacy_pairs(f, names).items():
            if k not in pairs:
                pairs[k] = v

    rows = []
    for (a, b), v in pairs.items():
        c1, c2 = name2cp[a], name2cp[b]
        if c1 >= KERN_LIMIT or c2 >= KERN_LIMIT:
            continue
        v = int(v)
        if v == 0 or not -32768 <= v <= 32767:
            continue
        rows.append((c1, c2, v))
    rows.sort()

    upm = sub["head"].unitsPerEm
    body = struct.pack(">HHI", 1, upm, len(rows))
    body += b"".join(struct.pack(">HHh", *r) for r in rows)
    tbl = newTable("PRKT")
    tbl.data = body
    sub["PRKT"] = tbl
    sub.save(path)
    print(f"         kern pairs={len(rows)} (+{len(body) / 1024:.0f} KB)")


KERN_JOBS = [
    ("NotoSansSCSub-Regular.ttf", None, None),
    ("NotoSansSCSub-Bold.ttf", None, None),
    ("YaheiSub-Regular.ttf", SRC_YAHEI, 0),
    ("YaheiSub-Bold.ttf", SRC_YAHEI_BOLD, 0),
    ("NSimSunSub-Regular.ttf", SRC_SIMSUN, 1),
]


LIB_FONTS_DIR = os.path.join(os.path.dirname(ROOT), "PaperSharp", "Fonts")
# 只同步**可随包再分发**的那两份：Noto Sans SC 的子集（SIL OFL，OFL.txt 随包）。
# YaheiSub-*/NSimSunSub-* 是微软雅黑与中易宋体的派生二进制，许可不允许随 MIT 包分发，
# 因此既不进 dll 也不进仓库的 Fonts 目录——消费方需要雅黑度量一致时，用
# FontManager.BundledFontDirectory 指向自己的字体目录。仓内样例/金标仍从
# samples/assets/fonts（老路径）读到它们，这条不影响对拍。
LIB_FONTS = ("NotoSansSCSub-Regular.ttf", "NotoSansSCSub-Bold.ttf", "OFL.txt")


def sync_to_library() -> None:
    """把烤好的子集同步进库工程（csproj 以 EmbeddedResource 随 dll 打包）。

    必须有的原因：字体嵌进 dll 之后，**运行时用的是库里的副本**，而本脚本产出的是
    samples/assets/fonts 那一份。不同步就会出现「被金标验证的字体 ≠ 出厂字体」——
    而且漂移是静默的（两边都是合法字体，只是 PRKT 与字形集不同）。
    BundledFontTests.Embedded_copy_matches_source_of_truth 会在两边不一致时直接红。
    """
    if not os.path.isdir(LIB_FONTS_DIR):
        print(f"[warn] 库字体目录不存在，跳过同步: {LIB_FONTS_DIR}", file=sys.stderr)
        return
    for name in LIB_FONTS:
        src = os.path.join(OUT_DIR, name)
        if not os.path.exists(src):
            print(f"[warn] 未产出 {name}，库内副本未更新", file=sys.stderr)
            continue
        dst = os.path.join(LIB_FONTS_DIR, name)
        same = os.path.exists(dst) and open(src, "rb").read() == open(dst, "rb").read()
        if not same:
            with open(src, "rb") as f, open(dst, "wb") as g:
                g.write(f.read())
        print(f"[lib]  {name} -> {'未变' if same else '已同步'} {os.path.getsize(dst):,} B")


def bake_all() -> None:
    """只重跑烘焙（子集已在位）：用于调 PRKT 格式时不重新裁剪一遍。"""
    for fn, src, idx in KERN_JOBS:
        path = os.path.join(OUT_DIR, fn)
        if not os.path.exists(path):
            print(f"[skip] 子集不存在: {fn}", file=sys.stderr)
            continue
        print(f"[bake] {fn}")
        bake_kern(path, src, idx)
    sync_to_library()


SUBSET_FLAGS = [
    # 精简：去掉 hinting/子程序化，CJK 排版用不到 OpenType 高级特性
    #（带 hinting 会让字距按指令取整，浏览器那边是不打折量拿浮动 advance）。
    "--no-hinting",
    "--desubroutinize",
    "--layout-features=kern,liga",
    "--name-IDs=0,1,2,3,4,5,6",
    "--notdef-outline",
]


def subset(src: str, dst: str, text_file: str, font_number: int | None = None) -> None:
    """从一份（可能是 ttc 的）系统字体里按码位裁剪。"""
    if not os.path.exists(src):
        print(f"[skip] 源字体不存在: {src}", file=sys.stderr)
        return
    args = [src]
    if font_number is not None:
        args.append(f"--font-number={font_number}")
    args += [f"--text-file={text_file}"] + SUBSET_FLAGS + [f"--output-file={dst}"]
    run("fontTools.subset", args)
    bake_kern(dst, src, font_number)
    before, after = os.path.getsize(src), os.path.getsize(dst)
    print(f"[subset] {os.path.basename(src)}#{font_number or 0} → {os.path.basename(dst)}  "
          f"{before/1024/1024:.1f} MB -> {after/1024:.0f} KB")
    print_metrics(dst)


def instance_and_subset(wght: str, dst: str) -> None:
    if not os.path.exists(SRC_VARIABLE):
        print(f"[skip] 源字体不存在: {SRC_VARIABLE}（可用 NOTO_SRC 指定）", file=sys.stderr)
        return
    tmp = os.path.join(OUT_DIR, "_noto-full.tmp.ttf")
    # 1) 可变 → 静态实例（只钉字重轴，其余轴打平到默认值）
    run("fontTools.varLib.instancer", [SRC_VARIABLE, f"wght={wght}", "-o", tmp])
    # 2) 按语料码位裁剪
    subset(tmp, dst, CHARSET_TXT)
    os.remove(tmp)


def print_metrics(path: str) -> None:
    """自检：纵向度量必须是浏览器实测的那组（win 1.1600/0.2880，行距 normal 1.448）。"""
    from fontTools.ttLib import TTFont

    f = TTFont(path, lazy=True)
    upm = f["head"].unitsPerEm
    os2 = f["OS/2"]
    print(f"         upm={upm} win={os2.usWinAscent / upm:.4f}/{os2.usWinDescent / upm:.4f} "
          f"glyphs={len(f.getGlyphOrder())}")


def write_license() -> None:
    """OFL 要求字体副本随带许可证；可变字体名字表 ID 13 就带全文，直接抽出来。"""
    lic = os.path.join(OUT_DIR, "OFL.txt")
    if os.path.exists(lic) or not os.path.exists(SRC_VARIABLE):
        return
    from fontTools.ttLib import TTFont

    name = TTFont(SRC_VARIABLE, lazy=True)["name"]
    text = name.getName(13, 3, 1, 0x409) or name.getName(13, 1, 0, 0)
    if text:
        with open(lic, "w", encoding="utf-8") as fh:
            fh.write(str(text) + "\n")
        print(f"[license] {lic}")


def main() -> None:
    if len(sys.argv) > 1 and sys.argv[1] == "bake":
        bake_all()
        return
    os.makedirs(OUT_DIR, exist_ok=True)
    charset = build_charset()
    with open(CHARSET_TXT, "w", encoding="utf-8") as f:
        f.write(charset)
    print(f"[charset] {len(charset)} codepoints -> {CHARSET_TXT}")
    # 等宽 face 只需要 ASCII（CJK 在浏览器里也走字形回退，引擎侧由 SubsetIfCovers 承接）
    with open(ASCII_TXT, "w", encoding="utf-8") as f:
        f.write("".join(chr(i) for i in range(0x20, 0x7F)))

    for suffix, wght in TARGETS:
        instance_and_subset(wght, os.path.join(OUT_DIR, f"NotoSansSCSub-{suffix}.ttf"))
    for suffix in ("Regular", "Bold"):
        src = SRC_YAHEI if suffix == "Regular" else SRC_YAHEI_BOLD
        subset(src, os.path.join(OUT_DIR, f"YaheiSub-{suffix}.ttf"), CHARSET_TXT, 0)
    # generic monospace：Blink 在本机解析到 NSimSun = simsun.ttc 的第 1 号字面
    subset(SRC_SIMSUN, os.path.join(OUT_DIR, "NSimSunSub-Regular.ttf"), ASCII_TXT, 1)
    write_license()
    sync_to_library()

    print("\n完成。运行时 FontManager 会按 CSS family 关键字分派到对应的那份子集"
          "（dll 内嵌副本已同步）。")


if __name__ == "__main__":
    main()
