using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using UVision.Api;
using UVision.Api.Auth;
using Xunit;

namespace UVision.Tests;

/// <summary>201 <c>Location</c> 은 호스트가 정한 API 기본 경로를 따른다(문자열로 박힌 기본값이 아니라).</summary>
public class ApiLocationTests
{
    private sealed class CustomBaseFactory : UVisionApiFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting($"{UVisionOptions.SectionName}:ApiBasePath", "/vision-api");
            builder.ConfigureTestServices(s => s.AddSingleton(new AdminPinOptions { Pin = PinFactory.Pin }));
        }
    }

    private static HttpClient Client(UVisionApiFactory factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Admin-Pin", PinFactory.Pin);
        return client;
    }

    [Fact]
    public async Task Created_Location_FollowsConfiguredApiBasePath()
    {
        using var factory = new CustomBaseFactory();
        var client = Client(factory);

        var scenario = await client.PostAsJsonAsync("/vision-api/scenarios", new { name = "loc" });
        Assert.Equal(HttpStatusCode.Created, scenario.StatusCode);
        var id = (await scenario.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("scenario_id").GetString();
        Assert.Equal($"/vision-api/scenarios/{id}", scenario.Headers.Location!.OriginalString);

        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent([0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10]);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        form.Add(file, "image", "ref.jpg");
        form.Add(new StringContent("ok"), "label");
        var reference = await client.PostAsync($"/vision-api/scenarios/{id}/references", form);
        Assert.Equal(HttpStatusCode.Created, reference.StatusCode);
        Assert.StartsWith($"/vision-api/scenarios/{id}/references/ok/", reference.Headers.Location!.OriginalString);

        var model = await client.PostAsJsonAsync($"/vision-api/scenarios/{id}/models", new { model_name = "m" });
        Assert.Equal(HttpStatusCode.Created, model.StatusCode);
        Assert.StartsWith($"/vision-api/scenarios/{id}/models/", model.Headers.Location!.OriginalString);
    }
}
