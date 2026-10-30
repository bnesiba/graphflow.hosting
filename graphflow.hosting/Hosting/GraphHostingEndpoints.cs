using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;

namespace GraphFlow.Hosting;

public sealed record GraphDescription(string Name, string StateType, string? DisplayName = null, string? Description = null);

public static class GraphHostingEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    // Returning the group lets consuming applications apply authorization and other conventions.
    public static RouteGroupBuilder MapGraphFlowHosting(this IEndpointRouteBuilder endpoints,
        string prefix = "/graphflow")
    {
        var group = endpoints.MapGroup(prefix);
        group.MapGet("/graphs", (GraphRunner runner) =>
            runner.Graphs.Select(Describe));
        group.MapGet("/graphs/{name}", Details);
        group.MapPost("/graphs/{name}/runs", Execute);
        group.MapPost("/graphs/{name}/runs/stream", Stream);
        return group;
    }

    private static GraphDescription Describe(HostedGraphInfo graph) => new(graph.Name, graph.StateType.Name, graph.DisplayName, graph.Description);

    private static IResult Details(string name, GraphRunner runner, ILogger<GraphRunner> logger)
    {
        if (!runner.Graphs.Any(g => g.Name == name)) return Results.NotFound();
        try { return Results.Ok(runner.Describe(name)); }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unable to describe graph {GraphName}", name);
            return Results.Problem(statusCode: 500, title: "Unable to describe this graph.");
        }
    }

    private static IResult? Validate(GraphRunner runner, string name, JsonElement input)
    {
        var graph = runner.Graphs.FirstOrDefault(g => g.Name == name);
        if (graph is null) return Results.NotFound();
        try
        {
            if (input.Deserialize(graph.StateType, JsonOptions) is null)
                return Results.Problem(statusCode: 400, title: "Graph input must not be null.");
        }
        catch (JsonException)
        {
            return Results.Problem(statusCode: 400, title: "Input does not match the graph's state type.");
        }
        return null;
    }

    private static async Task<IResult> Execute(string name, JsonElement input, GraphRunner runner)
    {
        if (Validate(runner, name, input) is { } error) return error;
        var result = await Task.Run(() => runner.Run(name, input));
        return Results.Json(result, statusCode: result.Succeeded ? 200 : 422);
    }

    private static IResult Stream(string name, JsonElement input, GraphRunner runner)
    {
        if (Validate(runner, name, input) is { } error) return error;
        return TypedResults.ServerSentEvents(Events(runner, name, input.Clone()));
    }

    private static async IAsyncEnumerable<SseItem<JsonElement>> Events(GraphRunner runner,
        string name, JsonElement input, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Bound queued snapshots. A slow client applies backpressure to the synchronous producer.
        var channel = Channel.CreateBounded<SseItem<JsonElement>>(new BoundedChannelOptions(32)
        {
            SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait
        });
        using var delivery = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = delivery.Token;
        var runId = Guid.NewGuid();
        var producer = Task.Run(async () =>
        {
            try
            {
                var result = runner.Run(name, input, progress =>
                {
                    if (token.IsCancellationRequested) return;
                    try
                    {
                        var item = new SseItem<JsonElement>(JsonSerializer.SerializeToElement(progress, JsonOptions), "progress");
                        channel.Writer.WriteAsync(item, token).AsTask().GetAwaiter().GetResult();
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { }
                }, runId);
                await channel.Writer.WriteAsync(new SseItem<JsonElement>(
                    JsonSerializer.SerializeToElement(result, JsonOptions), "end"), token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception ex)
            {
                channel.Writer.TryComplete(ex);
            }
            finally { channel.Writer.TryComplete(); }
        });
        try
        {
            await foreach (var item in channel.Reader.ReadAllAsync(token)) yield return item;
            await producer;
        }
        finally
        {
            // Disconnect stops delivery, not graph execution. The runner owns its scope until it returns.
            await delivery.CancelAsync();
        }
    }
}

