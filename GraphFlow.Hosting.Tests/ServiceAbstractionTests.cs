using System.Text.Json;
using GraphFlow.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

public class ServiceAbstractionTests
{
    [Fact]
    public void DefaultInterfacesResolveToOneRunnerAndRegistrationIsIdempotent()
    {
        var services = new ServiceCollection();
        services.AddGraphFlowHosting();
        services.AddGraphFlowHosting();
        using var provider = services.BuildServiceProvider();
        var implementation = provider.GetRequiredService<GraphRunner>();
        Assert.Same(implementation, provider.GetRequiredService<IGraphRunner>());
        Assert.Same(implementation, provider.GetRequiredService<IGraphCatalog>());
        Assert.Single(provider.GetServices<IGraphRunner>());
        Assert.Single(provider.GetServices<IGraphCatalog>());
    }

    [Fact]
    public void HostingPreservesApplicationProvidedInterfaces()
    {
        var services = new ServiceCollection();
        var custom = new CustomServices();
        services.AddSingleton<IGraphRunner>(custom);
        services.AddSingleton<IGraphCatalog>(custom);
        services.AddGraphFlowHosting();
        using var provider = services.BuildServiceProvider();
        Assert.Same(custom, provider.GetRequiredService<IGraphRunner>());
        Assert.Same(custom, provider.GetRequiredService<IGraphCatalog>());
    }

    private sealed class CustomServices : IGraphRunner, IGraphCatalog
    {
        public IReadOnlyList<HostedGraphInfo> Graphs => [];
        public GraphDetails Describe(string name) => throw new KeyNotFoundException();
        public GraphRunResult Run(string name, JsonElement input, Action<GraphProgress>? progress = null, Guid? runId = null)
            => new(runId ?? Guid.NewGuid(), name, true, input.Clone(), null);
    }
}
