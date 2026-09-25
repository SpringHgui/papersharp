const fs=require('fs');
const j=JSON.parse(fs.readFileSync('src/PaperSharpSamples/bin/Release/net10.0/output/boxes-_probe-tblshrink.json','utf8'));
const e=j.boxes.filter(b=>b.tag==='td').sort((a,b)=>a.y-b.y||a.x-b.x).map(b=>+b.w.toFixed(2));
const tgt=[[524.80,246.25,27.95],[353.95,178.84,26.20],[254.30,139.53,25.17]];
['799','559','419'].forEach((n,r)=>{
  const eng=e.slice(r*3,r*3+3), t=tgt[r];
  console.log('表宽 '+n+'   引擎 '+eng.map(x=>x.toFixed(2).padStart(7)).join(' ')+'   Chrome '+t.map(x=>x.toFixed(2).padStart(7)).join(' ')+'   Δ '+t.map((x,k)=>((x-eng[k])>=0?'+':'')+(x-eng[k]).toFixed(2).padStart(6)).join(' '));
});
