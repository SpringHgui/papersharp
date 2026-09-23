using Xunit;

// 并发安全化改造（RenderScope 请求隔离）后，跨 collection 并行执行本身就是线程安全回归测试的一部分：
// 若再有静态可变渲染态混入，这里的并行会先炸。
[assembly: CollectionBehavior(DisableTestParallelization = false)]
