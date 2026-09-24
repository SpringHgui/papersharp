// 临时：手读 TTF/TTC 的 hhea / OS2 / head 表，打印 per-em 纵向度量（配合浏览器侧真值反推取整规则）。
// 用法：node tools/_metrics.mjs <font-file> [...]
import { readFileSync } from "node:fs";

function parse(buf, offset) {
  const u16 = (o) => buf.readUInt16BE(o), i16 = (o) => buf.readInt16BE(o);
  const numTables = u16(offset + 4);
  const tables = {};
  for (let i = 0; i < numTables; i++) {
    const rec = offset + 12 + i * 16;
    tables[buf.toString("ascii", rec, rec + 4)] = buf.readUInt32BE(rec + 8);
  }
  const head = tables.head, hhea = tables.hhea, os2 = tables["OS/2"];
  if (!head || !hhea) return null;
  const upm = u16(head + 18);
  const out = {
    unitsPerEm: upm,
    hheaAsc: i16(hhea + 4) / upm, hheaDesc: i16(hhea + 6) / upm, hheaGap: i16(hhea + 8) / upm,
  };
  if (os2 != null) {
    const ver = u16(os2);
    out.os2Ver = ver;
    out.weight = u16(os2 + 4);
    out.winAsc = u16(os2 + 74) / upm; out.winDesc = u16(os2 + 76) / upm;
    out.typoAsc = i16(os2 + 68) / upm; out.typoDesc = i16(os2 + 70) / upm; out.typoGap = i16(os2 + 72) / upm;
    if (ver >= 2) { out.sxHeight = i16(os2 + 82) / upm; out.capHeight = i16(os2 + 84) / upm; }
  }
  out.os2Bits = os2 != null ? u16(os2) : 0;
  const name = tables.name;
  if (name != null) {    const cnt = u16(name + 2);
    for (let i = 0; i < cnt; i++) {
      const r = name + 6 + i * 12;
      const id = u16(r + 6);
      if (id !== 1 && id !== 4 && id !== 16) continue;
      const platform = u16(r);
      let s = buf.toString(platform === 3 ? "utf16le" : "ascii", name + buf.readUInt32BE(r + 8), name + buf.readUInt32BE(r + 8) + buf.readUInt16BE(r + 10));
      if (platform === 3) s = Buffer.from(s, "latin1").swap16().toString("utf16le");
      out["name" + id] = out["name" + id] || s;
    }
  }
  return out;
}

for (const f of process.argv.slice(2)) {
  const buf = readFileSync(f);
  const tag = buf.toString("ascii", 0, 4);
  const first = tag === "ttcf" ? buf.readUInt32BE(12) : 0;
  const r = parse(buf, first);
  console.log(f.split(/[\\/]/).pop(), JSON.stringify(r));
}
