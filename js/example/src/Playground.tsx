import { useCallback, useEffect, useRef, useState, type ReactNode } from "react";
import { render, warmUp, type EngineFormat } from "./engine";
import { presets } from "./presets";
import { useCopy } from "./i18n";

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

// 整页样例（presets[].src）按需抓取并缓存：切回同一个样例不必再取一次。
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

/** 从整页文档里切出 <style> 块与 <body>：分别喂 CSS 编辑器与 HTML 编辑器。 */
function splitDoc(src: string) {
  const style = /<style[^>]*>([\s\S]*?)<\/style>/i.exec(src);
  const body = /<body[^>]*>([\s\S]*?)<\/body>/i.exec(src);
  const lines = (style?.[1] ?? "").split("\n");
  const pad = Math.min(...lines.filter((l) => l.trim()).map((l) => l.length - l.trimStart().length));
  const css = lines.map((l) => l.slice(pad)).join("\n").trim();
  return { html: (body?.[1] ?? src).trim(), css };
}

interface Output {
  url: string;
  /** 展示用产物：栅格格式就是 url 本身，PDF 则是另渲染的一张 PNG。 */
  preview: string;
  previewIsImage: boolean;
  /** 预览位图实际用的像素密度：PDF 的预览会跟屏幕 DPR 走，与产物的倍率不是一回事。 */
  previewScale: number;
  bytes: number;
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
      const bytes = await render({ ...req, format });
      const url = toUrl(bytes, format);

      // PDF 直接塞 iframe 会被 Chrome 内置查看器接管（深色工具栏 + 缩略图栏），
      // 所以另渲一张 PNG 只用于展示，下载与体积统计仍然是真正的 PDF 产物。
      // 展示那张按 max(倍率, 屏幕 DPR) 出图：PDF 是矢量、不吃倍率，但位图预览吃，
      // 高分屏上按 1× 出就是一屏糊字。
      let preview = url;
      let previewIsImage = isRaster(format);
      let previewScale = scale;
      if (format === "pdf") {
        previewScale = Math.max(scale, window.devicePixelRatio || 1);
        try {
          preview = toUrl(await render({ ...req, scale: previewScale, format: "png" }), "png");
          previewIsImage = true;
        } catch {
          /* 预览图渲染失败不致命：退回 iframe，让用户至少看到真 PDF */
          previewScale = scale;
        }
      }

      const live = preview === url ? [url] : [url, preview];
      for (const stale of urls.current) if (!live.includes(stale)) URL.revokeObjectURL(stale);
      urls.current = live;

      if (previewIsImage) await measureImage(preview).then(setDims);
      else setDims({ w: width * scale, h: 0 });
      setError(null);
      renderedSig.current = requested;
      setOut({ url, preview, previewIsImage, previewScale, bytes: bytes.byteLength, format });
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
  // 页面尺寸以引擎位图实测为准（不是请求 width）。位图是物理像素，要按它自己的像素密度折回 CSS：
  // 栅格格式的预览就是产物（吃倍率），PDF 的预览是另渲的 DPR 图。无位图可量（SVG iframe 预览）时退回请求宽度与固定视口。
  const pScale = out?.previewScale ?? scale;
  const contentH = dims.h ? dims.h / pScale : PANE_H;
  const contentW = dims.w ? dims.w / pScale : width;
  // 尺寸报的是下载件的含义：位图产物就是它的像素数；PDF 是矢量，报预览换算出的 CSS 页尺寸，
  // 否则高分屏上会冒出一个跟倍率对不上的物理像素数。
  const shownDims = out?.format === "pdf" ? { w: Math.round(contentW), h: Math.round(contentH) } : dims;

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

          <div className="panes one">
            <div className="pane">
              {out && out.previewIsImage && (
                <ScaleBox naturalW={contentW} naturalH={contentH}>
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
              {t.stats.size}: <b>{out ? `${(out.bytes / 1024).toFixed(0)} KB` : "—"}</b>
            </span>
            {shownDims.h > 0 && (
              <span>
                {t.stats.dims}: <b>{`${shownDims.w}×${shownDims.h} px`}</b>
              </span>
            )}
            <span className="dot" />
            <span>{busy ? t.status.rendering : error ? t.status.error : out ? t.status.ready : t.status.booting}</span>
            {out?.format === "pdf" && <span className="note">{t.status.pdfNote}</span>}
          </footer>
        </div>
      </div>
    </section>
  );
}

/** 预览视口高度（CSS 像素）：iframe 类内容按这个高度裁，图片类内容按自身比例铺满。 */
const PANE_H = 520;

/**
 * 把自然尺寸的内容等比缩进可用宽度里。
 * 给了 naturalH 就按内容比例定高（图片不留空白），否则铺满 PANE_H（iframe 需要确定高度）。
 * 超过 PANE_H 的长文档在框内滚动。
 */
function ScaleBox({
  naturalW,
  naturalH = PANE_H,
  children,
}: {
  naturalW: number;
  naturalH?: number;
  children: ReactNode;
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
    <div ref={boxRef} className="scaler" style={{ height: Math.min(scaledH, PANE_H) }}>
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
