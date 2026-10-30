using System.Text.Json;
using GraphFlow.Hosting.Demo;
using GraphFlow.Hosting.Demo.Models;
using GraphFlow.Hosting;
using GraphFlow.util;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

public class GraphRunnerTests
{
    [Theory]
    [InlineData(false, 3, 2)]
    [InlineData(true, 7, 7)]
    public void FinalResultDescribesVisitedNodesAndEvaluatedEdges(bool loop, int nodeCount, int edgeCount)
    {
        using var provider = CreateProvider();
        var runner = provider.GetRequiredService<IGraphRunner>();
        var input = ExampleGraphStateObject.DefaultValue(); input.ShouldDoTheThing = loop;
        var result = runner.Run("example", JsonSerializer.SerializeToElement(input));
        var summary = Assert.IsType<GraphExecutionSummary>(result.Execution);
        Assert.Equal(nodeCount, summary.Nodes.Sum(n => n.Executions));
        Assert.Equal(edgeCount, summary.Edges.Sum(e => e.Evaluations));
        Assert.Equal(loop ? 2 : 1, summary.Nodes.Single(n => n.Name == "Node1").Executions);
        var branch = summary.Edges.Single(e => e.Name == "Node2_Node3_edge_0");
        Assert.Equal(1, branch.Followed);
        Assert.Equal(loop ? 1 : 0, branch.Rejected);
        if (!loop)
        {
            Assert.DoesNotContain(summary.Nodes, n => n.Name == "Node4");
            Assert.DoesNotContain(summary.Edges, e => e.Name == "Node2_Node4_edge_1");
        }
        Assert.All(summary.Nodes, n => { Assert.Equal(0, n.Failures); Assert.Equal(0, n.Incomplete); });
    }

    [Fact]
    public void FinalResultPreservesRejectedEdgesAndFailedNodes()
    {
        using var provider = CreateProvider();
        var failed = provider.GetRequiredService<IGraphRunner>().Run("failure", JsonSerializer.SerializeToElement(new OtherState()));
        Assert.False(failed.Succeeded);
        Assert.Equal(1, Assert.Single(failed.Execution!.Nodes).Failures);
        var services = new ServiceCollection();
        services.AddGraphFlowHosting().AddGraph<OtherState>("reject", sp =>
        {
            var graph = sp.GetRequiredService<GraphBuilder<OtherState>>().GetExecutableGraph();
            graph.AddNode("start", s => s); graph.AddNode("unvisited", s => s);
            graph.AddEdge("start", "unvisited", _ => false); graph.SetStartNode("start");
            return graph;
        });
        using var rejecting = services.BuildServiceProvider();
        var result = rejecting.GetRequiredService<IGraphRunner>().Run("reject", JsonSerializer.SerializeToElement(new OtherState()));
        Assert.Single(result.Execution!.Nodes);
        var edge = Assert.Single(result.Execution.Edges);
        Assert.Equal(1, edge.Rejected); Assert.Equal(0, edge.Followed);
    }
    private sealed class ScopeProbe : IDisposable
    {
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExecutionScopeIsDisposedEvenWhenConstructionFails(bool fail)
    {
        var probes = new List<ScopeProbe>();
        var services = new ServiceCollection();
        services.AddScoped<ScopeProbe>();
        services.AddGraphFlowHosting().AddGraph<OtherState>("scoped", sp =>
        {
            probes.Add(sp.GetRequiredService<ScopeProbe>());
            if (fail) throw new InvalidOperationException();
            var graph = sp.GetRequiredService<GraphBuilder<OtherState>>().GetExecutableGraph();
            graph.AddNode("identity", s => s);
            graph.SetStartNode("identity");
            return graph;
        });
        using var provider = services.BuildServiceProvider();
        var runner = provider.GetRequiredService<GraphRunner>();
        for (var i = 0; i < 2; i++)
            Assert.Equal(!fail, runner.Run("scoped", JsonSerializer.SerializeToElement(new OtherState())).Succeeded);
        Assert.Equal(2, probes.Count);
        Assert.NotSame(probes[0], probes[1]);
        Assert.All(probes, p => Assert.True(p.Disposed));
    }

