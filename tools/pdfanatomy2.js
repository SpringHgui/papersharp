// PDF 体积解剖 v2：解析对象字典 + /Length，按类型聚合字节
// 用法: node tools/pdfanatomy2.js file.pdf [...]
const fs = require('fs');
for (const f of process.argv.slice(2)) {
  const buf = fs.readFileSync(f);
  const s = buf.toString('latin1');
  console.log(`\n=== ${f}  (${(buf.length/1024).toFixed(0)} KB) ===`);
  const re = /(\d+) 0 obj\s*<</g;
  let m, rows = [], total = 0;
  while ((m = re.exec(s))) {
    // 括号平衡取字典
    let i = m.index + m[0].length - 2, depth = 0, end = -1;
    while (i < s.length) {
      if (s[i] === '<' && s[i+1] === '<') { depth++; i += 2; }
      else if (s[i] === '>' && s[i+1] === '>') { depth--; i += 2; if (depth === 0) { end = i; break; } }
      else i++;
    }
    if (end < 0) break;
    const dict = s.slice(m.index, end);
    const lenM = /\/Length\s+(\d+)/.exec(dict);
    let streamLen = 0;
    if (lenM) { streamLen = +lenM[1]; re.lastIndex = Math.max(re.lastIndex, end); }
    let kind = 'dict';
    if (/\/FontFile2/.test(dict)) kind = 'font-File2(TTF)';
    else if (/\/FontFile3/.test(dict)) kind = 'font-File3(CFF)';
    else if (/\/Subtype\s*\/Image/.test(dict)) kind = 'image';
    else if (/\/Type\s*\/Page\b/.test(dict)) kind = 'content';
    else if (lenM) kind = 'stream-other';
    else if (/\/Type\s*\/Font\b/.test(dict)) kind = 'fontobj';
    if (streamLen > 500 || kind !== 'dict') {
      const name = (/\/BaseFont\s*\/([A-Za-z0-9+\-_]+)/.exec(dict)?.[1] ?? /\/Name\s*\/([A-Za-z0-9+\-_]+)/.exec(dict)?.[1] ?? '')
        .padEnd(22, ' ').slice(0,22);
      const filt = (/\/Filter\s*\/(\w+)/.exec(dict)?.[1] ?? '-');
      rows.push({ id: +m[1], kind, bytes: streamLen || (end - m.index), filt, name });
    }
  }
  const by = {};
  for (const r of rows) (by[r.kind] ??= { n: 0, b: 0 }).n++, by[r.kind].b += r.bytes;
  for (const [k, v] of Object.entries(by).sort((a,b)=>b[1].b-a[1].b))
    console.log(`${k.padEnd(18)} n=${String(v.n).padStart(3)} ${(v.b/1024).toFixed(0).padStart(7)} KB`);
  console.log('--- big streams ---');
  for (const r of rows.filter(r=>r.bytes > 20000).sort((a,b)=>b.bytes-a.bytes))
    console.log(`obj#${String(r.id).padStart(4)} ${r.kind.padEnd(18)} ${(r.bytes/1024).toFixed(0).padStart(7)} KB filt=${r.filt} ${r.name}`);
  // 每个 /Type/Font 的 /W 数组宽度条目数（间接看出用了多少字形）+ DescendantFonts 数
  for (const fm of s.matchAll(/\/BaseFont\s*\/([A-Za-z0-9+\-_]+)[\s\S]{0,600}?\/W\s*\[([\s\S]*?)\]/g)) {
    const w = fm[2].match(/\d+\s+\d+\s*\[[^\]]*\]|\d+\s+\d+/g) || [];
    let glyphs = 0; for (const seg of fm[2].matchAll(/(\d+)\s+(\d+)\s+\[/g)) { } // 粗算
    const ranges = [...fm[2].matchAll(/(\d+)\s+(\d+)\s*\[([^\]]*)\]/g)];
    let cnt = 0; for (const rg of ranges) cnt += (rg[2]-rg[1]+1) + (rg[3].trim().split(/\s+/).length - (rg[2]-rg[1]+1));
    console.log(`font ${fm[1]}  /W covers≈${cnt} glyphs (${ranges.length} ranges)`);
  }
}
