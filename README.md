# GraphFlow Hosting prototype

A .NET 10 hosting package for predefined GraphFlow graphs, with discovery, JSON/SSE execution, and an optional embedded Explorer UI.

## Projects

| Project | Purpose |
| --- | --- |
| GraphFlow.Hosting.AspNetCore | Packable library: registration, runner/catalog services, endpoints, embedded HTML/CSS/JavaScript |
| GraphFlow.Hosting.Demo | Application consuming the library: fast example, slow demo, application configuration, OpenAPI, launch profiles |
| GraphFlow.Hosting.Tests | Engine, adapter, discovery, HTTP/SSE, UI-serving, and service-registration tests |
| package-tests | Separate package-only consumer used to verify the generated NuGet artifact; intentionally outside the solution |

The library does not reference the demo. Solution tests reference the demo to reuse its sample graphs; the package-only check defines an unrelated counter graph and has no project references. GraphFlow's source repository remains untouched.

## Run the demo

Open `graphflow.hosting.slnx` and select **GraphFlow.Hosting.Demo** as the startup project in Visual Studio. F5 or Ctrl+F5 opens `/graphflow/ui/` using the selected HTTP or HTTPS launch profile. The old graphflow.hosting application project has been renamed/moved; reload the solution if it was already open.

From this directory:

```powershell
dotnet restore graphflow.hosting.slnx
dotnet run --project GraphFlow.Hosting.Demo --launch-profile http
```

For command-line runs, open http://localhost:5272/graphflow/ui/ manually. HTTPS uses https://localhost:7255/graphflow/ui/.

The **Example graph** is immediate. **Slow streaming demo** sleeps one second inside every node function and edge predicate, including rejected edges. Its default looping input takes about 14 seconds (seven node executions and seven edge evaluations, 29 progress events). Set shouldDoTheThing=false for the five-second path. Discovery never sleeps. The sleeps belong only to the demo and do not accumulate across runs.

## Use the library

See [the package README](GraphFlow.Hosting.AspNetCore/README.md) for a complete arbitrary-graph example, service interfaces, endpoint contracts, and prototype limits.

```csharp
builder.Services.AddScoped<MyGraph>();
builder.Services.AddGraphFlowHosting()
    .AddGraph<MyState>("my-graph", sp => sp.GetRequiredService<MyGraph>().Build());
app.MapGraphFlowHosting("/graphflow");
app.MapGraphFlowUI("/graphflow/ui", apiPrefix: "/graphflow"); // Optional
```

The package exposes IGraphRunner and IGraphCatalog, uses GraphFlow as its only direct NuGet runtime dependency, and references the ASP.NET Core shared framework. There is no frontend build or third-party JavaScript dependency. OpenAPI is demo-only. The initial package targets .NET 10 because it uses built-in SSE support.

## Build and validate

```powershell
dotnet test graphflow.hosting.slnx --no-restore
node --test ui-tests/sse.test.mjs
./package-tests/Verify-Package.ps1
```

The package check packs the library, restores it into a fresh cache, and runs an independent application on an ephemeral loopback port. It verifies interfaces, discovery, JSON and SSE results, embedded assets, custom paths/PathBase, coexistence with existing endpoints, and absence of demo types. It shuts down afterward.

Generated package: `artifacts/bcn.GraphFlow.Hosting.AspNetCore.0.1.0-preview.1.nupkg`. This is a local preview, not published to a registry. NuGet.Config includes `packages/`, which contains the existing local GraphFlow 1.1.0 package snapshot. Another machine consuming the hosting preview must also have a feed containing this GraphFlow dependency.

Restricted Windows validation used these commands to avoid an inaccessible user NuGet configuration and reuse existing packages:

```powershell
$env:APPDATA = Join-Path $PWD '.local-appdata'
dotnet restore graphflow.hosting.slnx --configfile NuGet.Config --source packages --source C:\Users\bnesiba\.nuget\packages --packages .packages -p:NuGetAudit=false
dotnet test graphflow.hosting.slnx --no-restore
node --preserve-symlinks --preserve-symlinks-main --test ui-tests/sse.test.mjs
./package-tests/Verify-Package.ps1 -CacheSource C:\Users\bnesiba\.nuget\packages
```

## Explorer and limitations

The UI provides graph selection, a topology diagram, JSON input editing, schema inspection, streaming/normal execution, a 500-event snapshot history, and final results. Node/edge names match progress events. Edge highlights indicate evaluation, not necessarily selection. Show latest returns from a selected snapshot to the current state or final result. Custom UI/API prefixes and PathBase are supported.

Execution is synchronous. Disconnect stops observation, not the graph. There is no durable history, replay, execution timeout, or global concurrency limit. SSE queues at most 32 events per run and applies backpressure to slow consumers; payload size is not bounded. Some engine error details are unavailable. Schemas describe serialization shape rather than business validation. The application owns security policies.

Earlier browser verification covered both sample branches, final-only execution, invalid input, failed graphs, snapshot inspection, and live delayed execution. UI screenshots remain in ui-tests/. Packaging validation exercises the same embedded assets without changing their behavior.

Final-only and streamed runs now both render a post-run graph from execution metadata. Select nodes/edges for counts; loop revisits and rejected edge evaluations are retained. See the package README's post-run graph section. Validation now includes 27 .NET tests, including summary mapping, failed nodes, rejected edges, and JSON/SSE parity. Browser verification covered both final-only example paths. Preview: ui-tests/post-run-preview.png.

The demo now implements IHostedGraph<TState> on both graph definitions. Program.cs uses `.AddGraph<ExampleGraph, ExampleGraphStateObject>()` and `.AddGraph<SlowExampleGraph, ExampleGraphStateObject>()`; names, descriptions, and example input live with the graph classes. The overload registers each definition as scoped and reuses the original factory registration. The interface exposes instance properties directly, with no name override or GraphPresentation required. See the package README for metadata scope and lifecycle details.

