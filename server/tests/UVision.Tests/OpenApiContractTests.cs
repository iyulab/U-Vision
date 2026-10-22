using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace UVision.Tests;

/// <summary>
/// API 계약 문서(<c>/openapi/v1.json</c>)가 저장소의 정본 <c>server/openapi/u-vision.json</c> 과 같은지 본다 —
/// 엔드포인트 · DTO 를 바꾸면 이 테스트가 깨지고, 그 diff 가 곧 계약 변경이다. 의도한 변경이면
/// <c>UPDATE_OPENAPI=1 dotnet test --filter OpenApiContractTests</c> 로 정본을 다시 쓴다.
/// </summary>
public class OpenApiContractTests
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    [Fact]
    public async Task Document_Matches_Committed_Contract()
    {
        var actual = await FetchDocumentAsync();
        var path = Path.Combine(ServerRoot(), "openapi", "u-vision.json");

        if (Environment.GetEnvironmentVariable("UPDATE_OPENAPI") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, actual);
        }

        Assert.True(File.Exists(path), $"정본 없음: {path} — UPDATE_OPENAPI=1 로 만든다");
        Assert.Equal(Normalize(await File.ReadAllTextAsync(path)), actual);
    }

    [Fact]
    public async Task Document_Carries_Wire_Names_And_Enums()
    {
        var doc = JsonNode.Parse(await FetchDocumentAsync())!;
        var schemas = doc["components"]!["schemas"]!;

        var inspect = schemas["InspectResponse"]!["properties"]!;
        Assert.NotNull(inspect["image_id"]);
        Assert.NotNull(inspect["requires_review"]);
        Assert.NotNull(schemas["InspectResultV2"]!["properties"]!["settled_by"]);

        Assert.Equal(["OK", "NG"], Enumerate(schemas["Verdict"]!["enum"]!));
        Assert.Equal(["shadow", "advisory", "co_primary", "ml_primary"], Enumerate(schemas["AuthorityStage"]!["enum"]!));

        var inspectOp = doc["paths"]!["/api/u-vision/inspect"]!["post"]!;
        Assert.Equal("Inspect", (string?)inspectOp["operationId"]);
        Assert.NotNull(inspectOp["responses"]!["503"]);
    }

    private static async Task<string> FetchDocumentAsync()
    {
        using var factory = new UVisionApiFactory();
        var client = factory.CreateClient();
        var body = await client.GetStringAsync("/openapi/v1.json");
        return Normalize(body);
    }

    // 줄바꿈 · 들여쓰기 차이(체크아웃 설정)로 깨지지 않게 다시 써서 비교한다.
    private static string Normalize(string json) =>
        JsonNode.Parse(json)!.ToJsonString(Indented).Replace("\r\n", "\n") + "\n";

    private static string[] Enumerate(JsonNode node) =>
        node.AsArray().Select(n => (string)n!).ToArray();

    private static string ServerRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "UVision.slnx")))
                return dir.FullName;
        throw new InvalidOperationException("UVision.slnx 를 찾지 못했다");
    }
}
