# GraphFlow.Hosting.AspNetCore

Prototype hosting for GraphFlow on ASP.NET Core / .NET 10. Provides named graph registration, graph discovery, JSON and server-sent-event (SSE) execution, and an optional embedded HTML/JavaScript Explorer UI.

## Setup

Install `bcn.GraphFlow.Hosting.AspNetCore` and use the `GraphFlow.Hosting` namespace. This preview requires `bcn.GraphFlow` 1.1.0, currently supplied in this repository's local package feed.

```csharp
using GraphFlow.Hosting;
using GraphFlow.models;
using GraphFlow.util;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddScoped<CounterGraph>();
builder.Services.AddGraphFlowHosting()
    .AddGraph<CounterState>("counter",
        sp => sp.GetRequiredService<CounterGraph>().Build(),
        new("Counter", "Adds one to the input value.", new CounterState()));

var app = builder.Build();
app.MapGraphFlowHosting("/graphflow");
app.MapGraphFlowUI("/explorer", apiPrefix: "/graphflow"); // Optional
app.Run();

public sealed class CounterState { public int Value { get; set; } }
public sealed class CounterGraph(GraphBuilder<CounterState> builder)
{
    public ExecutableGraph<CounterState> Build()
    {
        var graph = builder.GetExecutableGraph();
        graph.AddNode("increment", state => { state.Value++; return state; });
        graph.SetStartNode("increment");
        return graph;
    }
}
```

GraphFlow services are added once per state type unless already registered. Multiple graph names and state types are supported. Factories must return a fresh graph, resolve scoped dependencies from their supplied provider, and use consistent topology and names. Discovery calls the factory without executing nodes or predicates. No hosting interface is required on your graph definition.

`IGraphCatalog` lists registrations and describes topology/schema. `IGraphRunner` runs a named graph synchronously and reports detached JSON snapshots through a callback. Both are registered by AddGraphFlowHosting. The endpoints consume these interfaces; the default implementation is GraphRunner. Registration uses TryAdd so an application may supply its own implementation.

## HTTP contract

Under the selected API prefix:

| Method | Path | Result |
| --- | --- | --- |
| GET | /graphs | Names, state types, display metadata |
| GET | /graphs/{name} | Nodes, directed edges, start node, input schema, example input |
| POST | /graphs/{name}/runs | Final JSON result (200 success, 422 execution failure) |
| POST | /graphs/{name}/runs/stream | SSE progress and one end event with the final result |

For the setup above, POST `/graphflow/graphs/counter/runs` with `{"value":41}`. The API prefix is a mount point; it does not replace the graphs route segment. Defaults use `/graphflow/graphs/...`.

Unknown graphs return 404. Null or incompatible/malformed JSON returns 400 before execution. SSE failures after streaming starts are represented by the end result. Progress includes runId, executionId, sequence, eventType, objectName, checkpoint, and state. Nodes/edges are identified by graph-local names. An edge highlight indicates evaluation, not necessarily selection.

The UI is served from embedded resources: no frontend installation, CDN, or static-file copying. Custom paths and PathBase are supported. The UI uses same-origin fetch with POST; no SignalR or browser EventSource is required. Both mapping methods return route groups so the application can apply its own authorization policies. Configure authentication, authorization, and any request-protection policies in the consuming application.

## Prototype limits

- .NET 10 is required. The only direct NuGet runtime dependency is GraphFlow; ASP.NET Core is a shared framework reference.
- Execution is synchronous. A disconnected client stops receiving updates but cannot cancel the graph. There is no durable run storage, replay, execution timeout, or global concurrency limit.
- SSE uses a bounded 32-event queue; slow clients can slow execution. This bounds event count, not payload size. The UI retains 500 recent snapshots.
- Node failures are checked in engine metadata because GraphFlow may swallow exceptions. Original failure details are not always available.
- Input schemas describe serialization shape, not business validation. Custom JSON serializers/options and dynamic graph topology are not supported by this preview contract.
- This package contains no sample graphs, artificial delays, application configuration, or launch profiles. Those belong to the separate demo.


## Post-run graph

Both JSON results and SSE end events now include `execution`, an aggregate summary keyed by node/edge name. Nodes report executions, failures, and incomplete attempts. Edges report evaluations, followed/rejected counts, failures, and incomplete attempts. Only visited items are included; untouched topology remains unhighlighted. Construction/input failures may return execution=null when no metadata is available.

After either run mode the Explorer renders this summary: green for visited nodes/followed edges, dashed for edges evaluated but never followed, red for failures, and amber for incomplete attempts. Select an item to inspect counts, including repeated visits through loops. Live edge highlighting still means evaluation; final highlighting uses the engine's recorded evaluation result. Summary counts do not represent a chronological trace or replay.
