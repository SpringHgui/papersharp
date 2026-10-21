// PDF 体积解剖：列出每类对象的字节占比（字体流/图像流/内容流/其他）
// 用法: node tools/pdfanatomy.js file.pdf [...]
const fs = require('fs');
for (const f of process.argv.slice(2)) {
  const buf = fs.readFileSync(f);
  console.log(`\n=== ${f}  (${(buf.length/1024).toFixed(0)} KB) ===`);
  // 找所有 "N 0 obj" 起始（近似：从文件头扫）
  const s = buf.toString('latin1');
  const objs = [];
  const re = /(\d+) 0 obj\b/g;
  let m;
  while ((m = re.exec(s))) {
    const start = m.index;
    const end = s.indexOf('endobj', start);
    if (end < 0) break;
    const head = s.slice(start, Math.min(start + 400, end));
    // 流长度
    let streamLen = 0, streamStart = -1;
    const sm = /stream\r?\n?/.exec(s.slice(start, end));
    if (sm) {
      streamStart = start + sm.index + sm[0].length;
      const em = s.indexOf('endstream', streamStart);
      if (em > 0) streamLen = em - streamStart;
    }
    objs.push({ id: +m[1], head, len: end - start + 6, streamLen, dictLen: (end-start) - streamLen });
  }
  const cat = (o) => {
    const h = o.head;
    if (/\/FontFile2|\/FontFile3/.test(h)) return 'font-program';
    if (/\/Font\b|\/Type\s*\/Font/.test(h) && !o.streamLen) return 'font-desc';
    if (/\/Subtype\s*\/Image/.test(h)) return 'image';
    if (/\/Filter\s*\/FlateDecode/.test(h) && o.streamLen && !/\/Subtype\s*\/Image/.test(h)) return 'content-stream';
    if (o.streamLen) return 'other-stream';
    if (/\/Object\.?Stream|\/ObjStm/.test(h)) return 'objstm';
    return 'dict';
  };
  const by = {};
  for (const o of objs) {
    const k = cat(o);
    (by[k] ??= { n: 0, bytes: 0, streams: 0, items: [] }).n++;
    by[k].bytes += o.len;
    by[k].streams += o.streamLen;
    if (o.streamLen > 20000 || k === 'font-program' || k === 'image') by[k].items.push(o);
  }
  for (const [k, v] of Object.entries(by).sort((a,b)=>b[1].bytes-a[1].bytes)) {
    console.log(`${k.padEnd(16)} n=${String(v.n).padStart(4)} bytes=${(v.bytes/1024).toFixed(0).padStart(8)} KB  (stream ${(v.streams/1024).toFixed(0)} KB)`);
  }
  for (const k of ['font-program','image']) {
    for (const o of (by[k]?.items ?? [])) {
      const name = (/\/BaseFont\s*([A-Za-z0-9+\-_]+)/.exec(o.head)?.[1] ?? /\/Name\s*\/([A-Za-z0-9+\-_]+)/.exec(o.head)?.[1] ?? '').padEnd(24);
      const filt = (/\/Filter\s*\/(\w+)/.exec(o.head)?.[1] ?? 'none');
      const sub = (/\/Subtype\s*\/(\w+)/.exec(o.head)?.[1] ?? '');
      console.log(`  ${k} obj#${o.id} ${(o.streamLen/1024).toFixed(0)} KB filt=${filt} ${sub} ${name}`);
    }
  }
  // 字体名统计
  const faces = [...s.matchAll(/\/BaseFont\s*\/([A-Za-z0-9+\-_]+)/g)].map(x=>x[1]);
  const fam = {};
  for (const b of faces) { const key = b.split('+').pop().replace(/-\w+$/,''); fam[b] = (fam[b]||0)+1; }
  console.log('BaseFonts:', JSON.stringify(fam));
  console.log('xref streams:', (s.match(/\/Type\s*\/ObjStm/g)||[]).length, ' classic xref tables:', (s.match(/^xref$/gm)||[]).length);
}
