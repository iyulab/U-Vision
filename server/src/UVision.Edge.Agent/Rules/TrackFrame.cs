using System.Text.Json;

namespace UVision.Edge.Agent.Rules;

/// <summary>
/// 워커가 프레임마다 건네는 트랙 하나(원장 D-105 W-2) — 좌표는 프레임 기준 0~1(왼쪽 위 + 크기). 규칙은 «땅에 닿은 점» 인 상자의
/// <b>아래 중심</b>(<see cref="GroundX"/>, <see cref="GroundY"/>)으로 구역 안 · 선 통과를 판정한다 — 사람 · 지게차 · 상자 모두 바닥에서
/// 구역에 들어가고 선을 넘는다.
/// </summary>
public sealed record TrackSample(string TrackId, string Class, double Score, double X, double Y, double W, double H)
{
    public double GroundX => X + W / 2;

    public double GroundY => Y + H;
}

/// <summary>
/// 한 소스의 프레임 하나 — <see cref="Time"/> 은 프레임이 찍힌 벽시계 시각이다. 규칙의 «T 초 동안 안정» · «지속» 은 시계가 아니라
/// <b>이 시각</b>으로 잰다(파일 재생 · 가짜 워커로 시간을 마음대로 흘릴 수 있고, 처리가 늦어도 판정이 바뀌지 않는다).
/// </summary>
public sealed record TrackFrame(string SourceId, long Seq, DateTimeOffset Time, IReadOnlyList<TrackSample> Tracks);

/// <summary>규칙이 낸 사건 — 에이전트가 outbox 에 넣으면(<c>EnqueueEvent</c>) 엔진으로 간다. <see cref="Time"/> 은 사건을 일으킨 프레임의 시각.</summary>
public sealed record RuleEvent(string ScenarioId, string Kind, DateTimeOffset Time, JsonElement Data);
