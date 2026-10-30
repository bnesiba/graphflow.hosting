# GraphFlow hosting prototype

Stages 1–3 provide the GraphFlow baseline, execution adapter, and HTTP/SSE endpoints. The browser UI is the next stage.

The sample graph and state were copied from graphflow/graphFlow/ExampleApi. The state dictionary is initialized to remove a nullable warning; graph behavior is unchanged. The original graphflow repository was not modified.

## Dependency

packages/bcn.GraphFlow.1.1.0.nupkg is a snapshot of the existing local Debug package produced by graphflow. NuGet.Config adds this directory as a package source. The .NET 10 host successfully consumes its .NET 8 library. No project reference or GraphFlow rebuild is required.

## Validation

From this directory:

```powershell
dotnet restore graphflow.hosting.slnx
dotnet test graphflow.hosting.slnx --no-restore --logger 'console;verbosity=detailed'
```

In the restricted execution environment, validation used an isolated package directory and the existing local package cache:

```powershell
$env:APPDATA = Join-Path $PWD '.local-appdata'
dotnet restore graphflow.hosting.slnx --configfile NuGet.Config --source packages --source C:\Users\bnesiba\.nuget\packages --packages .packages -p:NuGetAudit=false
dotnet test graphflow.hosting.slnx --no-restore --logger 'console;verbosity=detailed'
```

Two parameterized test cases verify exact final state, node order, successful node metadata, callback execution IDs and checkpoints, and callbacks occurring before StreamGraph returns.

| Input ShouldDoTheThing | Node path | Final NodeCount | Callbacks |
| --- | --- | --- | --- |
| false | 1, 2, 3 | 3 | 11 |
| true | 1, 2, 4, 5, 1, 2, 3 | 7 | 29 |

Both finish with ShouldDoTheThing=true. ExecuteGraph and StreamGraph return identical expected state. Callbacks begin with GraphStart and end with NodeComplete for Node3; neither execution emits GraphComplete. Edge events include rejected edges. There were no StateChange callbacks on these sample runs.

The callback receives mutable state; the tests capture scalar values immediately. Hosting must snapshot payloads before asynchronous delivery and emit its own terminal event after execution returns. These tests establish synchronous library behavior, not network delivery, concurrency isolation, or failure handling; those belong to subsequent stages.

## Stage 2: execution adapter

The sample application now registers its example through `AddGraphFlowHosting().AddGraph<T>(name, factory)`. `GraphRunner` resolves registrations by case-sensitive name, creates and disposes a scope per execution, and invokes the factory inside that scope. Factories must return a fresh graph; do not capture a shared graph or scoped service in the registration delegate. Existing GraphBuilder registration is respected, and engine registration is added once per state type.

```csharp
builder.Services.AddScoped<ExampleGraph>();
builder.Services.AddGraphFlowHosting()
    .AddGraph<ExampleGraphStateObject>("example",
        sp => sp.GetRequiredService<ExampleGraph>().GetGraph());
```

`GraphRunner.Run(name, inputJson, onProgress, runId)` is synchronous. It accepts JSON input and deserializes a fresh typed state per run. Progress uses detached JSON snapshots, ordered sequence numbers, the hosting run ID, and the engine execution ID. The optional hosting run ID lets the future API establish subscription correlation before execution. The runner retains no run history or event buffer.

The result contains the final JSON state, graph name, run ID, success flag, and a safe error description. Success requires successful graph, node, and edge metadata. Unknown graph names throw KeyNotFoundException; invalid input, construction errors, and execution errors return failed results. Duplicate names fail during registration. This is a prototype contract; HTTP error mapping is still to be designed.

Progress subscribers must return promptly. A subscriber or snapshot failure is recorded, further callbacks are suppressed, and graph execution continues; the returned result is marked failed with a progress-delivery error. This does not mean the graph's effects were rolled back. GraphFlow does not consistently preserve original execution error details, so the adapter does not promise them. There is no cancellation or terminal network event in this stage.

Stage 2 tests cover retained snapshots, result mapping, multiple graphs and state types, four deliberately overlapping executions, failed nodes, missing start nodes, subscriber failures, invalid input, duplicate/unknown names, and fresh scope disposal on both success and factory failure. HTTP endpoints, transport scheduling, backpressure, and the end event remain stage 3 work.


## Stage 3: HTTP and SSE

Map the endpoints once in an existing API:

```csharp
app.MapGraphFlowHosting("/graphflow");
// The returned route group supports host policies, e.g. .RequireAuthorization().
```

| Method | Path | Response |
| --- | --- | --- |
| GET | /graphflow/graphs | Registered names and state type names |
| GET | /graphflow/graphs/{name} | One graph's basic metadata |
| POST | /graphflow/graphs/{name}/runs | Final GraphRunResult as JSON |
| POST | /graphflow/graphs/{name}/runs/stream | SSE progress followed by one end event |

