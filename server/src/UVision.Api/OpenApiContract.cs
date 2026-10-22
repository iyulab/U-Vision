using System.Text.Json.Nodes;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;
using UVision.Api.Models;

namespace UVision.Api;

/// <summary>
/// 단독 서버가 내놓는 OpenAPI 문서(<c>/openapi/v1.json</c>)의 모양. 문서 생성은 단독 서버에만 둔다 —
/// 임베드 호스트는 자기 <c>AddOpenApi</c> 를 부르면 같은 엔드포인트 메타데이터를 그대로 얻는다
/// (<c>UVision.Hosting</c> 은 OpenAPI 패키지에 의존하지 않는다 — 호스트 오염 방지).
/// </summary>
public static class OpenApiContract
{
    public static OpenApiOptions AddUVisionContract(this OpenApiOptions options)
    {
        options.AddDocumentTransformer((document, _, _) =>
        {
            var version = typeof(UVisionOptions).Assembly.GetName().Version;
            document.Info = new OpenApiInfo
            {
                Title = "U-Vision API",
                Version = version is null ? "0.0.0" : $"{version.Major}.{version.Minor}.{version.Build}",
                Description = "Self-hosted vision judgment engine. Paths are shown under the default API base path; a host may mount it elsewhere.",
            };
            return Task.CompletedTask;
        });

        // AuthorityStage 는 사용자 정의 변환기(snake_case 문자열)라 생성기가 모양을 모른다 — 실제 wire 값을 적는다.
        options.AddSchemaTransformer((schema, context, _) =>
        {
            if (context.JsonTypeInfo.Type == typeof(AuthorityStage))
            {
                schema.Type = JsonSchemaType.String;
                schema.Enum = Enum.GetValues<AuthorityStage>()
                    .Select(s => (JsonNode)JsonValue.Create(AuthorityStageJsonConverter.ToWire(s)))
                    .ToList();
            }
            return Task.CompletedTask;
        });
        return options;
    }
}
