using Xunit;

// PaperSharp 的 Renderer/FontManager/ImageLoader 依赖静态可变全局态（BaseDir、字体注册表、图片缓存），
// 并非线程安全。xUnit 默认跨 collection 并行会互相污染渲染结果，这里整程序集串行执行。
// （顺带的真实发现：多线程同时调 Renderer.RenderPdf 目前是不安全的，见 ROADMAP 候选项。）
[assembly: CollectionBehavior(DisableTestParallelization = true)]