    public sealed class OtherState { public int Value { get; set; } }
    private static ServiceProvider CreateProvider()
    {
        var services = new ServiceCollection();
        services.AddScoped<ExampleGraph>();
        services.AddGraphFlowHosting()
            .AddGraph<ExampleGraphStateObject>("example", sp => sp.GetRequiredService<ExampleGraph>().GetGraph())
            .AddGraph<ExampleGraphStateObject>("alias", sp => sp.GetRequiredService<ExampleGraph>().GetGraph())
            .AddGraph<OtherState>("other", sp =>
            {
                var graph = sp.GetRequiredService<GraphBuilder<OtherState>>().GetExecutableGraph();
                graph.AddNode("increment", s => { s.Value++; return s; });
                graph.SetStartNode("increment");
                return graph;
            })
            .AddGraph<OtherState>("failure", sp =>
            {
                var graph = sp.GetRequiredService<GraphBuilder<OtherState>>().GetExecutableGraph();
                graph.AddNode("throws", _ => throw new InvalidOperationException("private detail"));
                graph.SetStartNode("throws");
                return graph;
            })
            .AddGraph<OtherState>("missing-start", sp => sp.GetRequiredService<GraphBuilder<OtherState>>().GetExecutableGraph());
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    [Fact]
    public void SnapshotsRemainUnchangedAndNamesRouteToDifferentStateTypes()
    {
        using var provider = CreateProvider();
        var runner = provider.GetRequiredService<GraphRunner>();
        var events = new List<GraphProgress>();
        var id = Guid.NewGuid();
        var result = runner.Run("example", JsonSerializer.SerializeToElement(ExampleGraphStateObject.DefaultValue()), events.Add, id);
        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(3, result.State!.Value.GetProperty("nodeCount").GetInt32());
        Assert.Equal(0, events[0].State.GetProperty("nodeCount").GetInt32());
        Assert.Empty(events[0].State.GetProperty("nodeOutputs").EnumerateObject());
        Assert.Equal(Enumerable.Range(1, events.Count).Select(x => (long)x), events.Select(e => e.Sequence));
        Assert.All(events, e => Assert.Equal(id, e.RunId));
        var other = runner.Run("other", JsonSerializer.SerializeToElement(new OtherState { Value = 41 }));
        Assert.True(other.Succeeded, other.Error);
        Assert.Equal(42, other.State!.Value.GetProperty("value").GetInt32());
        Assert.Equal(5, runner.Graphs.Count);
    }

    [Fact]
    public async Task ConcurrentRunsHaveIndependentStateAndEvents()
    {
        using var provider = CreateProvider();
        var runner = provider.GetRequiredService<GraphRunner>();
        using var barrier = new Barrier(4);
        var runs = await Task.WhenAll(Enumerable.Range(0, 4).Select(i => Task.Run(() =>
        {
            var events = new List<GraphProgress>();
            var input = ExampleGraphStateObject.DefaultValue();
            input.ShouldDoTheThing = i % 2 == 0;
            var result = runner.Run(i % 2 == 0 ? "example" : "alias", JsonSerializer.SerializeToElement(input), e =>
            {
                events.Add(e);
                if (e.Sequence == 1 && !barrier.SignalAndWait(TimeSpan.FromSeconds(10)))
                    throw new TimeoutException();
            });
            Assert.True(result.Succeeded, result.Error);
            Assert.Equal(i % 2 == 0 ? 7 : 3, result.State!.Value.GetProperty("nodeCount").GetInt32());
            Assert.All(events, e => Assert.Equal(result.RunId, e.RunId));
            Assert.Single(events.Select(e => e.ExecutionId).Distinct());
            return events[0].ExecutionId;
        })));
        Assert.Equal(4, runs.Distinct().Count());
    }

    [Theory]
    [InlineData("failure")]
    [InlineData("missing-start")]
    public void EngineFailuresAreReported(string name)
    {
        using var provider = CreateProvider();
        var result = provider.GetRequiredService<GraphRunner>().Run(name, JsonSerializer.SerializeToElement(new OtherState()));
        Assert.False(result.Succeeded);
        Assert.NotNull(result.Error);
        Assert.DoesNotContain("private detail", result.Error);
    }

    [Fact]
    public void SubscriberFailureIsReportedAndNextRunStillWorks()
    {
        using var provider = CreateProvider();
        var runner = provider.GetRequiredService<GraphRunner>();
        var input = JsonSerializer.SerializeToElement(new OtherState());
        var result = runner.Run("other", input, _ => throw new Exception());
        Assert.False(result.Succeeded);
        Assert.Equal("Progress delivery failed.", result.Error);
        Assert.True(runner.Run("other", input).Succeeded);
        Assert.False(runner.Run("other", JsonSerializer.SerializeToElement("invalid")).Succeeded);
        Assert.Throws<KeyNotFoundException>(() => runner.Run("unknown", input));
        var builder = new ServiceCollection().AddGraphFlowHosting();
        builder.AddGraph<OtherState>("duplicate", _ => null!);
        Assert.Throws<ArgumentException>(() => builder.AddGraph<OtherState>("duplicate", _ => null!));
    }
}



