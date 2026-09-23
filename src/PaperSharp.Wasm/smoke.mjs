import { dotnet } from "./_framework/dotnet.js";
import { writeFileSync } from "node:fs";

const inst = await dotnet.create();
const exp = await inst.getAssemblyExports("PaperSharp.Wasm");

console.log("info:", await exp.PaperSharp.Wasm.Api.Info());

const html = `
<div class="card">
  <h1>Hello from PaperSharp</h1>
  <p>HTML + CSS to PNG, no browser.</p>
</div>`;
const css = `
.card { width: 1200px; height: 630px; display: flex; align-items: center; justify-content: center;
        background: linear-gradient(to bottom, #dbeafe, #fef2f2); }
h1 { font-size: 60px; font-weight: bold; color: #1e293b; }
p { font-size: 24px; color: #475569; text-align: center; }`;

const t0 = Date.now();
const res = JSON.parse(await exp.PaperSharp.Wasm.Api.Render(JSON.stringify({ html, css, format: "png", width: 1200, scale: 1 })));
if (!res.ok) { console.error("RENDER FAIL", res.error); process.exit(1); }
writeFileSync("smoke.png", Buffer.from(res.data, "base64"));
console.log("smoke.png written in", Date.now() - t0, "ms, bytes:", res.data.length);

const pdf = JSON.parse(await exp.PaperSharp.Wasm.Api.Render(JSON.stringify({ html, css, format: "pdf" })));
if (pdf.ok) writeFileSync("smoke.pdf", Buffer.from(pdf.data, "base64"));
console.log("pdf:", pdf.ok ? "ok " + pdf.data.length : "FAIL " + pdf.error);

const svg = JSON.parse(await exp.PaperSharp.Wasm.Api.Render(JSON.stringify({ html, css, format: "svg", width: 1200 })));
if (svg.ok) writeFileSync("smoke.svg", Buffer.from(svg.data, "base64"));
console.log("svg:", svg.ok ? "ok " + svg.data.length : "FAIL " + svg.error);
process.exit(0);
