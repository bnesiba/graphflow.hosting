using ExampleApi;
using ExampleApi.Models;
using GraphFlow.Hosting;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddGraphFlowHosting().AddGraph<ExampleGraphStateObject>("example", sp => sp.GetRequiredService<ExampleGraph>().GetGraph(),
    new("Example graph", "A conditional graph with a loop and two execution paths.", ExampleGraphStateObject.DefaultValue()))
    .AddGraph<ExampleGraphStateObject>("slow-example", sp => sp.GetRequiredService<SlowExampleGraph>().GetGraph(),
        new("Slow streaming demo", "Pauses 1 second at each node and edge evaluation. The default loop takes about 14 seconds.", SlowExampleGraph.ExampleInput()));
builder.Services.AddScoped<ExampleGraph>();
builder.Services.AddScoped<SlowExampleGraph>();

builder.Services.AddAuthorization();
builder.Services.AddOpenApi();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

app.UseAuthorization();

app.MapGraphFlowHosting();
app.MapGraphFlowUI();

app.Run();




