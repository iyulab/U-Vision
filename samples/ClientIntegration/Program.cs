using Microsoft.Kiota.Abstractions;
using UVision.Client;
using UVision.Client.Models;

// Usage: dotnet run -- <engine-url> <image> [scenario-id] [expected-part]
//   engine-url     where the engine server is reached, e.g. http://localhost:8080
//   scenario-id    omitted: the scenarios on the engine are listed
//   expected-part  for a part verification scenario
if (args.Length < 2)
{
    Console.Error.WriteLine("usage: dotnet run -- <engine-url> <image> [scenario-id] [expected-part]");
    return 2;
}

using var http = new HttpClient();
var engine = UVisionClient.Create(http, new Uri(args[0]));

if (args.Length < 3)
{
    var scenarios = await engine.Scenarios.GetAsync() ?? [];
    if (scenarios.Count == 0)
        Console.WriteLine("No scenarios yet - create one in the admin console (/u-vision/, Admin tab).");
    foreach (var s in scenarios)
        Console.WriteLine($"{s.ScenarioId}\t{s.Type ?? "inspection"}\t{s.Name}");
    return 0;
}

var form = new MultipartBody();
await using var image = File.OpenRead(args[1]);
form.AddOrReplacePart("image", ContentTypeOf(args[1]), image, Path.GetFileName(args[1]));
form.AddOrReplacePart("scenario_id", "text/plain", args[2]);
if (args.Length > 3)
    form.AddOrReplacePart("expected_part", "text/plain", args[3]);

try
{
    var result = await engine.V2.Inspect.PostAsync(form);
    Console.WriteLine($"class: {result?.Class} (settled by {result?.SettledBy?.Tier}, confidence {result?.Confidence})");
    if (result?.RequiresReview == true)
        Console.WriteLine("held for review" + (result.Posture == "review_block" ? " - confirm before the next item" : ""));
    if (!string.IsNullOrEmpty(result?.Findings))
        Console.WriteLine(result.Findings);
    return 0;
}
catch (DetectionUnavailableResponse unavailable)
{
    // The engine could not decide (for example, the vision model is down). It keeps no record; send the image again later.
    Console.Error.WriteLine($"no decision: {unavailable.Reason}");
    return 1;
}
catch (ProblemDetails problem)
{
    Console.Error.WriteLine($"rejected ({problem.ResponseStatusCode}): {problem.Detail ?? problem.Title}");
    return 1;
}

// The engine takes JPEG and PNG.
static string ContentTypeOf(string path) =>
    Path.GetExtension(path).Equals(".png", StringComparison.OrdinalIgnoreCase) ? "image/png" : "image/jpeg";
