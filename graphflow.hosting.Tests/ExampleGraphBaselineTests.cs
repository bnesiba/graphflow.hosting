using ExampleApi;
using ExampleApi.Models;
using GraphFlow.models;
using GraphFlow.util;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

public class ExampleGraphBaselineTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(false, 3)]
    [InlineData(true, 7)]
    public void ExecutionAndStreamingMatchExample(bool loop, int expectedCount)
    {
        var services = new ServiceCollection();
        services.UseGraphFlow<ExampleGraphStateObject>();
        services.AddScoped<ExampleGraph>();
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var factory = scope.ServiceProvider.GetRequiredService<ExampleGraph>();
        var input = ExampleGraphStateObject.DefaultValue();
        input.ShouldDoTheThing = loop;
        var direct = factory.GetGraph().ExecuteGraph(input);

        var streamInput = ExampleGraphStateObject.DefaultValue();
        streamInput.ShouldDoTheThing = loop;
        var events = new List<(GraphProgressType Type, string Name, int Count, Guid Execution, Guid Checkpoint)>();
        var returned = false;
        var streamed = factory.GetGraph().StreamGraph(streamInput, progress =>
        {
            Assert.False(returned);
            // Capture values during the callback: GraphValue is mutable.
            events.Add((progress.EventType, progress.GraphObjectName, progress.GraphValue.NodeCount,
                progress.GraphExecutionId, progress.GraphRunState.CurrentCheckpoint));
        });
        returned = true;

        var expected = new Dictionary<string, string>
        {
            ["Node1"] = loop ? "Node1RunNode1Run" : "Node1Run",
            ["Node2"] = loop ? "Node2DidTheThingNode2DidNotDoTheThing" : "Node2DidNotDoTheThing",
            ["Node3"] = "MadeItToNode3"
        };
        if (loop)
        {
            expected["Node4"] = "Node4Reached";
            expected["Node5"] = "Node5Run";
        }
        foreach (var result in new[] { direct, streamed })
        {
            Assert.Equal(expectedCount, result.StateObject.NodeCount);
            Assert.True(result.StateObject.ShouldDoTheThing);
            Assert.Equal(expected.OrderBy(x => x.Key), result.StateObject.NodeOutputs.OrderBy(x => x.Key));
            Assert.Equal(expectedCount, result.GraphData.NodeRuns.Count);
            Assert.All(result.GraphData.NodeRuns.Values, node => Assert.True(node.Succeeded));
        }
        Assert.NotEmpty(events);
        Assert.Equal(loop ? 29 : 11, events.Count);
        Assert.Equal(GraphProgressType.GraphStart, events[0].Type);
        Assert.Equal(0, events[0].Count);
        Assert.Equal(GraphProgressType.NodeComplete, events[^1].Type);
        Assert.Equal("Node3", events[^1].Name);
        Assert.Equal(Enumerable.Range(1, expectedCount),
            events.Where(e => e.Type == GraphProgressType.NodeComplete).Select(e => e.Count));
        Assert.Single(events.Select(e => e.Execution).Distinct());
        Assert.All(events, e => Assert.NotEqual(Guid.Empty, e.Checkpoint));
        var nodes = loop ? new[] { "Node1", "Node2", "Node4", "Node5", "Node1", "Node2", "Node3" }
                         : new[] { "Node1", "Node2", "Node3" };
        Assert.Equal(nodes, events.Where(e => e.Type == GraphProgressType.NodeStart).Select(e => e.Name));
        Assert.Equal(nodes, events.Where(e => e.Type == GraphProgressType.NodeComplete).Select(e => e.Name));
        Assert.DoesNotContain(events, e => e.Type == GraphProgressType.GraphComplete);
        output.WriteLine(string.Join(Environment.NewLine, events.Select((e, i) => $"{i + 1}: {e.Type} {e.Name} count={e.Count}")));
    }
}
