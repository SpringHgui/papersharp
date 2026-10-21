import { render } from "papersharp";
import { writeFile } from "node:fs/promises";

const png = await render(
  <div
    style={{
      fontSize: 72,
      background: "linear-gradient(to bottom right, #fff7ed, #fecaca)",
    }}
    tw="w-full h-full flex items-center justify-center"
  >
    Hello PaperSharp
  </div>,
  { width: 1200, height: 630 },
);
await writeFile("hello.png", png);
