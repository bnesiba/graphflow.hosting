using GraphFlow.Hosting.Demo;
using GraphFlow.Hosting.Demo.Models;
using GraphFlow.Hosting;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddGraphFlowHosting()
    .AddGraph<ExampleGraph, ExampleGraphStateObject>()
    .AddGraph<SlowExampleGraph, ExampleGraphStateObject>();

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





