using System.Net;
using System.Net.Http.Json;
using System.Net.ServerSentEvents;
using System.Text;
using System.Text.Json;
using GraphFlow.Hosting.Demo;
using GraphFlow.Hosting.Demo.Models;
using GraphFlow.Hosting;
using GraphFlow.util;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

public class GraphHttpTests
{
    [Fact]
    public async Task StreamLargerThanQueueCapacityLosesNoEvents()
    {
        await using var host = new Host(); await host.Start();
        using var response = await host.Stream("long");
        long sequence = 0;
        var ends = 0;
        await foreach (var item in SseParser.Create(await response.Content.ReadAsStreamAsync()).EnumerateAsync())
        {
            using var data = JsonDocument.Parse(item.Data);
            if (item.EventType == "progress")
                Assert.Equal(++sequence, data.RootElement.GetProperty("sequence").GetInt64());
            else
            {
                Assert.Equal("end", item.EventType);
                ends++;
                Assert.True(data.RootElement.GetProperty("succeeded").GetBoolean());
                Assert.Equal(100, data.RootElement.GetProperty("state").GetProperty("nodeCount").GetInt32());
            }
        }
        Assert.Equal(399, sequence);
        Assert.Equal(1, ends);
    }
    [Fact]
    public async Task EmbeddedUiUsesConfiguredApiAndServesOnlyKnownAssets()
    {
        await using var host = new Host(); await host.Start();
        using var response = await host.Client.GetAsync("/explorer");
        Assert.EndsWith("/explorer/", response.RequestMessage!.RequestUri!.AbsolutePath);
        Assert.Equal("text/html", response.Content.Headers.ContentType!.MediaType);
        Assert.Contains("./app.js", await response.Content.ReadAsStringAsync());
        using var config = JsonDocument.Parse(await host.Client.GetStringAsync("/explorer/config"));
        Assert.Equal("/custom", config.RootElement.GetProperty("apiPrefix").GetString());
        foreach (var asset in new[] { "app.js", "sse.mjs", "style.css" })
        {
            using var file = await host.Client.GetAsync("/explorer/" + asset);
            Assert.Equal(HttpStatusCode.OK, file.StatusCode);
            Assert.NotEmpty(await file.Content.ReadAsStringAsync());
        }
        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.GetAsync("/explorer/secret.txt")).StatusCode);
    }

    [Fact]
    public async Task UiPreservesApplicationPathBase()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        app.UsePathBase("/mounted"); app.MapGraphFlowUI("/explorer", "/custom");
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        using var client = new HttpClient { BaseAddress = new Uri(address) };
        using var page = await client.GetAsync("/mounted/explorer");
        Assert.EndsWith("/mounted/explorer/", page.RequestMessage!.RequestUri!.AbsolutePath);
        using var config = JsonDocument.Parse(await client.GetStringAsync("/mounted/explorer/config"));
        Assert.Equal("/mounted/custom", config.RootElement.GetProperty("apiPrefix").GetString());
    }

    [Fact]
    public async Task SlowDemoStreamsEachNodeAndEdgePauseAndPreservesResult()
    {
        await using var host = new Host(); await host.Start();
        // Discovery constructs wrappers but never sleeps or executes them.
        var details = await host.Client.GetFromJsonAsync<GraphDetails>("/custom/graphs/slow-example");
        Assert.Equal(5, details!.Nodes.Count);
        Assert.Equal(5, details.Edges.Count);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        using var response = await host.Stream("slow-example", true);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var started = new Dictionary<string, TimeSpan>();
        var pauses = 0;
        var progressCount = 0;
        GraphRunResult? result = null;
        await foreach (var item in SseParser.Create(await response.Content.ReadAsStreamAsync()).EnumerateAsync(timeout.Token))
        {
            if (item.EventType == "end")
            {
                Assert.Null(result);
                result = JsonSerializer.Deserialize<GraphRunResult>(item.Data, JsonSerializerOptions.Web);
                continue;
            }
            var progress = JsonSerializer.Deserialize<GraphProgress>(item.Data, JsonSerializerOptions.Web)!;
            progressCount++;
            Assert.Equal(progressCount, progress.Sequence);
            if (progress.EventType is "NodeStart" or "EdgeStart")
                started[progress.ObjectName] = watch.Elapsed;
            if (progress.EventType is "NodeComplete" or "EdgeComplete")
            {
                // If the server buffered the whole run, these pairs would arrive together.
                Assert.True(watch.Elapsed - started[progress.ObjectName] >= TimeSpan.FromSeconds(0.8),
                    $"Expected a visible pause for {progress.ObjectName}.");
                pauses++;
            }
        }
        Assert.Equal(14, pauses); // Seven node executions and seven edge evaluations, including a rejected edge.
        Assert.Equal(29, progressCount);
        Assert.NotNull(result);
        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(7, result.State!.Value.GetProperty("nodeCount").GetInt32());
        Assert.Equal("Node1RunNode1Run", result.State.Value.GetProperty("nodeOutputs").GetProperty("Node1").GetString());
        Assert.Equal("Node2DidTheThingNode2DidNotDoTheThing", result.State.Value.GetProperty("nodeOutputs").GetProperty("Node2").GetString());
    }
    private sealed class Host : IAsyncDisposable
    {
        public WebApplication App { get; }
        public HttpClient Client { get; private set; } = null!;
        public ManualResetEventSlim Release { get; } = new(false);
        public TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Host()
        {
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
            builder.Services.AddScoped<ExampleGraph>();
            builder.Services.AddScoped<SlowExampleGraph>();
            builder.Services.AddGraphFlowHosting()
                .AddGraph<ExampleGraphStateObject>("example", sp => sp.GetRequiredService<ExampleGraph>().GetGraph())
                .AddGraph<ExampleGraphStateObject>("slow-example", sp => sp.GetRequiredService<SlowExampleGraph>().GetGraph())
                .AddGraph<ExampleGraphStateObject>("long", sp =>
                {
                    var graph = sp.GetRequiredService<GraphBuilder<ExampleGraphStateObject>>().GetExecutableGraph();
                    for (var i = 0; i < 100; i++)
                    {
                        graph.AddNode($"node{i}", s => { s.NodeCount++; return s; });
                        if (i > 0) graph.AddEdge($"node{i - 1}", $"node{i}");
                    }
                    graph.SetStartNode("node0");
                    return graph;
                })
                .AddGraph<ExampleGraphStateObject>("paused", sp =>
                {
                    var graph = sp.GetRequiredService<GraphBuilder<ExampleGraphStateObject>>().GetExecutableGraph();
                    graph.AddNode("pause", s =>
                    {
                        if (!Release.Wait(TimeSpan.FromSeconds(15))) throw new TimeoutException();
                        s.NodeCount++;
                        Finished.TrySetResult();
                        return s;
                    });
                    graph.SetStartNode("pause");
                    return graph;
                })
                .AddGraph<ExampleGraphStateObject>("failure", sp =>
                {
                    var graph = sp.GetRequiredService<GraphBuilder<ExampleGraphStateObject>>().GetExecutableGraph();
                    graph.AddNode("fail", _ => throw new Exception("private error"));
                    graph.SetStartNode("fail");
                    return graph;
                });
            App = builder.Build();
            App.MapGraphFlowHosting("/custom");
            App.MapGraphFlowUI("/explorer", "/custom");
            App.MapGet("/existing", () => "still here");
        }
        public async Task Start()
        {
            await App.StartAsync();
            var address = App.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            Client = new HttpClient { BaseAddress = new Uri(address), Timeout = TimeSpan.FromSeconds(20) };
        }
        public async ValueTask DisposeAsync()
        {
            Release.Set();
            Client?.Dispose();
            await App.DisposeAsync();
            Release.Dispose();
        }
        public Task<HttpResponseMessage> Stream(string name, bool loop = false) => Client.SendAsync(
            new HttpRequestMessage(HttpMethod.Post, $"/custom/graphs/{name}/runs/stream")
            { Content = JsonContent.Create(new { nodeOutputs = new { }, nodeCount = 0, shouldDoTheThing = loop }) },
            HttpCompletionOption.ResponseHeadersRead);
    }

    [Fact]
    public async Task DiscoveryAndValidationWorkAlongsideExistingApi()
    {
        await using var host = new Host(); await host.Start();
        Assert.Equal("still here", await host.Client.GetStringAsync("/existing"));
        var graphs = await host.Client.GetFromJsonAsync<GraphDescription[]>("/custom/graphs");
        Assert.Equal(5, graphs!.Length);
        var graph = await host.Client.GetFromJsonAsync<GraphDetails>("/custom/graphs/example");
        Assert.Equal("ExampleGraphStateObject", graph!.StateType);
        Assert.Equal(5, graph.Nodes.Count);
        Assert.Equal(5, graph.Edges.Count);
        Assert.Equal("Node1", graph.StartNode);
        Assert.True(graph.InputSchema.TryGetProperty("properties", out _));
        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.GetAsync("/custom/graphs/unknown")).StatusCode);
        foreach (var suffix in new[] { "runs", "runs/stream" })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await host.Client.PostAsJsonAsync($"/custom/graphs/unknown/{suffix}", new { })).StatusCode);
            foreach (var invalid in new[] { "null", "\"wrong\"", "{broken", "{\"nodeCount\":\"wrong\"}" })
            {
                using var response = await host.Client.PostAsync($"/custom/graphs/example/{suffix}", new StringContent(invalid, Encoding.UTF8, "application/json"));
                Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            }
        }
    }

    [Theory]
    [InlineData(false, 3, 11)]
    [InlineData(true, 7, 29)]
    public async Task StreamingAndJsonAgree(bool loop, int count, int progressCount)
    {
        await using var host = new Host(); await host.Start();
        using var jsonResponse = await host.Client.PostAsJsonAsync("/custom/graphs/example/runs", new { shouldDoTheThing = loop });
        Assert.Equal(HttpStatusCode.OK, jsonResponse.StatusCode);
        var normal = (await jsonResponse.Content.ReadFromJsonAsync<GraphRunResult>())!;
        using var response = await host.Stream("example", loop);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType!.MediaType);
        var events = new List<SseItem<string>>();
        await foreach (var item in SseParser.Create(await response.Content.ReadAsStreamAsync()).EnumerateAsync()) events.Add(item);
        Assert.Equal(progressCount + 1, events.Count);
        Assert.Equal("end", events[^1].EventType);
        Assert.Single(events, e => e.EventType == "end");
        var result = JsonSerializer.Deserialize<GraphRunResult>(events[^1].Data, JsonSerializerOptions.Web)!;
        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(count, result.State!.Value.GetProperty("nodeCount").GetInt32());
        Assert.Equal(normal.State!.Value.ToString(), result.State.Value.ToString());
        Assert.NotNull(normal.Execution);
        Assert.Equal(JsonSerializer.Serialize(normal.Execution), JsonSerializer.Serialize(result.Execution));
        var progress = events.Take(progressCount).Select(e => JsonSerializer.Deserialize<GraphProgress>(e.Data, JsonSerializerOptions.Web)!).ToArray();
        Assert.All(progress, e => Assert.Equal(result.RunId, e.RunId));
        Assert.Equal(Enumerable.Range(1, progressCount).Select(i => (long)i), progress.Select(e => e.Sequence));
        Assert.Equal(0, progress[0].State.GetProperty("nodeCount").GetInt32());
    }

    [Fact]
    public async Task ProgressArrivesBeforeCompletionAndConcurrentRequestsStayIsolated()
    {
        await using var host = new Host(); await host.Start();
        using var response = await host.Stream("paused");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var events = SseParser.Create(await response.Content.ReadAsStreamAsync()).EnumerateAsync(timeout.Token).GetAsyncEnumerator();
        try
        {
            Assert.True(await events.MoveNextAsync());
            Assert.Equal("progress", events.Current.EventType);
            Assert.False(host.Finished.Task.IsCompleted);
            var pausedId = JsonDocument.Parse(events.Current.Data).RootElement.GetProperty("runId").GetGuid();
            using var other = await host.Stream("example", true);
            await foreach (var item in SseParser.Create(await other.Content.ReadAsStreamAsync()).EnumerateAsync(timeout.Token))
                Assert.NotEqual(pausedId, JsonDocument.Parse(item.Data).RootElement.GetProperty("runId").GetGuid());
            host.Release.Set();
            var ends = 0;
            while (await events.MoveNextAsync()) if (events.Current.EventType == "end") ends++;
            Assert.Equal(1, ends);
        }
        finally { host.Release.Set(); }
    }

    [Fact]
    public async Task FailureHasMatchingResultAndOneTerminalEvent()
    {
        await using var host = new Host(); await host.Start();
        using var normal = await host.Client.PostAsJsonAsync("/custom/graphs/failure/runs", new { });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, normal.StatusCode);
        using var response = await host.Stream("failure");
        var ends = new List<GraphRunResult>();
        await foreach (var item in SseParser.Create(await response.Content.ReadAsStreamAsync()).EnumerateAsync())
            if (item.EventType == "end") ends.Add(JsonSerializer.Deserialize<GraphRunResult>(item.Data, JsonSerializerOptions.Web)!);
        Assert.Single(ends);
        Assert.False(ends[0].Succeeded);
        Assert.DoesNotContain("private error", ends[0].Error!);
    }

    [Fact]
    public async Task DisconnectDoesNotCancelGraphExecution()
    {
        await using var host = new Host(); await host.Start();
        using (var response = await host.Stream("paused"))
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await using var events = SseParser.Create(await response.Content.ReadAsStreamAsync()).EnumerateAsync(timeout.Token).GetAsyncEnumerator();
            Assert.True(await events.MoveNextAsync());
        }
        host.Release.Set();
        await host.Finished.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("still here", await host.Client.GetStringAsync("/existing"));
    }
}









