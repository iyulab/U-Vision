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

            // 웹 기본 JSON 옵션의 AllowReadingFromString 때문에 생성기가 숫자를 «숫자 또는 숫자 모양 문자열»로 적는다.
            // 그것은 읽기 쪽 관용이지 wire 모양이 아니다 — 서버는 숫자를 숫자로만 쓰고, 클라이언트도 숫자로 보낸다.
            // 계약은 정본 모양만 적어, 생성된 타입이 숫자를 문자열과 섞지 않게 한다(JSON 옵션은 건드리지 않는다).
            if (schema.Type is { } type
                && (type & (JsonSchemaType.Number | JsonSchemaType.Integer)) != 0
                && (type & JsonSchemaType.String) != 0)
            {
                schema.Type = type & ~JsonSchemaType.String;
                schema.Pattern = null;
            }
            return Task.CompletedTask;
        });
        return options;
    }
}
