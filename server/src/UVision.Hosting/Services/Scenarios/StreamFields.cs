using System.Text.RegularExpressions;
using UVision.Api.Models;

namespace UVision.Api.Services.Scenarios;

/// <summary>
/// 스트림 감시 필드를 요청과 저장된 시나리오에서 합친 모양 — 경계 검증과 저장이 같은 합침을 쓴다(<see cref="CountingFields"/> 와 같은 이유).
/// 생략하면 보존, 유형이 바뀌면 옮기지 않는다.
/// </summary>
internal readonly record struct StreamFields(List<ScenarioSource>? Sources, List<ScenarioRule>? Rules, List<ScenarioOutput>? Outputs)
{
    public static StreamFields Merge(ScenarioInput input, Scenario? existing, bool typeChanged)
    {
        var kept = typeChanged ? null : existing;
        return new(input.Sources ?? kept?.Sources, input.Rules ?? kept?.Rules, input.Outputs ?? kept?.Outputs);
    }
}

/// <summary>
/// 스트림 감시 어휘의 경계 검증(원장 D-105 W-4 · W-5) — 지어진 값만 받고 모순된 구성을 거부한다(변형하지 않는다).
/// 위반이면 사람이 읽을 사유, 아니면 null.
/// </summary>
internal static class StreamValidator
{
    public const int MaxSources = 16;
    public const int MaxShapesPerSource = 16;
    public const int MaxRules = 64;
    public const int MaxPolygonPoints = 64;
    public const double MaxSeconds = 86_400;
    public const int MaxFrames = 100_000;
    public const int MaxCount = 100_000;

    private static readonly Regex Id = new(@"^[A-Za-z0-9_-]{1,64}\z", RegexOptions.Compiled);

    // 별칭 — URL 모양(':' '/' '@')과 공백을 뺀다. 실제 주소 · 비밀번호는 노드 안에서만 풀리므로 시나리오에 닿으면 안 된다.
    private static readonly Regex CameraRef = new(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,63}\z", RegexOptions.Compiled);

    // 세는 종류는 검출기가 내는 라벨이라 판정 클래스 id 보다 넓게 받는다 — 줄을 끊거나 앞뒤가 흐린 값만 거른다.
    private static readonly Regex ClassLabel = new(@"^(?=\S)[^\p{C}]{1,64}(?<=\S)\z", RegexOptions.Compiled);

    public static string? Validate(StreamFields f)
    {
        if (f.Sources is not { Count: > 0 } sources)
            return "monitoring 시나리오는 sources 가 필수다(카메라 소스 1개 이상)";
        if (f.Rules is not { Count: > 0 } rules)
            return "monitoring 시나리오는 rules 가 필수다(규칙 1개 이상)";
        if (sources.Count > MaxSources) return $"sources 는 {MaxSources}개 이하";
        if (rules.Count > MaxRules) return $"rules 는 {MaxRules}개 이하";

        var sourceMap = new Dictionary<string, ScenarioSource>(StringComparer.Ordinal);
        foreach (var s in sources)
        {
            if (!Id.IsMatch(s.Id ?? "")) return "sources[].id 는 영숫자 · '_' · '-' 1~64자";
            if (!sourceMap.TryAdd(s.Id!, s)) return $"sources[].id 가 겹친다: {s.Id}";
            if (!CameraRef.IsMatch(s.CameraRef ?? ""))
                return $"sources[{s.Id}].camera_ref 는 노드 로컬 별칭(영숫자로 시작하는 영숫자 · '.' · '_' · '-' 1~64자)이다 — 카메라 주소 · 비밀번호는 시나리오에 적지 않는다";
            var shapeError = ValidateShapes(s);
            if (shapeError is not null) return shapeError;
        }

        var ruleMap = new Dictionary<string, ScenarioRule>(StringComparer.Ordinal);
        foreach (var r in rules)
        {
            if (!Id.IsMatch(r.Id ?? "")) return "rules[].id 는 영숫자 · '_' · '-' 1~64자";
            if (!ruleMap.TryAdd(r.Id!, r)) return $"rules[].id 가 겹친다: {r.Id}";
            var error = ValidateRule(r, sourceMap);
            if (error is not null) return error;
        }

        return ValidateOutputs(f.Outputs, ruleMap);
    }

