import { useCallback, useEffect, useRef, useState, type ReactNode } from "react";
import { render, warmUp, type EngineFormat } from "./engine";
import { presets } from "./presets";
import { useCopy } from "./i18n";

type ViewMode = "engine" | "side" | "overlay";

const MIME: Record<EngineFormat, string> = {
  png: "image/png",
  jpeg: "image/jpeg",
  webp: "image/webp",
  svg: "image/svg+xml",
  pdf: "application/pdf",
  ico: "image/x-icon",
};

const EXT: Record<EngineFormat, string> = { png: "png", jpeg: "jpg", webp: "webp", svg: "svg", pdf: "pdf", ico: "ico" };

const isRaster = (f: EngineFormat) => f === "png" || f === "jpeg" || f === "webp" || f === "ico";

/** 产物字节 → 可展示的 object URL（由调用方负责 revoke）。 */
function toUrl(bytes: Uint8Array, format: EngineFormat): string {
  return URL.createObjectURL(new Blob([bytes as BlobPart], { type: MIME[format] }));
}

// 整页样例（presets[].src）按需抓取并缓存：默认挑战页首屏即要可编辑，不能等用户点开才加载。
const sampleCache = new Map<string, string>();
async function loadSample(src: string): Promise<string> {
  const hit = sampleCache.get(src);
  if (hit !== undefined) return hit;
  const res = await fetch(src);
  if (!res.ok) throw new Error(`${res.status} ${res.statusText} — ${src}`);
  const text = await res.text();
  sampleCache.set(src, text);
  return text;
}

/** 从整页文档里切出 <style> 块与 <body>：分别喂 CSS 编辑器与 HTML 编辑器，拼回 srcdoc 时仍是完整页。 */
function splitDoc(src: string) {
  const style = /<style[^>]*>([\s\S]*?)<\/style>/i.exec(src);
  const body = /<body[^>]*>([\s\S]*?)<\/body>/i.exec(src);
  const lines = (style?.[1] ?? "").split("\n");
  const pad = Math.min(...lines.filter((l) => l.trim()).map((l) => l.length - l.trimStart().length));
  const css = lines.map((l) => l.slice(pad)).join("\n").trim();
  return { html: (body?.[1] ?? src).trim(), css };
}

