using System.Net;
using System.Net.Http.Json;
using System.Net.ServerSentEvents;
using System.Text.Json;
using GraphFlow.Hosting;
using GraphFlow.util;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.WebHost.UseUrls("http://127.0.0.1:0");
builder.Services.AddGraphFlowHosting().AddGraph<CounterGraph, CounterState>();
await using var app = builder.Build();
app.UsePathBase("/mounted");
app.MapGet("/existing", () => "existing API");
app.MapGraphFlowHosting("/api/flows");
app.MapGraphFlowUI("/tools/explorer", "/api/flows");
await app.StartAsync();
try
{
    var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
    using var client = new HttpClient { BaseAddress = new Uri(address), Timeout = TimeSpan.FromSeconds(15) };
    Check(await client.GetStringAsync("/mounted/existing") == "existing API", "existing endpoint");
    var catalog = app.Services.GetRequiredService<IGraphCatalog>();
    Check(catalog.Graphs.Single().Name == "counter", "catalog interface");
    var run = app.Services.GetRequiredService<IGraphRunner>().Run("counter", JsonSerializer.SerializeToElement(new { value = 1 }));
    Check(run.Succeeded && run.State!.Value.GetProperty("value").GetInt32() == 2, "runner interface");
    var details = await client.GetFromJsonAsync<GraphDetails>("/mounted/api/flows/graphs/counter");
    Check(details!.Nodes.Single().Name == "increment", "graph details");
    using var response = await client.PostAsJsonAsync("/mounted/api/flows/graphs/counter/runs", new { value = 41 });
    var result = await response.Content.ReadFromJsonAsync<GraphRunResult>();
    Check(response.IsSuccessStatusCode && result!.Succeeded && result.State!.Value.GetProperty("value").GetInt32() == 42, "JSON execution");
    Check(result!.Execution!.Nodes.Single().Name == "increment" && result.Execution.Nodes.Single().Executions == 1, "post-run execution summary");
    using var streamResponse = await client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/mounted/api/flows/graphs/counter/runs/stream")
        { Content = JsonContent.Create(new { value = 41 }) }, HttpCompletionOption.ResponseHeadersRead);
    Check(streamResponse.Content.Headers.ContentType!.MediaType == "text/event-stream", "SSE content type");
    var progress = 0; var ends = 0;
    await foreach (var item in SseParser.Create(await streamResponse.Content.ReadAsStreamAsync()).EnumerateAsync())
    {
        if (item.EventType == "progress") progress++;
        if (item.EventType == "end")
        {
            ends++;
            var end = JsonSerializer.Deserialize<GraphRunResult>(item.Data, JsonSerializerOptions.Web)!;
            Check(end.Succeeded && end.State!.Value.GetProperty("value").GetInt32() == 42, "SSE result");
        }
    }
    Check(progress > 0 && ends == 1, "SSE event lifecycle");
    using var ui = await client.GetAsync("/mounted/tools/explorer");
    Check(ui.RequestMessage!.RequestUri!.AbsolutePath == "/mounted/tools/explorer/", "UI redirect");
    Check((await ui.Content.ReadAsStringAsync()).Contains("./app.js"), "embedded HTML");
    foreach (var file in new[] { "app.js", "sse.mjs", "style.css" })
        Check((await client.GetStringAsync("/mounted/tools/explorer/" + file)).Length > 100, "embedded " + file);
    using var config = JsonDocument.Parse(await client.GetStringAsync("/mounted/tools/explorer/config"));
    Check(config.RootElement.GetProperty("apiPrefix").GetString() == "/mounted/api/flows", "UI mount configuration");
    var library = typeof(IGraphRunner).Assembly;
    Check(!library.GetTypes().Any(t => t.Name.Contains("ExampleGraph") || t.Namespace?.Contains(".Demo") == true), "no demo types in library");
    Check(library.GetManifestResourceNames().Length == 4, "all four embedded UI assets");
    Console.WriteLine("Package consumer passed: interfaces, discovery, JSON, SSE, embedded UI, custom routes, PathBase, existing API, and no demo types.");
}
finally { await app.StopAsync(); }

static void Check(bool condition, string check)
{
    if (!condition) throw new InvalidOperationException("Package validation failed: " + check);
}
public sealed class CounterState { public int Value { get; set; } }
public sealed class CounterGraph(GraphBuilder<CounterState> builder) : IHostedGraph<CounterState>
{
    public string Name => "counter";
    public string DisplayName => "Counter";
    public string Description => "A graph defined by the consuming application.";
    public CounterState ExampleInput => new();
    public GraphFlow.models.ExecutableGraph<CounterState> GetGraph()
    {
        var graph = builder.GetExecutableGraph();
        graph.AddNode("increment", state => { state.Value++; return state; });
        graph.SetStartNode("increment");
        return graph;
    }
}

