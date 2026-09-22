#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
构建期字体子集化工具。

把系统微软雅黑（Regular + Bold）裁剪成"GB2312 常用汉字 + ASCII + 常见标点"子集，
输出到 assets/fonts/ 供 PaperSharp 运行时通过 SKTypeface.FromData 加载。

为什么在构建期做：SkiaSharp 2.88 的 PDF backend 对系统 CJK 字体不做子集化，
会整份嵌入（msyh.ttc ~20MB → PDF 20+MB）。预裁到常用字后单字体 ~2-3MB。

用法（在 paper-sharp 目录下）：
    python tools/subset-font.py

依赖：pip install fonttools
"""
import os
import sys
import subprocess

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
OUT_DIR = os.path.join(ROOT, "assets", "fonts")
CHARSET_TXT = os.path.join(OUT_DIR, "_charset.txt")

# Windows 系统字体源（子集化只在开发机做，产物跨平台通用）
SRC_REGULAR = r"C:\Windows\Fonts\msyh.ttc"
SRC_BOLD = r"C:\Windows\Fonts\msyhbd.ttc"


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


def subset(src: str, font_number: int, dst: str) -> None:
    if not os.path.exists(src):
        print(f"[skip] 源字体不存在: {src}", file=sys.stderr)
        return
    before = os.path.getsize(src)
    cmd = [
        sys.executable, "-m", "fontTools.subset",
        src,
        f"--font-number={font_number}",
        f"--text-file={CHARSET_TXT}",
        f"--output-file={dst}",
        # 精简：去掉 hinting/子程序化，CJK 排版用不到 OpenType 高级特性
        "--no-hinting",
        "--desubroutinize",
        "--layout-features=kern,liga",
        "--name-IDs=0,1,2,3,4,5,6",
        "--notdef-outline",
    ]
    print(f"[subset] {os.path.basename(src)}#{font_number} -> {os.path.basename(dst)}")
    subprocess.run(cmd, check=True)
    after = os.path.getsize(dst)
    print(f"         {before/1024/1024:.1f} MB -> {after/1024/1024:.2f} MB "
          f"({after/before*100:.1f}%)")


def main() -> None:
    os.makedirs(OUT_DIR, exist_ok=True)
    charset = build_charset()
    with open(CHARSET_TXT, "w", encoding="utf-8") as f:
        f.write(charset)
    print(f"[charset] {len(charset)} codepoints -> {CHARSET_TXT}")

    subset(SRC_REGULAR, 0, os.path.join(OUT_DIR, "YaheiSub-Regular.ttf"))
    subset(SRC_BOLD, 0, os.path.join(OUT_DIR, "YaheiSub-Bold.ttf"))

    print("\n完成。运行时 FontManager 会自动优先加载 assets/fonts/ 下的子集。")


if __name__ == "__main__":
    main()