// 并排/叠加的浏览器栏是「屏幕」渲染，@page 的边距被浏览器忽略；而引擎的预览位图（RenderPng）
// 会按 @page margin 留白（Renderer.cs:313），两栏顶边就差一个页边距。对比用的预览走这份剥了 @page
// 的 CSS，边距归零、和浏览器同框；@page size 也不参与——PDF 页尺寸由 render 的 pageSize 参数定，下载产物不受影响。
// 正则吃掉一层嵌套花括号：@page 里还可能有 @top-center / @bottom-center 这类 margin box 块。
const stripPrint = (css: string) => css.replace(/@page[^{]*\{(?:[^{}]|\{[^{}]*\})*\}/g, "");

// 与引擎 tailwind.mjs 的 PREFLIGHT 同源：两边吃到同一份重置，对比才不留 8px 的假差异。
const BROWSER_RESET = `
body { margin: 0; }
h1, h2, h3, h4, h5, h6, p, ul, ol, figure, blockquote { margin: 0; }
ul, ol { padding: 0; list-style: none; }
img { display: block; }
`;

interface Output {
  url: string;
  /** 展示用产物：栅格格式就是 url 本身，PDF 则是另渲染的一张 PNG。 */
  preview: string;
  previewIsImage: boolean;
  bytes: number;
  ms: number;
  format: EngineFormat;
}

export function Playground() {
  const { copy, lang } = useCopy();
  const t = copy.playground;

  const initial = presets[0];
  const [presetId, setPresetId] = useState(initial.id);
  const [html, setHtml] = useState(initial.html);
  const [css, setCss] = useState(initial.css);
  const [width, setWidth] = useState(initial.width);
  const [scale, setScale] = useState(initial.scale);
  const [format, setFormat] = useState<EngineFormat>(initial.format);
  const [quality, setQuality] = useState(90);
  const [view, setView] = useState<ViewMode>("engine");
  const [opacity, setOpacity] = useState(55);
  const [auto, setAuto] = useState(true);

  // 异步样例未就绪前不许跑渲染：空 HTML 会先出一张白页再被真内容闪掉。
  const [loadingSample, setLoadingSample] = useState(Boolean(initial.src));

  const [out, setOut] = useState<Output | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [booting, setBooting] = useState(true);
  const [dims, setDims] = useState({ w: 0, h: 0 });

  // 输入指纹：与上一次真正渲染的指纹一致就跳过，省掉 debounce 抖动带来的重复跑引擎。
  const signature = [presetId, html, css, width, scale, format, quality].join("\u0000");
  const renderedSig = useRef("");
  const urls = useRef<string[]>([]);
  const scrollers = useRef(new Map<string, HTMLDivElement | null>());

  /** 并排/叠加两层各自滚动会把对比拆成两个时刻，这里把 scrollTop 同步给另一层。 */
  function scrollPartner(from: string) {
    return (top: number) => {
      for (const [key, el] of scrollers.current) if (key !== from && el) el.scrollTop = top;
    };
  }
  function attachScroller(key: string) {
    return (el: HTMLDivElement | null) => {
      scrollers.current.set(key, el);
    };
  }

  useEffect(() => {
    warmUp()
      .then(() => setBooting(false))
      .catch((e) => {
        setBooting(false);
        setError(String(e?.message ?? e));
      });
  }, []);

  // 首屏就选中了带 src 的样例：直接载入，省掉一次「先选中后加载」的往返。
  useEffect(() => {
    if (!initial.src) return;
    applySrc(initial.src, initial.css, initial.html);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  useEffect(
    () => () => {
      for (const u of urls.current) URL.revokeObjectURL(u);
    },
    [],
  );

  const doRender = useCallback(async () => {
    setBusy(true);
    const requested = signature;
    const req = {
      html,
      css,
      width,
      scale,
      quality,
      pageSize: presets.find((p) => p.id === presetId)?.pageSize,
      icoSize: presets.find((p) => p.id === presetId)?.icoSize,
    };
    try {
      const t0 = performance.now();
      const bytes = await render({ ...req, format });
      const ms = Math.round(performance.now() - t0);
      const url = toUrl(bytes, format);

      // PDF 直接塞 iframe 会被 Chrome 内置查看器接管（深色工具栏 + 缩略图栏），
      // 而且叠加/并排视图拿不到可对齐的位图。所以另渲一张 PNG 只用于展示，
      // 下载与体积统计仍然是真正的 PDF 产物。
      let preview = url;
      let previewIsImage = isRaster(format);
      if (format === "pdf") {
        try {
          // 预览走剥掉 @page 的 CSS：让引擎位图与浏览器栏同按无页边距布局，顶边对齐；下载 url 仍是完整 PDF。
          preview = toUrl(await render({ ...req, css: stripPrint(css), format: "png" }), "png");
          previewIsImage = true;
        } catch {
          /* 预览图渲染失败不致命：退回 iframe，让用户至少看到真 PDF */
        }
      }

      const live = preview === url ? [url] : [url, preview];
      for (const stale of urls.current) if (!live.includes(stale)) URL.revokeObjectURL(stale);
      urls.current = live;

      if (previewIsImage) await measureImage(preview).then(setDims);
      else setDims({ w: width * scale, h: 0 });
      setError(null);
      renderedSig.current = requested;
      setOut({ url, preview, previewIsImage, bytes: bytes.byteLength, ms, format });
    } catch (e: any) {
      renderedSig.current = requested; // 失败也记账，否则同一份错误输入会反复重跑
      setError(String(e?.message ?? e));
    } finally {
      setBusy(false);
    }
  }, [html, css, format, width, scale, quality, presetId, signature]);

  useEffect(() => {
    if (!auto || busy || loadingSample || signature === renderedSig.current) return;
    const id = setTimeout(doRender, 350);
    return () => clearTimeout(id);
  }, [auto, busy, loadingSample, signature, doRender]);

  /** 拉取整页样例并填进两个编辑器；失败时保留 preset 自带的占位文案，别静默留白。 */
  async function applySrc(src: string, presetCss: string, presetHtml: string) {
    setLoadingSample(true);
    try {
      const { html: docHtml, css: docCss } = splitDoc(await loadSample(src));
      setHtml(presetHtml || docHtml);
      setCss(presetCss || docCss);
    } catch (e: any) {
      setError(`样例加载失败：${e?.message ?? e}`);
    } finally {
      setLoadingSample(false);
    }
  }

  function applyPreset(id: string) {
    const p = presets.find((x) => x.id === id)!;
    setPresetId(id);
    setWidth(p.width);
    setScale(p.scale);
    setFormat(p.format);
    if (p.src) applySrc(p.src, p.css, p.html);
    else {
      setHtml(p.html);
      setCss(p.css);
    }
  }

  const filename = `papersharp-${presetId}.${EXT[format]}`;
  const showBrowser = view !== "engine";
  const srcdoc = `<meta charset="utf-8"><style>${BROWSER_RESET}${css}</style>${html}`;
  // 浏览器视口对齐引擎产物的布局尺寸，两栏才能逐像素比。倍率≠1 时位图是物理像素，宽高都要折回 CSS。
  // 页面尺寸以位图实测为准（不是请求 width），否则两栏比例差一档；@page 边距已只从预览渲染里剥掉
  // （见 stripPrint），下载产物仍保留完整打印语义。无位图可量（SVG iframe 预览）时退回请求宽度与固定视口。
  const contentH = dims.h ? dims.h / scale : PANE_H;
  const contentW = dims.w ? dims.w / scale : width;

  return (
    <section id="playground" className="pg">
      <header className="pg-head">
        <h2>{t.title}</h2>
        <p>{t.subtitle}</p>
      </header>

      <div className="pg-bar">
        <label className="field">
          <span>{t.sample}</span>
          <select value={presetId} onChange={(e) => applyPreset(e.target.value)}>
            {presets.map((p) => (
              <option key={p.id} value={p.id}>
                {p.name[lang]}
              </option>
            ))}
          </select>
        </label>

        <label className="field num">
          <span>{t.controls.width}</span>
          <input
            type="number"
            min={240}
            max={4096}
            step={1}
            value={width}
            onChange={(e) => setWidth(Number(e.target.value) || width)}
          />
        </label>

        <label className="field">
          <span>{t.controls.scale}</span>
          <select value={scale} onChange={(e) => setScale(Number(e.target.value))}>
            {[1, 2, 3].map((s) => (
              <option key={s} value={s}>
                {s}×
              </option>
            ))}
          </select>
        </label>

        <label className="field">
          <span>{t.controls.format}</span>
          <select value={format} onChange={(e) => setFormat(e.target.value as EngineFormat)}>
            {(Object.keys(MIME) as EngineFormat[]).map((f) => (
              <option key={f} value={f}>
                {f.toUpperCase()}
              </option>
            ))}
          </select>
        </label>

        {(format === "jpeg" || format === "webp") && (
          <label className="field num">
            <span>{t.controls.quality}</span>
            <input
              type="number"
              min={1}
              max={100}
              value={quality}
              onChange={(e) => setQuality(Math.max(1, Math.min(100, Number(e.target.value) || quality)))}
            />
          </label>
        )}

        <div className="spacer" />

        <div className="seg" role="group">
          {(["engine", "side", "overlay"] as ViewMode[]).map((v) => (
            <button key={v} type="button" className={view === v ? "on" : ""} onClick={() => setView(v)}>
              {t.view[v]}
            </button>
          ))}
        </div>

        {view === "overlay" && (
          <label className="field range">
            <span>{lang === "zh" ? "引擎不透明度" : "Engine opacity"}</span>
            <input type="range" min={0} max={100} value={opacity} onChange={(e) => setOpacity(Number(e.target.value))} />
          </label>
        )}

        <label className="check">
          <input type="checkbox" checked={auto} onChange={(e) => setAuto(e.target.checked)} />
          {lang === "zh" ? "自动" : "Auto"}
        </label>

        <button type="button" className="btn" onClick={doRender} disabled={busy}>
          {busy ? t.status.rendering : t.actions.render}
        </button>

        <a className="btn ghost" href={out?.url} download={filename} aria-disabled={!out}>
          {t.actions.download}
        </a>
      </div>

      <div className="pg-body">
        <div className="editors">
          <label className="editor">
            <span className="editor-tag">{t.html}</span>
            <textarea value={html} spellCheck={false} onChange={(e) => setHtml(e.target.value)} />
          </label>
          <label className="editor">
            <span className="editor-tag">{t.css}</span>
            <textarea value={css} spellCheck={false} onChange={(e) => setCss(e.target.value)} />
          </label>
        </div>

        <div className="stage">
          {error && (
            <div className="err">
              <b>{t.status.error}</b>
              <pre>{error}</pre>
            </div>
          )}

          <div className={"panes " + (showBrowser ? "two" : "one")} style={{ ["--op" as any]: opacity / 100 }}>
            {showBrowser && (
              <div className="pane" data-label={t.status.browserLabel}>
                <ScaleBox naturalW={contentW} naturalH={contentH} attach={attachScroller("browser")} onScrolled={scrollPartner("browser")}>
                  <iframe title="browser" srcDoc={srcdoc} sandbox="" scrolling="no" />
                </ScaleBox>
              </div>
            )}

            <div className={view === "overlay" ? "pane overlay" : "pane"} data-label={t.status.engineLabel}>
              {out && out.previewIsImage && (
                <ScaleBox naturalW={contentW} naturalH={contentH} attach={attachScroller("engine")} onScrolled={scrollPartner("engine")}>
                  <img src={out.preview} alt="PaperSharp output" />
                </ScaleBox>
              )}
              {out && !out.previewIsImage && (
                <ScaleBox naturalW={width} naturalH={PANE_H}>
                  <iframe title={out.format} src={out.preview} />
                </ScaleBox>
              )}
              {!out && !error && <div className="hint">{booting ? t.status.booting : t.status.rendering}</div>}
            </div>
          </div>

          <footer className="stats">
            <span>
              {t.stats.time}: <b>{out ? `${out.ms} ms` : "—"}</b>
            </span>
            <span>
              {t.stats.size}: <b>{out ? `${(out.bytes / 1024).toFixed(0)} KB` : "—"}</b>
            </span>
            {dims.h > 0 && (
              <span>
                {t.stats.dims}: <b>{`${dims.w}×${dims.h} px`}</b>
              </span>
            )}
            <span className="dot" />
            <span>{busy ? t.status.rendering : error ? t.status.error : out ? t.status.ready : t.status.booting}</span>
            {out?.format === "pdf" && <span className="note">{t.status.pdfNote}</span>}
            {view === "overlay" && <span className="note">{t.status.overlayNote}</span>}
          </footer>
        </div>
      </div>
    </section>
  );
}

/** 预览视口高度（CSS 像素）：iframe 类内容按这个高度裁，图片类内容按自身比例铺满。 */
const PANE_H = 520;

/**
 * 把自然尺寸的内容等比缩进可用宽度里，引擎层与浏览器层用同一个系数才能逐像素对齐。
 * 给了 naturalH 就按内容比例定高（图片不留空白），否则铺满 PANE_H（iframe 需要确定高度）。
 * 超过 PANE_H 的长文档在框内滚动，两层的滚动位置由调用方同步。
 */
function ScaleBox({
  naturalW,
  naturalH = PANE_H,
  children,
  onScrolled,
  attach,
}: {
  naturalW: number;
  naturalH?: number;
  children: ReactNode;
  onScrolled?: (top: number) => void;
  attach?: (el: HTMLDivElement | null) => void;
}) {
  const boxRef = useRef<HTMLDivElement>(null);
  const [avail, setAvail] = useState(naturalW);

  useEffect(() => {
    const el = boxRef.current;
    if (!el) return;
    const ro = new ResizeObserver(([entry]) => setAvail(entry.contentRect.width));
    ro.observe(el);
    return () => ro.disconnect();
  }, []);

  const k = Math.min(1, avail / naturalW);
  const innerH = naturalH ? naturalH : PANE_H / k;
  const scaledH = Math.round(innerH * k);
  return (
    <div
      ref={(el) => {
        boxRef.current = el;
        attach?.(el);
      }}
      className="scaler"
      style={{ height: Math.min(scaledH, PANE_H) }}
      onScroll={(e) => onScrolled?.(e.currentTarget.scrollTop)}
    >
      {/* 缩放后的占位盒：transform 不改布局尺寸，没有这一层，滚动范围会按未缩放的 naturalH 算。 */}
      <div className="scaler-fit" style={{ width: Math.round(naturalW * k), height: scaledH }}>
        <div className="scaler-inner" style={{ width: naturalW, height: innerH, transform: `scale(${k})` }}>
          {children}
        </div>
      </div>
    </div>
  );
}

function measureImage(url: string): Promise<{ w: number; h: number }> {
  return new Promise((resolve) => {
    const img = new Image();
    img.onload = () => resolve({ w: img.naturalWidth, h: img.naturalHeight });
    img.onerror = () => resolve({ w: 0, h: 0 });
    img.src = url;
  });
}
