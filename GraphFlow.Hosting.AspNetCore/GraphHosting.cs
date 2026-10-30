using System.Text.Json;
using System.Text.Json.Schema;
using System.Text.Json.Nodes;
using GraphFlow.models;
using GraphFlow.util;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace GraphFlow.Hosting;

public sealed record HostedGraphInfo(string Name, Type StateType, string? DisplayName = null, string? Description = null);
public sealed record GraphProgress(Guid RunId, Guid ExecutionId, long Sequence,
    string EventType, string ObjectName, Guid Checkpoint, JsonElement State);
public sealed record GraphRunResult(Guid RunId, string GraphName, bool Succeeded,
    JsonElement? State, string? Error)
{
    public GraphExecutionSummary? Execution { get; init; }
}

public static class HostingExtensions
{
    public static GraphHostingBuilder AddGraphFlowHosting(this IServiceCollection services)
    {
        services.TryAddSingleton(sp => new GraphRunner(sp.GetRequiredService<IServiceScopeFactory>(), sp.GetServices<GraphRegistration>()));
        services.TryAddSingleton<IGraphRunner>(sp => sp.GetRequiredService<GraphRunner>());
        services.TryAddSingleton<IGraphCatalog>(sp => sp.GetRequiredService<GraphRunner>());
        return new GraphHostingBuilder(services);
    }
}

public sealed class GraphHostingBuilder(IServiceCollection services)
{
    public GraphHostingBuilder AddGraph<T>(string name, Func<IServiceProvider, ExecutableGraph<T>> factory, GraphPresentation<T>? presentation = null)
        where T : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(factory);
        if (services.Any(d => d.ServiceType == typeof(GraphRegistration) &&
            d.ImplementationInstance is GraphRegistration r && r.Info.Name == name))
            throw new ArgumentException($"Graph '{name}' is already registered.", nameof(name));

        // Register the engine once per state type, including when the host already registered it.
        if (!services.Any(d => d.ServiceType == typeof(GraphBuilder<T>)))
            services.UseGraphFlow<T>();
        services.AddSingleton<GraphRegistration>(new GraphRegistration<T>(name, factory, presentation));
        return this;
    }
}

internal abstract class GraphRegistration(HostedGraphInfo info)
{
    public HostedGraphInfo Info { get; } = info;
    public abstract GraphDetails Describe(IServiceProvider services);
    public abstract GraphRunResult Run(IServiceProvider services, Guid runId, JsonElement input,
        Action<GraphProgress>? progress);
}

