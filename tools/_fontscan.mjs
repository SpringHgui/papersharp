// 临时：扫 C:\Windows\Fonts，按「round(asc*s)+round(desc*s) 指纹」找出浏览器 sans-serif/serif/monospace 用的到底是哪张 face。
// 用法：node tools/_fontscan.mjs
import { readdirSync, readFileSync } from "node:fs";

const DIR = "C:/Windows/Fonts";
const SIZES = [10, 12, 15, 16, 20, 22, 24, 26, 32, 40];
const TARGETS = {
  "sans-serif": [15, 17, 21, 24, 29, 32, 35, 37, 46, 58],
  "serif": [15, 17, 21, 23, 29, 31, 35, 37, 46, 57],
  "monospace": [10, 12, 15, 16, 20, 22, 24, 26, 33, 40],
  "Microsoft YaHei": [14, 16, 20, 21, 26, 29, 31, 35, 42, 52],
  "Arial": [11, 14, 17, 17, 22, 25, 27, 30, 36, 44],
};

function tablesOf(buf, offset) {
  const n = buf.readUInt16BE(offset + 4), t = {};
  for (let i = 0; i < n; i++) {
    const rec = offset + 12 + i * 16;
    t[buf.toString("ascii", rec, rec + 4)] = buf.readUInt32BE(rec + 8);
  }
  return t;
}
function read(buf, t) {
  const u16 = (o) => buf.readUInt16BE(o), i16 = (o) => buf.readInt16BE(o);
  if (!t.head || !t.hhea) return null;
  const upm = u16(t.head + 18);
  const out = { upm, a: i16(t.hhea + 4) / upm, d: -i16(t.hhea + 6) / upm, gap: i16(t.hhea + 8) / upm };
  if (t["OS/2"] != null) {
    out.wa = u16(t["OS/2"] + 74) / upm; out.wd = u16(t["OS/2"] + 76) / upm;
    out.ta = i16(t["OS/2"] + 68) / upm; out.td = -i16(t["OS/2"] + 70) / upm;
  }
  if (t.name != null) {
    const cnt = u16(t.name + 2);
    for (let i = 0; i < cnt; i++) {
      const r = t.name + 6 + i * 12, id = u16(r + 6), plat = u16(r);
      if (id !== 1) continue;
      const off = t.name + buf.readUInt32BE(r + 8), len = buf.readUInt16BE(r + 10);
      out.family = plat === 3
        ? buf.subarray(off, off + len).swap16().toString("utf16le")
        : buf.toString("latin1", off, off + len);
    }
  }
  return out;
}
const r0 = (x) => Math.round(x);   // 半进位向上：与浏览器实测一致即可，稍后用指纹反证

const seen = new Map();
for (const f of readdirSync(DIR)) {
  if (!/\.(ttf|ttc|otf)$/i.test(f)) continue;
  let buf;
  try { buf = readFileSync(DIR + "/" + f); } catch { continue; }
  const tag = buf.toString("ascii", 0, 4);
  const faces = tag === "ttcf" ? Array.from({ length: buf.readUInt32BE(8) }, (_, i) => buf.readUInt32BE(12 + i * 4)) : [0];
  for (const off of faces) {
    try {
      const m = read(buf, tablesOf(buf, off));
      if (!m || !(m.a > 0 && m.d > 0)) continue;
      const sig = SIZES.map((s) => r0(m.a * s) + r0(m.d * s));
      for (const [name, t] of Object.entries(TARGETS)) {
        if (sig.join() === t.join()) console.log(`MATCH ${name.padEnd(16)} ${f.padEnd(22)} ${JSON.stringify(m.family)} a=${m.a.toFixed(4)} d=${m.d.toFixed(4)} gap=${m.gap.toFixed(4)} wa=${m.wa?.toFixed(4)} wd=${m.wd?.toFixed(4)}`);
      }
      if (m.family) seen.set(m.family, f);
    } catch { /* 忽略读不动的表 */ }
  }
}
