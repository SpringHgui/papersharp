// 存为 image.tsx，运行 `bun image.tsx` 即可写出 1200×630 的 output.png
import { renderToFile } from "papersharp";

await renderToFile(
  "./output.png",
  <div tw="w-full h-full flex items-center justify-center bg-linear-to-b from-blue-100 to-red-50">
    <h1 tw="text-6xl font-bold text-slate-800">Hello from PaperSharp</h1>
  </div>,
  { width: 1200, height: 630 },
);
