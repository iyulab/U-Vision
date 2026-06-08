using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;

namespace UVision.Api;

public static class UVisionMiddleware
{
    public static IApplicationBuilder UseUVision(this IApplicationBuilder app)
    {
        var options = app.ApplicationServices.GetRequiredService<UVisionOptions>();

        var basePath = "/" + options.BasePath.Trim('/');
        var apiBasePath = "/" + options.ApiBasePath.Trim('/');

        var assembly = typeof(UVisionMiddleware).Assembly;
        var fileProvider = new EmbeddedFileProvider(assembly, "UVision.Api.wwwroot");

        // SPA 런타임 config — 소비앱 설정을 전역 변수로 주입.
        var configJson = JsonSerializer.Serialize(new
        {
            apiBase = apiBasePath,
            basePath,
            title = options.Title,
        });
        var configScript = $"<script>window.__UVISION_CONFIG__={configJson}</script>";

        // prefix 정확매칭: {prefix} 자체 또는 {prefix}/... 만 매칭(`/u-visionXxx` 형제 경로 오삼킴 방지).
        static bool MatchesPrefix(string path, string prefix) =>
            path.Equals(prefix, StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase);

        app.Use(async (context, next) =>
        {
            var path = context.Request.Path.Value ?? "";
            var method = context.Request.Method;

            // API: 엔드포인트 라우팅(UseEndpoints)에 위임.
            if (MatchesPrefix(path, apiBasePath))
            {
                await next();
                return;
            }

            // 정적 + SPA fallback: {basePath}/** (GET/HEAD)
            if ((method == "GET" || method == "HEAD")
                && MatchesPrefix(path, basePath))
            {
                var subpath = path.Length > basePath.Length
                    ? path[basePath.Length..].TrimStart('/')
                    : "";

                if (!string.IsNullOrEmpty(subpath) && Path.HasExtension(subpath))
                {
                    var fileInfo = fileProvider.GetFileInfo(subpath);
                    if (fileInfo.Exists)
                    {
                        context.Response.ContentType = GetContentType(subpath);
                        context.Response.ContentLength = fileInfo.Length;
                        // SW 스크립트는 항상 재검증(업데이트 신속 감지). 해시 에셋은 기본 캐시(SW precache가 신선도 관리).
                        var name = Path.GetFileName(subpath);
                        if (name.Equals("sw.js", StringComparison.OrdinalIgnoreCase)
                            || name.Equals("registerSW.js", StringComparison.OrdinalIgnoreCase))
                            context.Response.Headers.CacheControl = "no-cache";
                        await using var stream = fileInfo.CreateReadStream();
                        await stream.CopyToAsync(context.Response.Body);
                        return;
                    }
                }

                var index = fileProvider.GetFileInfo("index.html");
                if (index.Exists)
                {
                    context.Response.ContentType = "text/html; charset=utf-8";
                    context.Response.Headers.CacheControl = "no-cache";
                    await using var stream = index.CreateReadStream();
                    using var reader = new StreamReader(stream, Encoding.UTF8);
                    var html = await reader.ReadToEndAsync();

                    // 기본 base(/u-vision)와 다르면 Vite 빌드 절대경로 치환.
                    // ⚠️ index.html 한정 best-effort 치환 — SW/manifest/번들은 빌드타임 base가 baked되므로
                    //    실제 다른 경로로 마운트하려면 VITE_BASE를 맞춰 클라이언트를 재빌드해야 한다.
                    if (!basePath.Equals("/u-vision", StringComparison.OrdinalIgnoreCase))
                        html = html.Replace("/u-vision/", basePath + "/", StringComparison.OrdinalIgnoreCase);

                    html = html.Replace("</head>", configScript + "</head>", StringComparison.OrdinalIgnoreCase);

                    var bytes = Encoding.UTF8.GetBytes(html);
                    await context.Response.Body.WriteAsync(bytes);
                    return;
                }

                context.Response.StatusCode = 404;
                return;
            }

            await next();
        });

        return app;
    }

    private static string GetContentType(string path) =>
        Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".js" or ".mjs"   => "application/javascript",
            ".css"            => "text/css",
            ".html"           => "text/html; charset=utf-8",
            ".json"           => "application/json",
            ".webmanifest"    => "application/manifest+json",
            ".svg"            => "image/svg+xml",
            ".png"            => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".ico"            => "image/x-icon",
            ".woff"           => "font/woff",
            ".woff2"          => "font/woff2",
            _                 => "application/octet-stream",
        };
}
