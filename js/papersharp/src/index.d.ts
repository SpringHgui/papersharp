export interface RenderOptions {
  /** 画布 CSS 宽度，默认 1200 */
  width?: number;
  /** 固定画布高度（给根元素 w-full/h-full 提供 100% 参照） */
  height?: number;
  /** 设备缩放（retina 导出用 2），默认 1 */
  scale?: number;
  /** "png" | "jpeg" | "webp" | "svg" | "pdf"，默认 "png" */
  format?: string;
  /** 追加的用户 CSS（与 Tailwind 生成结果合并） */
  css?: string;
  /** jpeg/webp 质量 0-100，默认 90 */
  quality?: number;
  /** pdf 页面："a4" | "letter" */
  pageSize?: string;
  /** 仅 renderToFile：true 时写完不结束常驻运行时（长驻服务连续渲染用）；默认写完后退出进程 */
  keepAlive?: boolean;
}

export function render(input: unknown, opts?: RenderOptions): Promise<Uint8Array>;
export function renderToFile(path: string, input: unknown, opts?: RenderOptions): Promise<Uint8Array>;
export function quit(code?: number): never;
export function registerFont(family: string, source: string | Uint8Array, weight?: number): Promise<void>;
export function dataUri(src: string): Promise<string>;
export function info(): Promise<Record<string, string | boolean>>;
