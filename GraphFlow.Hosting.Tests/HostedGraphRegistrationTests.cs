using System.Text.Json;
using GraphFlow.Hosting;
using GraphFlow.models;
using GraphFlow.util;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

public class HostedGraphRegistrationTests
{
    public sealed class State { public int Value { get; set; } }
    public sealed class Tracker { public List<Definition> Instances { get; } = []; }
    public sealed class Definition : IHostedGraph<State>, IDisposable
    {
        private readonly GraphBuilder<State> builder;
        public bool Disposed { get; private set; }
        public Definition(GraphBuilder<State> builder, Tracker tracker)
        {
            this.builder = builder;
            tracker.Instances.Add(this);
        }
        public string Name => "defined";
        public string DisplayName => "Defined graph";
        public string Description => "Adds one.";
        public State ExampleInput { get; } = new() { Value = 10 };
        public int GraphsBuilt { get; private set; }
        public ExecutableGraph<State> GetGraph()
        {
            GraphsBuilt++;
            var graph = builder.GetExecutableGraph();
            graph.AddNode("increment", s => { s.Value++; return s; }); graph.SetStartNode("increment");
            return graph;
        }
        public void Dispose() => Disposed = true;
    }

    [Fact]
    public void MetadataAndEachOperationUseSeparateDisposedScopes()
    {
        var services = new ServiceCollection(); var tracker = new Tracker(); services.AddSingleton(tracker);
        services.AddGraphFlowHosting().AddGraph<Definition, State>();
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        Assert.Empty(tracker.Instances);
        var catalog = provider.GetRequiredService<IGraphCatalog>();
        Assert.Equal("Defined graph", Assert.Single(catalog.Graphs).DisplayName);
        var metadataInstance = Assert.Single(tracker.Instances);
        Assert.True(metadataInstance.Disposed);
        Assert.Equal(0, metadataInstance.GraphsBuilt);
        metadataInstance.ExampleInput.Value = 99;
        var detail = catalog.Describe("defined");
        Assert.Equal("Adds one.", detail.Description);
        Assert.Equal(10, detail.ExampleInput!.Value.GetProperty("value").GetInt32());
        var runner = provider.GetRequiredService<IGraphRunner>();
        for (var i = 0; i < 2; i++)
        {
            var result = runner.Run("defined", JsonSerializer.SerializeToElement(new State { Value = 41 }));
            Assert.True(result.Succeeded, result.Error);
            Assert.Equal(42, result.State!.Value.GetProperty("value").GetInt32());
        }
        Assert.Equal(4, tracker.Instances.Distinct().Count());
        Assert.All(tracker.Instances, instance => Assert.True(instance.Disposed));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DuplicateNamesAreRejectedWhenCatalogResolves(bool mixWithFactory)
    {
        var services = new ServiceCollection(); services.AddSingleton<Tracker>();
        var hosting = services.AddGraphFlowHosting().AddGraph<Definition, State>();
        if (mixWithFactory) hosting.AddGraph<State>("defined", _ => throw new Exception("Must not construct graph"));
        else hosting.AddGraph<Definition, State>();
        Assert.Single(services, d => d.ServiceType == typeof(Definition));
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        Assert.Throws<ArgumentException>(() => provider.GetRequiredService<IGraphCatalog>());
        Assert.All(provider.GetRequiredService<Tracker>().Instances, instance => Assert.True(instance.Disposed));
    }
}
