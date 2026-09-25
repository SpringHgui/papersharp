const fs = require('fs');
const { execSync } = require('child_process');
const out = {};
for (const s of ['m9', 'm3', 'm4']) {
  const b = fs.readFileSync('src/PaperSharpSamples/bin/Release/net10.0/output/boxes-' + s + '.png');
  out[s] = { pngW: b.readUInt32BE(16), pngH: b.readUInt32BE(20), cssH: b.readUInt32BE(20) / 2 };
  const doc = 'G:/Github/papersharp/src/PaperSharpSamples/bin/Release/net10.0/output/boxes-' + s + '.html';
  let v = execSync('node /c/Users/Sprin/AppData/Local/Temp/ps-snap/cdp-probe.mjs "' + doc + '" --width 1284 --expr "JSON.stringify({sh:document.documentElement.scrollHeight,bh:document.body.scrollHeight})"',
    { encoding: 'utf8', shell: '/bin/bash' }).trim();
  v = JSON.parse(v); if (typeof v === 'string') v = JSON.parse(v);
  out[s].browserScrollH = v.sh; out[s].browserBodyH = v.bh;
  console.log(s.padEnd(4), 'PNG', out[s].pngW + '×' + out[s].pngH,
    '| 引擎CSS高', out[s].cssH.toFixed(2),
    '| 浏览器 scrollHeight', v.sh,
    '| Δ', (v.sh - out[s].cssH).toFixed(2));
}
fs.writeFileSync('.scratch/heights.json', JSON.stringify(out));
