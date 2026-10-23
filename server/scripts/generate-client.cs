// Regenerates src/UVision.Client/Generated from the published contract openapi/u-vision.json.
// Usage (from server/): dotnet run scripts/generate-client.cs
//
// The contract lists every path under the default API base path. The client takes that base path as part of its
// base URL instead, so a host that mounts the engine under another base path (or behind a proxy prefix) needs no
// rewriting: the input handed to Kiota is the contract with the common prefix moved out of the paths.
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

const string Prefix = "/api/u-vision";
var server = Directory.GetCurrentDirectory();
var contract = Path.Combine(server, "openapi", "u-vision.json");
var output = Path.Combine(server, "src", "UVision.Client", "Generated");

var doc = JsonNode.Parse(File.ReadAllText(contract))!.AsObject();
var paths = doc["paths"]!.AsObject();
var relative = new JsonObject();
foreach (var (path, item) in paths.ToList())
{
    if (!path.StartsWith(Prefix + "/", StringComparison.Ordinal))
        throw new InvalidOperationException($"path outside {Prefix}: {path}");
    paths.Remove(path);
    relative[path[Prefix.Length..]] = item;
}
doc["paths"] = relative;
doc.Remove("servers");

// ASP.NET Core describes a form as allOf fragments, one per field. Kiota maps a multipart body to its MultipartBody
// type only when it sees one object schema, so the fragments are merged into one — the same fields and requirements.
foreach (var (_, item) in relative)
foreach (var (_, operation) in item!.AsObject())
{
    if (operation?["requestBody"]?["content"]?["multipart/form-data"]?["schema"] is not JsonObject schema
        || schema["allOf"] is not JsonArray fragments)
        continue;
    var properties = new JsonObject();
    foreach (var fragment in fragments)
    foreach (var (name, property) in fragment!["properties"]!.AsObject().ToList())
    {
        fragment["properties"]!.AsObject().Remove(name);
        properties[name] = property;
    }
    schema.Remove("allOf");
    schema["properties"] = properties;
}

var input = Path.Combine(Path.GetTempPath(), $"u-vision-client-input-{Environment.ProcessId}.json");
File.WriteAllText(input, doc.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
try
{
    var kiota = Process.Start(new ProcessStartInfo("dotnet",
    [
        "kiota", "generate",
        "--language", "CSharp",
        "--openapi", input,
        "--output", output,
        "--class-name", "UVisionApiClient",
        "--namespace-name", "UVision.Client",
        "--clean-output",
        "--exclude-backward-compatible",
        "--log-level", "Warning",
    ]) { WorkingDirectory = server })!;
    kiota.WaitForExit();
    // The lock file and the run log record the temporary input's absolute path and only serve `kiota update`; this
    // script is the regeneration path, so neither is kept.
    File.Delete(Path.Combine(output, "kiota-lock.json"));
    File.Delete(Path.Combine(output, ".kiota.log"));
    return kiota.ExitCode;
}
finally
{
    File.Delete(input);
}
