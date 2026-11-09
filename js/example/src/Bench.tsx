import { BENCH_ROWS, CHART_FILES, COLS, SNAPSHOT, fmt, type BenchRow } from "./benchData";
import { useCopy } from "./i18n";

const GH = "https://github.com/SpringHgui/papersharp";
const CHART_DIR = `${GH}/tree/master/benchmarks/CiBench/results/charts`;

function cell(row: BenchRow, col: (typeof COLS)[number]): string {
  return col.key === "coldWarm" ? row.coldWarm : fmt(row[col.key], col.digits);
}

export function Bench() {
  const { copy, lang } = useCopy();
  const b = copy.bench;

  return (
    <section id="bench" className="band alt">
      <h2>{b.title}</h2>
      <p className="bench-lede">{b.intro}</p>

      <div className="kpis">
        {b.kpis.map((k) => (
          <div className="kpi" key={k.label}>
            <b>{k.value}</b>
            <span>{k.label}</span>
          </div>
        ))}
      </div>

      <div className="charts2">
        {CHART_FILES.map((file) => (
          <a key={file} href={`/charts/${file}`} target="_blank" rel="noreferrer">
            <img
              src={`/charts/${file}`}
              alt={b.charts.find((c) => c.file === file)?.alt ?? file}
              width={480}
              height={320}
              loading="lazy"
            />
          </a>
        ))}
      </div>

      <div className="bench-table-wrap">
        <table className="bench">
          <thead>
            <tr>
              <th scope="col">{b.columns.name}</th>
              <th scope="col">{b.columns.runtime}</th>
              {COLS.map((c, i) => (
                <th key={c.key} scope="col">
                  {b.columns.metrics[i]}
                </th>
              ))}
            </tr>
          </thead>
          <tbody>
            {BENCH_ROWS.map((row) => (
              <tr key={row.name} className={row.ours ? "ours" : ""}>
                <th scope="row">{row.name}</th>
                <td className="dim">{row.runtime[lang]}</td>
                {COLS.map((c) => (
                  <td key={c.key}>{cell(row, c)}</td>
                ))}
              </tr>
            ))}
          </tbody>
        </table>
      </div>

      <p className="bench-note">{b.note}</p>
      <p className="bench-src">
        {b.source}{" "}
        <a href={`${GH}/blob/master/benchmarks/CiBench/README.md`} target="_blank" rel="noreferrer">
          CiBench README
        </a>
        {" · "}
        <a href={`${GH}/blob/master/benchmarks/CiBench/results/${SNAPSHOT}.json`} target="_blank" rel="noreferrer">
          {SNAPSHOT}.json
        </a>
        {" · "}
        <a href={`${GH}/blob/master/benchmarks/CiBench/results/${SNAPSHOT}.csv`} target="_blank" rel="noreferrer">
          {SNAPSHOT}.csv
        </a>
        {" · "}
        <a href={CHART_DIR} target="_blank" rel="noreferrer">
          {b.allCharts}
        </a>
      </p>
    </section>
  );
}
