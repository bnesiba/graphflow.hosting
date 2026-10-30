using GraphFlow.Hosting.Demo.Models;
using GraphFlow.models;

namespace GraphFlow.Hosting.Demo;

/// <summary>A demo-only version of the example graph, paced for watching SSE progress.</summary>
public sealed class SlowExampleGraph(ExampleGraph example)
{
    public ExecutableGraph<ExampleGraphStateObject> GetGraph()
    {
        // Wrap a fresh graph so discovery remains immediate and the fast example is unchanged.
        var graph = example.GetGraph();
        foreach (var node in graph.graphNodes.Values)
        {
            var execute = node.nodeFunction;
            node.nodeFunction = state =>
            {
                Thread.Sleep(TimeSpan.FromSeconds(1));
                return execute(state);
            };
        }
        foreach (var edge in graph.graphEdges)
        {
            var evaluate = edge.evaluation;
            edge.evaluation = state =>
            {
                Thread.Sleep(TimeSpan.FromSeconds(1));
                return evaluate(state);
            };
        }
        return graph;
    }

    public static ExampleGraphStateObject ExampleInput() => new()
    {
        NodeOutputs = new(),
        ShouldDoTheThing = true
    };
}