Both POST endpoints take the initial state directly as a JSON body. Example:

```json
{"nodeOutputs":{},"nodeCount":0,"shouldDoTheThing":true}
```

For local manual use, run the application and use its displayed address:

```powershell
curl.exe -N -H "Content-Type: application/json" -d '{"shouldDoTheThing":true}' http://localhost:5000/graphflow/graphs/example/runs/stream
```

The sample launch profile may use a different port or HTTPS. Graph listing does not instantiate graphs. Graph detail lookup now constructs a graph in a temporary scope to inspect topology; see the stage 3 follow-up below.

Unknown graph names return 404. Malformed JSON, null input, or JSON incompatible with the state type returns 400 before execution starts. This is deserialization validation, not application-specific business validation. Non-streamed successful execution returns 200; graph execution/construction failure returns 422 with a failed GraphRunResult.

A valid streaming request uses HTTP 200 and text/event-stream. Each `progress` event contains runId, executionId, sequence (starting at 1), eventType, objectName, checkpoint, and an immutable state snapshot. The final `end` event contains the same result shape as the normal endpoint: runId, graphName, succeeded, state, error. Once streaming starts, failures are conveyed through the terminal result rather than a new HTTP status. A connected client gets one end event and then EOF. A disconnected client cannot be guaranteed an end event.

Use browser fetch with a JSON POST and read response.body as an SSE stream; native EventSource cannot send the POST body. Network chunks need not align with SSE events, so the UI must retain partial frames while parsing. Do not automatically retry execution POSTs: retries start new runs. There is no replay, result persistence, or separate subscription endpoint.

Streaming uses built-in ASP.NET Core SSE and System.Threading.Channels; no new runtime or test packages were added. Each run has a 32-item bounded progress queue. When it fills, the synchronous producer waits for the writer; progress is not silently dropped. This bounds queued event count, not payload bytes. Disconnect cancels delivery and releases blocked queue writes, but the graph continues until GraphFlow returns and its execution scope is disposed. There is no hard execution cancellation or global concurrency limit in this prototype. Slow consumers can slow graph execution.

HTTP tests start real Kestrel on an ephemeral loopback port, without a test-server package. They verify discovery/custom prefixes alongside an existing API endpoint, input errors, final-state parity, event ordering, live delivery before completion, concurrent stream isolation, failed execution, disconnect behavior, and a 399-progress-event run exceeding the queue capacity. Stage 4 (the browser UI) has not started.



## Stage 3 follow-up: graph details for the UI

GET /graphflow/graphs/{name} now returns name, stateType, displayName, description, startNode, nodes, edges, inputSchema, and exampleInput. The example graph exposes five nodes and five directed edges, including its loop. Each node has a name; each edge has a name, source node name, target node name, and zero-based evaluation order within its source node. Parallel edges remain distinct. No instance GUIDs or executable delegates are exposed.

Node and edge names are stable graph-local identifiers and match progress.objectName (interpret alongside eventType to distinguish node and edge events). Factories must build consistent topology and names across discovery and execution. Detail inspection rejects blank/duplicate names and edges or start nodes referencing nodes outside the graph. It follows each node's outgoing edges, which is the adjacency data execution uses. A graph without a start node is described with startNode=null; it still fails if executed.

Detail lookup invokes the registered factory in a new scope, disposed after inspection, but never invokes node functions or edge predicates. Factories therefore must be safe to call for discovery. Factory/description errors return a safe 500 problem response and are logged server-side. Unknown names remain 404. Listing stays lightweight and does not invoke factories.

Input schema generation uses the built-in System.Text.Json schema exporter with web serialization conventions, including camelCase names and numeric-string handling. The root explicitly excludes null to match the HTTP contract. This describes serialization shape, not business rules or automatic default values. Optional typed example input is snapshotted at registration, so later mutation cannot change the published example.

```csharp
services.AddGraphFlowHosting().AddGraph<ExampleGraphStateObject>(
    "example",
    sp => sp.GetRequiredService<ExampleGraph>().GetGraph(),
    new GraphPresentation<ExampleGraphStateObject>(
        DisplayName: "Example graph",
        Description: "A conditional graph with a loop and two execution paths.",
        ExampleInput: ExampleGraphStateObject.DefaultValue()));
```

This is enough to build the initial UI's graph picker, topology display, example JSON editor, live node/edge highlighting, state inspection, and final result display. Predicates remain opaque: an edge evaluation event is not itself proof that the edge was taken. Rich condition labels, generated forms, and graph editing are not part of this prototype contract.
