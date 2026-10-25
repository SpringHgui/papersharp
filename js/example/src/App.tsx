import { useState } from "react";
import { Playground } from "./Playground";
import { useCopy } from "./i18n";

const GITHUB = "https://github.com/SpringHgui/papersharp";

const snippets = {
  csharp: `using PaperSharp;

byte[] pdf = Renderer.RenderPdf(html, css);                       // 分页矢量 PDF
byte[] png = Renderer.RenderPng(html, css, width: 794, scale: 2);  // 2x 高清
byte[] svg = Renderer.RenderSvg(html, css, 1200);                  // 矢量图元`,
  jsx: `import { render } from "papersharp";

const png = await render(
  <div tw="w-full h-full flex items-center justify-center"
       style={{ fontSize: 72 }}>
    Hello PaperSharp
  </div>,
  { width: 1200, height: 630 },
);`,
  browser: `import { dotnet } from "/_framework/dotnet.js";

const runtime = await dotnet.create();
const api = (await runtime.getAssemblyExports("PaperSharp.Wasm")).PaperSharp.Wasm.Api;

const { data } = JSON.parse(
  await api.Render(JSON.stringify({ html, css, format: "pdf", width: 794 })),
);
const pdf = Uint8Array.from(atob(data), (c) => c.charCodeAt(0));`,
};

export function App() {
  const { copy, lang, setLang } = useCopy();

  return (
    <>
      <nav className="nav">
        <a className="brand" href="#top">
          Paper<span>Sharp</span>
        </a>
        <div className="nav-links">
          <a href="#playground">{copy.nav.playground}</a>
          <a href="#features">{copy.nav.features}</a>
          <a href="#usage">{copy.nav.usage}</a>
        </div>
        <div className="nav-right">
          <button type="button" className="lang" onClick={() => setLang(lang === "zh" ? "en" : "zh")}>
            {lang === "zh" ? "EN" : "中文"}
          </button>
          <a className="gh" href={GITHUB} target="_blank" rel="noreferrer">
            GitHub
          </a>
        </div>
      </nav>

      <header className="hero" id="top">
        <p className="eyebrow">{copy.hero.eyebrow}</p>
        <h1>{copy.hero.title}</h1>
        <p className="lede">{copy.hero.subtitle}</p>
        <div className="cta">
          <a className="btn primary" href="#playground">
            {copy.hero.ctaPrimary}
          </a>
          <a className="btn ghost" href="#features">
            {copy.hero.ctaSecondary}
          </a>
        </div>
        <div className="installs">
          <code>
            <em>{copy.hero.installTitle}</em>
            {copy.hero.installCmd}
          </code>
          <code>
            <em>{copy.hero.npmTitle}</em>
            {copy.hero.npmCmd}
          </code>
        </div>
      </header>

      <Playground />

      <section id="features" className="band">
        <h2>{copy.features.title}</h2>
        <div className="grid">
          {copy.features.items.map((f) => (
            <article key={f.name}>
              <h3>{f.name}</h3>
              <p>{f.desc}</p>
            </article>
          ))}
        </div>
      </section>

      <section className="band alt">
        <h2>{copy.formats.title}</h2>
        <div className="fmts">
          {copy.formats.items.map((f) => (
            <div key={f.name} className="fmt">
              <b>{f.name}</b>
              <span>{f.desc}</span>
            </div>
          ))}
        </div>
      </section>

      <section id="usage" className="band">
        <h2>{copy.usage.title}</h2>
        <Usage />
      </section>

      <footer className="foot">
        <span>{copy.footer.note}</span>
        <span className="sep" />
        <a href="https://www.nuget.org/packages/PaperSharp" target="_blank" rel="noreferrer">
          {copy.footer.nuget}
        </a>
        <a href="https://www.npmjs.com/package/papersharp" target="_blank" rel="noreferrer">
          {copy.footer.npm}
        </a>
        <a href={GITHUB + "/blob/master/LICENSE"} target="_blank" rel="noreferrer">
          {copy.footer.license}
        </a>
      </footer>
    </>
  );
}

function Usage() {
  const { copy } = useCopy();
  const keys = ["csharp", "jsx", "browser"] as const;
  const [tab, setTab] = useState<(typeof keys)[number]>("csharp");

  return (
    <div className="usage">
      <div className="seg">
        {keys.map((k) => (
          <button key={k} type="button" className={tab === k ? "on" : ""} onClick={() => setTab(k)}>
            {copy.usage.tabs[k]}
          </button>
        ))}
      </div>
      <p className="note">{copy.usage.notes[tab]}</p>
      <pre className="code">{snippets[tab]}</pre>
    </div>
  );
}
