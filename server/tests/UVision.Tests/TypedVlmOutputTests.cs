using UVision.Api.Models;
using UVision.Api.Services.Vlm;
using Xunit;

namespace UVision.Tests;

/// <summary>
/// 불량 유형을 나누는 VLM 출력 — 스키마가 선언한 유형만 받게 구속하고, 답의 유형을 판정에 싣는다. 유형이 없는
/// 시나리오의 프롬프트 · 파싱은 그대로다.
/// </summary>
public class TypedVlmOutputTests
{
    [Fact]
    public void TypedSchema_ConstrainsDefectClassToDeclaredIdsOrNull()
    {
        var schema = IronHiveVlmProvider.TypedSchema(["scratch", "dent"]).ToJsonString();

        Assert.Contains("\"defect_class\":{\"type\":[\"string\",\"null\"],\"enum\":[\"scratch\",\"dent\",null]}", schema);
        Assert.Contains("\"required\":[\"verdict\",\"defect_class\",\"findings\",\"confidence\"]", schema);
    }

    [Fact]
    public void ParseAnswer_Typed_CarriesTheDefectClass()
    {
        var r = IronHiveVlmProvider.ParseAnswer(
            """{"verdict":"NG","defect_class":"dent","findings":"찍힘","confidence":0.8}""", typed: true)!;

        Assert.Equal(Verdict.NG, r.Verdict);
        Assert.Equal("dent", r.DefectClass);
        Assert.Equal("찍힘", r.Findings);
    }

    [Fact]
    public void ParseAnswer_Binary_IgnoresAnyDefectClass()
    {
        var r = IronHiveVlmProvider.ParseAnswer(
            """{"verdict":"NG","defect_class":"dent","findings":"x","confidence":0.8}""", typed: false)!;

        Assert.Null(r.DefectClass);
    }

    [Fact]
    public void Prompt_ListsDefectTypesOnlyWhenDeclared()
    {
        var binary = VlmPrompt.BuildSystemPrompt(new ScenarioContext { ScenarioId = "s", Criteria = "c" });
        var typed = VlmPrompt.BuildSystemPrompt(new ScenarioContext
        {
            ScenarioId = "s", Criteria = "c", DefectClasses = [new() { Id = "scratch" }, new() { Id = "dent" }],
        });

        Assert.DoesNotContain("[불량 유형]", binary);
        Assert.Contains("scratch, dent", typed);
        Assert.StartsWith(binary, typed);
    }

    [Fact]
    public void Prompt_DescribesDefectTypesByDisplayName_AnswerStaysTheId()
    {
        var typed = VlmPrompt.BuildSystemPrompt(new ScenarioContext
        {
            ScenarioId = "s", Criteria = "c",
            DefectClasses = [new() { Id = "scratch", Label = "긁힘" }, new() { Id = "dent" }],
        });

        Assert.Contains("다음 id 중 하나로 답한다: scratch(긁힘), dent.", typed);
    }

    [Fact]
    public async Task Mock_NamesADeclaredDefectOnNg()
    {
        var mock = new MockVlmProvider();
        var context = new ScenarioContext { ScenarioId = "s", DefectClasses = [new() { Id = "scratch" }, new() { Id = "dent" }] };
        for (byte b = 0; b < 30; b++)
        {
            var r = await mock.InspectAsync(new byte[] { b, 1, 2 }, context);
            if (r.Verdict == Verdict.NG) Assert.Contains(r.DefectClass, context.DefectClasses.Select(c => c.Id));
            else Assert.Null(r.DefectClass);
        }
    }
}
