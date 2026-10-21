// Node 侧 worker：把浏览器系 HTML→PDF 工具统一到一个 stdio 行协议后面。
//
// 为什么要 worker 而不是一次一调：驱动要保证「所有引擎走同一条测量路径」才敢比。
// worker 常驻 = warm 口径（JIT/浏览器/字体缓存只付一次），驱动每档重启进程 = cold 口径。
//
// 协议（stdin 收，stdout 回，一行一个 JSON）：
//   {"id":1,"cmd":"gen","engine":"puppeteer","html":"<abs>","out":"<abs>"}
//     -> {"id":1,"ok":true,"ms":123.4,"bytes":45678,"pages":3,"err":null,"ver":"..."}
//   {"id":0,"cmd":"stat"} -> {"id":0,"ok":true,"engines":{"puppeteer":"25.x",...}}
//   {"id":-1,"cmd":"quit"} -> 退出
//
// PDF 参数与 PdfBench 既有 puppeteer 适配器逐项对齐：A4 / printBackground / 四边 0 /
// preferCSSPageSize / networkidle —— 换了参数就等于换了口径，历史数字作废。
import fs from 'node:fs';
import { pathToFileURL } from 'node:url';
import { createInterface } from 'node:readline';

const CHROME = 'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe';

let _puppeteer = null, _playwright = null;
async function load() {
  if (!_puppeteer) _puppeteer = await import('puppeteer-core');
  if (!_playwright) _playwright = await import('playwright');
  return { puppeteer: _puppeteer.default ?? _puppeteer, playwright: _playwright };
}

// 每个 engine 一个惰性单例资源（browser），跨 job 复用
const live = new Map();
async function browser(engine) {
  if (live.has(engine)) return live.get(engine);
  const { puppeteer, playwright } = await load();
  let b;
  if (engine === 'puppeteer') {
    b = await puppeteer.launch({
      headless: true, executablePath: CHROME,
      args: ['--no-sandbox', '--disable-gpu', '--disable-dev-shm-usage', '--no-first-run',
             '--disable-features=Translate', '--metrics-recording-only'],
    });
  } else if (engine === 'playwright') {
    b = await playwright.chromium.launch({
      headless: true, executablePath: CHROME,
      args: ['--no-sandbox', '--disable-gpu', '--disable-dev-shm-usage', '--no-first-run',
             '--metrics-recording-only'],
    });
  } else {
    throw new Error(`未知 engine: ${engine}`);
  }
  live.set(engine, b);
  return b;
}

export async function gen(engine, htmlPath, outPath) {
  const url = pathToFileURL(htmlPath).href;
  const b = await browser(engine);
  const t0 = process.hrtime.bigint();
  if (engine === 'puppeteer') {
    const page = await b.newPage();
    try {
      await page.goto(url, { waitUntil: 'networkidle0', timeout: 120000 });
      await page.pdf({
        path: outPath, format: 'A4', printBackground: true,
        margin: { top: '0', bottom: '0', left: '0', right: '0' },
        preferCSSPageSize: true,
      });
    } finally { await page.close(); }
  } else {
    const page = await b.newPage();
    try {
      await page.goto(url, { waitUntil: 'networkidle', timeout: 120000 });
      await page.pdf({
        path: outPath, format: 'A4', printBackground: true,
        margin: { top: 0, bottom: 0, left: 0, right: 0 },
        preferCSSPageSize: true,
      });
    } finally { await page.close(); }
  }
  const ms = Number(process.hrtime.bigint() - t0) / 1e6;
  return { ms, bytes: fs.statSync(outPath).size };
}

export async function dispose() {
  for (const [, b] of live) { try { await b.close(); } catch { /* 已退出 */ } }
  live.clear();
}

const RL = createInterface({ input: process.stdin, terminal: false });

async function handle(line) {
  let req;
  try { req = JSON.parse(line); } catch { return { id: -9, ok: false, err: 'bad json' }; }
  if (req.cmd === 'quit') { await dispose(); process.stdout.write('\u0000QUIT\n'); process.exit(0); }
  if (req.cmd === 'stat') {
    const { puppeteer, playwright } = await load();
    const pv = (m) => { try { return JSON.parse(fs.readFileSync(`node_modules/${m}/package.json`)).version; } catch { return '?'; } };
    return { id: req.id, ok: true, engines: { puppeteer: pv('puppeteer-core'), playwright: pv('playwright') }, node: process.version };
  }
  if (req.cmd !== 'gen') return { id: req.id, ok: false, err: `未知 cmd ${req.cmd}` };
  try {
    const r = await gen(req.engine, req.html, req.out);
    return { id: req.id, ok: true, ...r, err: null };
  } catch (e) {
    // 引擎炸了要把 browser 丢掉：留在 live 里会让后续每个 job 继承同一具尸体
    try { const b = live.get(req.engine); if (b) await b.close(); } catch { /* ignore */ }
    live.delete(req.engine);
    return { id: req.id, ok: false, err: String(e && e.message ? e.message : e).slice(0, 300) };
  }
}

if (process.argv[1] && process.argv[1].endsWith('node_worker.mjs')) {
  RL.on('line', async (line) => {
    if (!line.trim()) return;
    const res = await handle(line);
    process.stdout.write(JSON.stringify(res) + '\n');
  });
  process.on('SIGBREAK', async () => { await dispose(); process.exit(0); });
}