internal sealed class GraphRegistration<T>(string name, Func<IServiceProvider, ExecutableGraph<T>> factory, GraphPresentation<T>? presentation)
    : GraphRegistration(new HostedGraphInfo(name, typeof(T), presentation?.DisplayName, presentation?.Description)) where T : class
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly JsonElement? exampleInput = presentation?.ExampleInput is { } example
        ? JsonSerializer.SerializeToElement(example, JsonSerializerOptions.Web) : null;

    public override GraphDetails Describe(IServiceProvider services)
    {
        var graph = factory(services) ?? throw new InvalidOperationException("Graph factory returned null.");
        var nodes = graph.graphNodes.Values.ToArray();
        var names = nodes.Select(n => n.name).ToHashSet(StringComparer.Ordinal);
        if (names.Count != nodes.Length || names.Any(string.IsNullOrWhiteSpace))
            throw new InvalidOperationException("Node names must be nonempty and unique.");
        var edges = new List<GraphEdgeDescription>();
        foreach (var node in nodes)
        {
            for (var order = 0; order < node.edges.Count; order++)
            {
                var edge = node.edges[order];
                if (!nodes.Contains(edge.targetNode))
                    throw new InvalidOperationException("An edge references a node outside the graph.");
                edges.Add(new(edge.name, node.name, edge.targetNode.name, order));
            }
        }
        if (edges.Any(e => string.IsNullOrWhiteSpace(e.Name)) ||
            edges.Select(e => e.Name).Distinct(StringComparer.Ordinal).Count() != edges.Count)
            throw new InvalidOperationException("Edge names must be nonempty and unique.");
        if (graph.startNode is not null && !nodes.Contains(graph.startNode))
            throw new InvalidOperationException("Start node is outside the graph.");
        var schema = JsonSerializerOptions.Web.GetJsonSchemaAsNode(typeof(T));
        // The hosting input contract rejects null even if the serializer's reference-type schema allows it.
        var rootSchema = schema as JsonObject ?? new JsonObject { ["allOf"] = new JsonArray(schema) };
        rootSchema["not"] = new JsonObject { ["type"] = "null" };
        var inputSchema = JsonSerializer.SerializeToElement(rootSchema);
        return new(name, typeof(T).Name, Info.DisplayName, Info.Description, graph.startNode?.name,
            nodes.Select(n => new GraphNodeDescription(n.name)).ToArray(), edges, inputSchema, exampleInput);
    }

    public override GraphRunResult Run(IServiceProvider services, Guid runId, JsonElement input,
        Action<GraphProgress>? progress)
    {
        // Deserialize for each run so callers cannot accidentally share mutable input state.
        var state = input.Deserialize<T>(JsonOptions)
            ?? throw new ArgumentException("Graph input must not be null.", nameof(input));
        var graph = factory(services) ?? throw new InvalidOperationException("Graph factory returned null.");
        long sequence = 0;
        Exception? callbackError = null;
        var result = graph.StreamGraph(state, update =>
        {
            if (callbackError is not null) return;
            try
            {
                progress?.Invoke(new GraphProgress(runId, update.GraphExecutionId, ++sequence,
                    update.EventType.ToString(), update.GraphObjectName,
                    update.GraphRunState.CurrentCheckpoint,
                    JsonSerializer.SerializeToElement(update.GraphValue, JsonOptions)));
            }
            catch (Exception ex)
            {
                // The engine can swallow callback exceptions. Keep the failure visible to hosting
                // without injecting transport failures into graph execution.
                callbackError = ex;
            }
        });
        var metadata = result.GraphData;
        var succeeded = callbackError is null && metadata.GraphRuns.Count > 0 &&
            metadata.GraphRuns.Values.All(r => r.Succeeded == true) &&
            metadata.NodeRuns.Values.All(r => r.Succeeded == true) &&
            metadata.EdgeRuns.Values.All(r => r.Succeeded == true);
        return new GraphRunResult(runId, name, succeeded,
            JsonSerializer.SerializeToElement(result.StateObject, JsonOptions),
            callbackError is not null ? "Progress delivery failed." :
            succeeded ? null : "Graph execution failed; the engine may not provide error details.")
        {
            Execution = new GraphExecutionSummary(
                metadata.NodeRuns.Values.GroupBy(n => n.NodeName).Select(g =>
                    new NodeExecutionSummary(g.Key, g.Count(), g.Count(n => n.Succeeded == false),
                        g.Count(n => n.Succeeded is null))).ToArray(),
                metadata.EdgeRuns.Values.GroupBy(e => e.EdgeName).Select(g =>
                    new EdgeExecutionSummary(g.Key, g.Count(),
                        g.Count(e => e.Succeeded == true && e.Evaluation),
                        g.Count(e => e.Succeeded == true && !e.Evaluation),
                        g.Count(e => e.Succeeded == false), g.Count(e => e.Succeeded is null))).ToArray())
        };
    }
}

/// <summary>Synchronous execution boundary. Transport scheduling belongs to the API layer.</summary>
public sealed class GraphRunner : IGraphRunner, IGraphCatalog
{
    private readonly IServiceScopeFactory scopes;
    private readonly IReadOnlyDictionary<string, GraphRegistration> graphs;

    internal GraphRunner(IServiceScopeFactory scopes, IEnumerable<GraphRegistration> registrations)
    {
        this.scopes = scopes;
        graphs = registrations.ToDictionary(r => r.Info.Name, StringComparer.Ordinal);
    }

    public IReadOnlyList<HostedGraphInfo> Graphs => graphs.Values.Select(r => r.Info).ToArray();

    public GraphDetails Describe(string graphName)
    {
        if (!graphs.TryGetValue(graphName, out var graph))
            throw new KeyNotFoundException($"Graph '{graphName}' is not registered.");
        using var scope = scopes.CreateScope();
        return graph.Describe(scope.ServiceProvider);
    }

    public GraphRunResult Run(string graphName, JsonElement input, Action<GraphProgress>? progress = null,
        Guid? runId = null)
    {
        if (!graphs.TryGetValue(graphName, out var graph))
            throw new KeyNotFoundException($"Graph '{graphName}' is not registered.");
        var id = runId ?? Guid.NewGuid();
        using var scope = scopes.CreateScope();
        try
        {
            return graph.Run(scope.ServiceProvider, id, input, progress);
        }
        catch (Exception)
        {
            return new GraphRunResult(id, graphName, false, null,
                "Graph input, construction, or execution failed.");
        }
    }
}




