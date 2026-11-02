# GraphFlow Hosting for ASP.NET Core

Expose your C# graphs through a .NET 10 Web API. Clients can discover registered graphs, inspect their nodes and edges, and run them with JSON input. A run can return its final state in one response or stream progress as server-sent events (SSE). The optional embedded Explorer lets you edit input, watch execution, and inspect state snapshots in the browser.

## Install

The hosting package is currently a local preview. With a NuGet source containing the preview and its `bcn.GraphFlow` 1.1.0 dependency, run this command in your ASP.NET Core project:

```sh
dotnet add package bcn.GraphFlow.Hosting.AspNetCore --prerelease
```

## Host a graph

This `Program.cs` exposes a graph named `counter` with one node that increments the input value.

```csharp
using GraphFlow.Hosting;
using GraphFlow.models;
using GraphFlow.util;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddGraphFlowHosting().AddGraph<CounterGraph, CounterState>();

var app = builder.Build();
app.MapGraphFlowHosting("/graphflow");
if (app.Environment.IsDevelopment())
{
    app.MapGraphFlowUI("/graphflow/ui", apiPrefix: "/graphflow");
}
app.Run();

public sealed class CounterState
{
    public int Value { get; set; }
}

public sealed class CounterGraph(GraphBuilder<CounterState> builder) : IHostedGraph<CounterState>
{
    public string Name => "counter";
    public string DisplayName => "Counter";
    public string Description => "Adds one to the input value.";
    public CounterState ExampleInput => new() { Value = 41 };

    public ExecutableGraph<CounterState> GetGraph()
    {
        var graph = builder.GetExecutableGraph();
        graph.AddNode("increment", state => { state.Value++; return state; });
        graph.SetStartNode("increment");
        return graph;
    }
}
```

## Run it

Start the app in Development and open `/graphflow/ui/` on the app's URL. Select **Counter** and run the example input. The `increment` node takes `value` from `41` to `42`. Choose streaming to inspect progress during execution. Explorer ships with the package and requires no frontend build.

To use the HTTP API directly, send the state object itself as the JSON request body.

| Request | Result |
| --- | --- |
| `GET /graphflow/graphs` | List registered graphs. |
| `GET /graphflow/graphs/counter` | Inspect the graph topology and state schema. |
| `POST /graphflow/graphs/counter/runs` | Send `{"value":41}` to receive a JSON result with `state.value` equal to `42`. |
| `POST /graphflow/graphs/counter/runs/stream` | Send the same input to receive SSE `progress` events followed by an `end` event with the run result. |

Your application controls access to the mapped routes. See the [package guide](GraphFlow.Hosting.AspNetCore/README.md) for factory-based registration, endpoint contracts, and execution limits.
