// 在无头浏览器里跑一段表达式，把结果打印出来（调试 wasm 启动这类「页内才会发生」的问题）。
//   node scripts/eval.mjs <url> <expression> [ settleMs ]
import { withBrowser, digest, sleep, writeShot } from "./cdp.mjs";
import { readFileSync } from "node:fs";

const url = process.argv[2];
// 表达式支持 @文件 —— 长 JS 走 argv 会被 shell 引号撕碎。
const raw = process.argv[3] ?? "";
const expression = raw.startsWith("@") ? readFileSync(raw.slice(1), "utf8") : raw;
const settleMs = Number(process.argv[4] ?? 0);
const outPng = process.argv[5];
if (!url || !expression) {
  console.error("用法: node scripts/eval.mjs <url> <expression|@file> [settleMs] [outPng]");
  process.exit(2);
}

const result = await withBrowser({
  url,
  width: Number(process.env.VERIFY_WIDTH ?? 1440),
  height: Number(process.env.VERIFY_HEIGHT ?? 1200),
  async fn({ cdp, sessionId }) {
    if (settleMs) await sleep(settleMs);
    const value = await cdp.evaluate(sessionId, `(${expression})()`);
    const shot = outPng ? await writeShot(cdp, sessionId, outPng) : null;
    return { value, shot, noise: digest(cdp) };
  },
});

console.log(JSON.stringify(result, null, 2));
