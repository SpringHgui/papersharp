// 站点布局/渲染验证：无头浏览器打开页面，等引擎出图（或超时），打印状态矩形 + 整页截图。
//   node scripts/verify.mjs [url] [outPng] [deadlineMs]
//   VERIFY_WIDTH / VERIFY_HEIGHT 覆盖视口
import { withBrowser, digest, writeShot } from "./cdp.mjs";

const url = process.argv[2] ?? "http://localhost:5173/";
const outPng = process.argv[3] ?? "verify.png";
const deadline = Date.now() + Number(process.argv[4] ?? 90000);
const width = Number(process.env.VERIFY_WIDTH ?? 1440);
const height = Number(process.env.VERIFY_HEIGHT ?? 1200);

const PROBE = `(() => {
  const q = (s) => document.querySelector(s);
  const rect = (el) => { const r = el.getBoundingClientRect(); return { sel: el.className || el.tagName, x: Math.round(r.x), y: Math.round(r.y), w: Math.round(r.width), h: Math.round(r.height) }; };
  const img = q('.pane img');
  return {
    vw: innerWidth, ready: document.readyState,
    hint: q('.hint')?.textContent?.trim() ?? null,
    err: q('.err')?.innerText?.slice(0, 300) ?? null,
    stats: [...document.querySelectorAll('.stats span')].map((s) => s.textContent.trim()),
    img: img ? { nw: img.naturalWidth, nh: img.naturalHeight, cw: Math.round(img.getBoundingClientRect().width) } : null,
    docH: Math.round(document.documentElement.scrollHeight),
    boxes: [...document.querySelectorAll('.nav, .hero h1, .pg, .pg-bar, .editor textarea, .stage, .pane, .band, .foot')].map(rect),
  };
})()`;

const result = await withBrowser({
  url,
  width,
  height,
  async fn({ cdp, sessionId }) {
    let state = null;
    for (;;) {
      try {
        state = await cdp.evaluate(sessionId, PROBE, { awaitPromise: false });
      } catch {
        /* 求值撞上导航，下一轮再问 */
      }
      if (state?.img || state?.err) break;
      if (Date.now() > deadline) break;
      await new Promise((r) => setTimeout(r, 1500));
    }
    const shot = await writeShot(cdp, sessionId, outPng);
    return { state, shot, noise: digest(cdp) };
  },
});

console.log(JSON.stringify(result, null, 2));
