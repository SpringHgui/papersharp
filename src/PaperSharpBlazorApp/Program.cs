using PaperSharpBlazorApp.Components;
using PaperSharpBlazorApp.Services;

namespace PaperSharpBlazorApp
{
    public class Program
    {
        public static void Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);

            // Add services to the container.
            builder.Services.AddRazorComponents()
                .AddInteractiveServerComponents();

            // Playground：渲染是纯函数式的进程内调用，缓存按进程共享即可。
            builder.Services.AddSingleton<PlaygroundSamples>();
            builder.Services.AddSingleton<PlaygroundService>();

            var app = builder.Build();

            // Configure the HTTP request pipeline.
            if (!app.Environment.IsDevelopment())
            {
                app.UseExceptionHandler("/Error");
                // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
                app.UseHsts();
            }

            app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
            app.UseHttpsRedirection();

            app.UseAntiforgery();

            app.MapStaticAssets();
            MapPlayground(app);
            app.MapRazorComponents<App>()
                .AddInteractiveServerRenderMode();

            app.Run();
        }

        /// <summary>
        /// Playground 只需要两个读接口：按 token 取 PNG、按相对路径取样例资产。
        /// 渲染与样例列表都走 Blazor 电路里的服务调用，不经 HTTP——少一次序列化，
        /// 也免得给本地图开一个多余的写入口。
        /// </summary>
        static void MapPlayground(WebApplication app)
        {
            // 缓存被淘掉就 404；<img> 断图比整页报错好。
            app.MapGet("/api/playground/image/{token}", (string token, PlaygroundService svc) =>
            {
                var png = svc.PeekImage(token);
                return png == null ? Results.NotFound() : Results.Bytes(png, "image/png");
            });

            // 让预览 iframe 与引擎解析到同一份相对资源（imgs/logo.png 等），否则差异表会多出假项。
            app.MapGet("/api/playground/asset/{**relative}", (string relative, PlaygroundService svc) =>
            {
                var asset = svc.ReadAsset(relative);
                return asset == null ? Results.NotFound() : Results.Bytes(asset.Value.Bytes, asset.Value.ContentType);
            });
        }
    }
}
