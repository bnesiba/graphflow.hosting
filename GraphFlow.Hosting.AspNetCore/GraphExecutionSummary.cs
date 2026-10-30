namespace GraphFlow.Hosting;

/// <summary>Aggregated execution metadata keyed by stable graph-local names.</summary>
public sealed record GraphExecutionSummary(IReadOnlyList<NodeExecutionSummary> Nodes, IReadOnlyList<EdgeExecutionSummary> Edges);
public sealed record NodeExecutionSummary(string Name, int Executions, int Failures, int Incomplete);
public sealed record EdgeExecutionSummary(string Name, int Evaluations, int Followed, int Rejected, int Failures, int Incomplete);
