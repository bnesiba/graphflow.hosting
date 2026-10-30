using System.Text.Json;

namespace GraphFlow.Hosting;

public sealed record GraphPresentation<T>(string? DisplayName = null, string? Description = null, T? ExampleInput = null)
    where T : class;
public sealed record GraphNodeDescription(string Name);
public sealed record GraphEdgeDescription(string Name, string Source, string Target, int Order);
public sealed record GraphDetails(string Name, string StateType, string? DisplayName, string? Description,
    string? StartNode, IReadOnlyList<GraphNodeDescription> Nodes, IReadOnlyList<GraphEdgeDescription> Edges,
    JsonElement InputSchema, JsonElement? ExampleInput);