    private static string? ValidateShapes(ScenarioSource s)
    {
        var zones = s.Zones ?? [];
        var lines = s.Lines ?? [];
        if (zones.Count > MaxShapesPerSource || lines.Count > MaxShapesPerSource)
            return $"sources[{s.Id}] 의 zones · lines 는 각각 {MaxShapesPerSource}개 이하";

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var z in zones)
        {
            if (!Id.IsMatch(z.Id ?? "")) return $"sources[{s.Id}].zones[].id 는 영숫자 · '_' · '-' 1~64자";
            if (!ids.Add(z.Id!)) return $"sources[{s.Id}] 의 zones 가 겹친다: {z.Id}";
            if (z.Polygon is not { Count: >= 3 and <= MaxPolygonPoints } polygon || polygon.Any(p => !IsPoint(p)))
                return $"sources[{s.Id}].zones[{z.Id}].polygon 은 [x, y] 꼭짓점 3~{MaxPolygonPoints}개이고 좌표는 0~1";
            if (PolygonArea(polygon) < 1e-9)
                return $"sources[{s.Id}].zones[{z.Id}].polygon 의 넓이가 0 이다(꼭짓점이 한 줄 위에 있다)";
        }
        ids.Clear();
        foreach (var l in lines)
        {
            if (!Id.IsMatch(l.Id ?? "")) return $"sources[{s.Id}].lines[].id 는 영숫자 · '_' · '-' 1~64자";
            if (!ids.Add(l.Id!)) return $"sources[{s.Id}] 의 lines 가 겹친다: {l.Id}";
            if (!IsPoint(l.A) || !IsPoint(l.B))
                return $"sources[{s.Id}].lines[{l.Id}] 의 a · b 는 [x, y] 이고 좌표는 0~1";
            if (l.A[0] == l.B[0] && l.A[1] == l.B[1])
                return $"sources[{s.Id}].lines[{l.Id}] 의 a 와 b 가 같다";
        }
        return null;
    }

    private static string? ValidateRule(ScenarioRule r, Dictionary<string, ScenarioSource> sources)
    {
        if (!ScenarioVocabulary.RuleKinds.Contains(r.Kind ?? ""))
            return $"rules[{r.Id}].kind 는 아직 {string.Join(", ", ScenarioVocabulary.RuleKinds)} 만 지원한다: {r.Kind}";
        if (!sources.TryGetValue(r.Source ?? "", out var source))
            return $"rules[{r.Id}].source 가 sources 에 없다: {r.Source}";
        if (r.Class is null || !ClassLabel.IsMatch(r.Class))
            return $"rules[{r.Id}].class 는 앞뒤 공백 · 제어문자 없는 1~64자 종류 이름";
        if (r.Zone is not null && r.Line is not null)
            return $"rules[{r.Id}] 는 zone 과 line 을 함께 줄 수 없다";
        if (r.Zone is not null && !(source.Zones ?? []).Any(z => z.Id == r.Zone))
            return $"rules[{r.Id}].zone 이 sources[{source.Id}].zones 에 없다: {r.Zone}";
        if (r.Line is not null && !(source.Lines ?? []).Any(l => l.Id == r.Line))
            return $"rules[{r.Id}].line 이 sources[{source.Id}].lines 에 없다: {r.Line}";

        return r.Kind == ScenarioVocabulary.CountRule ? ValidateCount(r) : ValidateAlarm(r);
    }

    private static string? ValidateCount(ScenarioRule r)
    {
        if (r.MinCount is not null || r.MaxCount is not null || r.ForSeconds is not null || r.ClearSeconds is not null)
            return $"rules[{r.Id}] 는 count 규칙이라 min_count · max_count · for_seconds · clear_seconds 를 줄 수 없다(alarm 의 것이다)";
        if (r.Line is not null)
        {
            // 선 통과는 순간이라 안정 구간이 뜻이 없다.
            return r.StableFrames is not null || r.StableSeconds is not null
                ? $"rules[{r.Id}] 는 line 을 세는 규칙이라 stable_frames · stable_seconds 를 줄 수 없다(통과는 순간이다)"
                : null;
        }
        if (r.StableFrames is not null && r.StableSeconds is not null)
            return $"rules[{r.Id}] 는 stable_frames 와 stable_seconds 를 함께 줄 수 없다 — 안정 구간은 프레임 수나 시간 하나로 정한다";
        if (r.StableFrames is < 1 or > MaxFrames)
            return $"rules[{r.Id}].stable_frames 는 1~{MaxFrames}";
        if (r.StableSeconds is { } t && !(t > 0 && t <= MaxSeconds))
            return $"rules[{r.Id}].stable_seconds 는 0 초과 {MaxSeconds:0} 이하";
        return null;
    }

    private static string? ValidateAlarm(ScenarioRule r)
    {
        if (r.Line is not null)
            return $"rules[{r.Id}] 는 alarm 규칙이라 line 을 줄 수 없다(구역 안 개수 조건이다)";
        if (r.StableFrames is not null || r.StableSeconds is not null)
            return $"rules[{r.Id}] 는 alarm 규칙이라 stable_frames · stable_seconds 를 줄 수 없다(count 의 것이다) — for_seconds 로 지속 시간을 준다";
        if ((r.MinCount is null) == (r.MaxCount is null))
            return $"rules[{r.Id}] 는 min_count(이상일 때 경보) 와 max_count(이하일 때 경보) 중 정확히 하나를 줘야 한다 — 존재는 min_count 1, 부재는 max_count 0";
        if (r.MinCount is < 1 or > MaxCount)
            return $"rules[{r.Id}].min_count 는 1~{MaxCount}(0 이상은 항상 참이다)";
        if (r.MaxCount is < 0 or > MaxCount)
            return $"rules[{r.Id}].max_count 는 0~{MaxCount}";
        if (r.ForSeconds is not { } f || !(f > 0 && f <= MaxSeconds))
            return $"rules[{r.Id}].for_seconds 는 필수이고 0 초과 {MaxSeconds:0} 이하";
        if (r.ClearSeconds is { } c && !(c > 0 && c <= MaxSeconds))
            return $"rules[{r.Id}].clear_seconds 는 0 초과 {MaxSeconds:0} 이하";
        return null;
    }

    private static string? ValidateOutputs(List<ScenarioOutput>? outputs, Dictionary<string, ScenarioRule> rules)
    {
        if (outputs is null) return null;
        if (outputs.Count == 0) return "outputs 가 비었다 — 생략하면 규칙이 낼 수 있는 모든 사건을 보낸다";
        var seen = new HashSet<(string, string?)>();
        foreach (var o in outputs)
        {
            if (!ScenarioVocabulary.Events.Contains(o.Event ?? ""))
                return $"outputs[].event 는 {string.Join(", ", ScenarioVocabulary.Events)} 중 하나: {o.Event}";
            if (!seen.Add((o.Event!, o.Rule))) return $"outputs 가 겹친다: {o.Event}{(o.Rule is null ? "" : $" ({o.Rule})")}";
            if (o.Event == ScenarioVocabulary.SourceState)
            {
                if (o.Rule is not null) return "outputs 의 source_state 는 규칙이 아니라 소스가 내므로 rule 을 줄 수 없다";
                continue;
            }
            if (o.Rule is null) continue;
            if (!rules.TryGetValue(o.Rule, out var rule)) return $"outputs[].rule 이 rules 에 없다: {o.Rule}";
            if (!ScenarioVocabulary.EventsOf(rule.Kind).Contains(o.Event!))
                return $"rules[{rule.Id}] 는 {rule.Kind} 규칙이라 {o.Event} 사건을 내지 않는다";
        }
        return null;
    }

    private static bool IsPoint(List<double>? p) =>
        p is { Count: 2 } && p.All(v => double.IsFinite(v) && v is >= 0 and <= 1);

    // 신발끈 공식 — 넓이가 0 인 퇴화 다각형(한 줄 위의 꼭짓점)만 거른다. 자기 교차는 따지지 않는다(짝-홀 규칙으로 안/밖이 정해진다).
    private static double PolygonArea(List<List<double>> p)
    {
        double sum = 0;
        for (var i = 0; i < p.Count; i++)
        {
            var a = p[i];
            var b = p[(i + 1) % p.Count];
            sum += a[0] * b[1] - b[0] * a[1];
        }
        return Math.Abs(sum) / 2;
    }
}
