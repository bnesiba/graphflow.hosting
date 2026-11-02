using GraphFlow.models;

namespace GraphFlow.Hosting;

/// <summary>A scoped graph definition supplying its public name, metadata, input example, and graph.</summary>
/// <remarks>Metadata is snapshotted in a temporary scope when the catalog is first resolved. GetGraph must return a fresh graph.</remarks>
public interface IHostedGraph<TState> where TState : class
{
    string Name { get; }
    string? DisplayName { get; }
    string? Description { get; }
    TState? ExampleInput { get; }
    ExecutableGraph<TState> GetGraph();
}
