using System.Text.Json;
using ExampleApi;
using ExampleApi.Models;
using GraphFlow.Hosting;
using GraphFlow.util;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

public class GraphDiscoveryTests
{
    private sealed class Probe : IDisposable
    {
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
    }

    [Fact]
    public void DiscoveryUsesScopedConstructionWithoutExecutingAndSnapshotsExample()
    {
        var constructed = 0;
        var executed = 0;
        var probes = new List<Probe>();
        var input = ExampleGraphStateObject.DefaultValue();
        var services = new ServiceCollection();
        services.AddScoped<Probe>();
        services.AddGraphFlowHosting().AddGraph<ExampleGraphStateObject>("inspect", sp =>
        {
            constructed++;
            probes.Add(sp.GetRequiredService<Probe>());
            var graph = sp.GetRequiredService<GraphBuilder<ExampleGraphStateObject>>().GetExecutableGraph();
            graph.AddNode("a", s => { executed++; return s; });
            graph.AddNode("b", s => { executed++; return s; });
            graph.AddEdge("a", "b", _ => { executed++; return true; });
            graph.AddEdge("a", "b", _ => { executed++; return false; });
            graph.SetStartNode("a");
            return graph;
        }, new("Inspect", "A description", input));
        input.NodeCount = 99;
        using var provider = services.BuildServiceProvider();
        var runner = provider.GetRequiredService<GraphRunner>();
        Assert.Equal("Inspect", Assert.Single(runner.Graphs).DisplayName);
        Assert.Equal(0, constructed);
        var first = runner.Describe("inspect");
        var second = runner.Describe("inspect");
        Assert.Equal(2, constructed);
        Assert.Equal(0, executed);
        Assert.All(probes, p => Assert.True(p.Disposed));
        Assert.NotSame(probes[0], probes[1]);
        Assert.Equal(first.Nodes, second.Nodes);
        Assert.Equal(first.Edges, second.Edges);
        Assert.Equal("a", first.StartNode);
        Assert.Equal(new[] { "a_b_edge_0", "a_b_edge_1" }, first.Edges.Select(e => e.Name));
        Assert.Equal(new[] { 0, 1 }, first.Edges.Select(e => e.Order));
        Assert.All(first.Edges, e => { Assert.Equal("a", e.Source); Assert.Equal("b", e.Target); });
        Assert.Equal(0, first.ExampleInput!.Value.GetProperty("nodeCount").GetInt32());
        var properties = first.InputSchema.GetProperty("properties");
        Assert.Contains(properties.GetProperty("nodeCount").GetProperty("type").EnumerateArray(), t => t.GetString() == "integer");
        Assert.Equal("boolean", properties.GetProperty("shouldDoTheThing").GetProperty("type").GetString());
        Assert.True(properties.TryGetProperty("nodeOutputs", out _));
        Assert.Throws<KeyNotFoundException>(() => runner.Describe("absent"));
    }

    [Fact]
    public void CyclicExampleTopologyMatchesProgressNames()
    {
        var services = new ServiceCollection();
        services.AddScoped<ExampleGraph>();
        services.AddGraphFlowHosting().AddGraph<ExampleGraphStateObject>("example", sp => sp.GetRequiredService<ExampleGraph>().GetGraph());
        using var provider = services.BuildServiceProvider();
        var runner = provider.GetRequiredService<GraphRunner>();
        var details = runner.Describe("example");
        Assert.Equal(5, details.Nodes.Count);
        Assert.Equal(5, details.Edges.Count);
        Assert.Equal("Node1", details.StartNode);
        Assert.Contains(details.Edges, e => e.Source == "Node5" && e.Target == "Node1");
        var input = ExampleGraphStateObject.DefaultValue(); input.ShouldDoTheThing = true;
        var events = new List<GraphProgress>();
        Assert.True(runner.Run("example", JsonSerializer.SerializeToElement(input), events.Add).Succeeded);
        foreach (var progress in events)
        {
            if (progress.EventType.StartsWith("Node")) Assert.Contains(details.Nodes, n => n.Name == progress.ObjectName);
            if (progress.EventType.StartsWith("Edge")) Assert.Contains(details.Edges, e => e.Name == progress.ObjectName);
        }
    }

    [Fact]
    public void AmbiguousEdgeNamesAreRejected()
    {
        var services = new ServiceCollection();
        services.AddGraphFlowHosting().AddGraph<ExampleGraphStateObject>("invalid", sp =>
        {
            var graph = sp.GetRequiredService<GraphBuilder<ExampleGraphStateObject>>().GetExecutableGraph();
            graph.AddNode("a", s => s); graph.AddNode("b", s => s);
            graph.AddEdge("a", "b"); graph.AddEdge("a", "b");
            graph.graphEdges[1].name = graph.graphEdges[0].name;
            return graph;
        });
        using var provider = services.BuildServiceProvider();
        Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<GraphRunner>().Describe("invalid"));
    }
}


