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
        // (스키마 id, 속성) — null 이면 wire 에서 빠지는 속성. 참조 타입이면 문서에서 oneOf 를 풀고, 어느 쪽이든 required 가 아니다.
        var omitted = new System.Collections.Concurrent.ConcurrentDictionary<(string SchemaId, string Property), bool>();

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

            // ReferenceLabel 은 변환기가 없어 숫자로 나간다(0 = ok, 1 = ng — WireContractTests 가 동결). 계약은 그 두 값만 적는다.
            if (context.JsonTypeInfo.Type == typeof(ReferenceLabel))
            {
                schema.Type = JsonSchemaType.Integer;
                schema.Enum = Enum.GetValues<ReferenceLabel>().Select(l => (JsonNode)JsonValue.Create((int)l)).ToList();
                schema.Description = "0 = ok reference, 1 = ng reference";
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

            // `WhenWritingNull` · `WhenWritingDefault` 속성은 null(기본값)일 때 wire 에서 빠진다 — null 로 오는 일이 없다. 생성기는 C# 의 nullable 을 보고
            // «null 이거나 값»으로 적는데, 그러면 생성된 클라이언트 타입이 오지 않는 null 을 처리하게 된다. 계약은 «생략되거나
            // 값»(required 아님 + null 없음)으로 적는다. null 을 실제로 쓰는 nullable(예: 분모 0 인 비율)은 그대로 둔다.
            if (context.JsonTypeInfo.Kind == System.Text.Json.Serialization.Metadata.JsonTypeInfoKind.Object
                && schema.Properties is { Count: > 0 } properties)
            {
                foreach (var property in context.JsonTypeInfo.Properties)
                {
                    if (!OmittedFromWire(property) || !properties.TryGetValue(property.Name, out var prop))
                        continue;
                    omitted[(context.JsonTypeInfo.Type.Name, property.Name)] = true;
                    if (prop.Type is { } t && (t & JsonSchemaType.Null) != 0 && prop is OpenApiSchema concrete)
                        concrete.Type = t & ~JsonSchemaType.Null;
                }
            }
            return Task.CompletedTask;
        });

        options.AddDocumentTransformer((document, _, _) =>
        {
            if (document.Components?.Schemas is not { } schemas)
                return Task.CompletedTask;

            // 참조 타입 속성은 생성기가 문서를 지을 때 `oneOf [null, $ref]` 로 감싼다(스키마 변환 뒤) — 모아 둔 속성을 여기서 푼다.
            foreach (var (schemaId, name) in omitted.Keys)
            {
                if (!schemas.TryGetValue(schemaId, out var owner) || owner.Properties is not { } properties
                    || !properties.TryGetValue(name, out var prop) || prop.OneOf is not { Count: 2 } oneOf)
                    continue;
                var nonNull = oneOf.Where(s => s.Type != JsonSchemaType.Null).ToList();
                if (nonNull.Count == 1)
                    properties[name] = nonNull[0];
            }

            // 응답으로만 쓰는 스키마에서는 생략되지 않는 속성이 언제나 온다 — C# `required` 가 아니어도(기본값 있는 속성)
            // 서버는 늘 쓴다. 생성기는 C# `required` 만 required 로 적어, 생성된 클라이언트가 늘 오는 값을 «없을 수도»로 본다.
            // 요청 본문에 닿는 스키마는 기본값이 곧 «생략 가능»이라 그대로 둔다.
            var requestSchemas = RequestBodySchemaIds(document, schemas);
            foreach (var (id, schema) in schemas)
            {
                if (requestSchemas.Contains(id) || schema is not OpenApiSchema { Properties: { Count: > 0 } props } concrete)
                    continue;
                concrete.Required ??= new HashSet<string>();
                foreach (var name in props.Keys.Where(n => !omitted.ContainsKey((id, n))))
                    concrete.Required.Add(name);
            }
            return Task.CompletedTask;
        });
        return options;
    }

    /// <summary>요청 본문이 (참조를 따라) 닿는 component 스키마 id 전부.</summary>
    private static HashSet<string> RequestBodySchemaIds(OpenApiDocument document, IDictionary<string, IOpenApiSchema> schemas)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        var stack = new Stack<IOpenApiSchema>(
            (document.Paths ?? []).Values
                .SelectMany(p => p.Operations?.Values ?? Enumerable.Empty<OpenApiOperation>())
                .SelectMany(o => o.RequestBody?.Content?.Values ?? [])
                .Select(m => m.Schema)
                .OfType<IOpenApiSchema>());
        while (stack.TryPop(out var schema))
        {
            if (schema is OpenApiSchemaReference { Reference.Id: { } refId })
            {
                if (found.Add(refId) && schemas.TryGetValue(refId, out var target))
                    stack.Push(target);
                continue;
            }
            foreach (var child in (schema.Properties?.Values ?? []).Concat(schema.OneOf ?? []).Concat(schema.AllOf ?? [])
                         .Concat(schema.AnyOf ?? []).Append(schema.Items).Append(schema.AdditionalProperties).OfType<IOpenApiSchema>())
                stack.Push(child);
        }
        return found;
    }

    private static bool OmittedFromWire(System.Text.Json.Serialization.Metadata.JsonPropertyInfo property) =>
        property.AttributeProvider?.GetCustomAttributes(typeof(System.Text.Json.Serialization.JsonIgnoreAttribute), inherit: true)
            .OfType<System.Text.Json.Serialization.JsonIgnoreAttribute>()
            .Any(a => a.Condition is System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
                or System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault) == true;
}
