using System.Text.Json;

namespace GraphFlow.Hosting;

/// <summary>Lists registrations and inspects graph definitions without executing them.</summary>
public interface IGraphCatalog
{
    IReadOnlyList<HostedGraphInfo> Graphs { get; }
    GraphDetails Describe(string graphName);
}

/// <summary>Executes a named graph in its own dependency-injection scope.</summary>
public interface IGraphRunner
{
    GraphRunResult Run(string graphName, JsonElement input, Action<GraphProgress>? progress = null,
        Guid? runId = null);
}
