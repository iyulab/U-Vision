using UVision.Api.Services.Events;

namespace UVision.Api.Endpoints;

/// <summary>판정 사건 내보내기(웹훅) 상태 — 무인증 읽기(메트릭과 같은 급). 받는 곳 주소 · 비밀은 싣지 않는다.</summary>
public static class EventEndpoints
{
    public static void MapEventEndpoints(this RouteGroupBuilder parent)
    {
        parent.MapGet("/events/status", (WebhookStatusReader reader) => Results.Ok(reader.Read()))
            .WithName("GetEventsStatus")
            .Produces<EventsStatusResponse>();
    }
}
