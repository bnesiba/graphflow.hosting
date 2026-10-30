using ExampleApi;
using ExampleApi.Models;
using GraphFlow.Hosting;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddGraphFlowHosting().AddGraph<ExampleGraphStateObject>("example", sp => sp.GetRequiredService<ExampleGraph>().GetGraph(),
    new("Example graph", "A conditional graph with a loop and two execution paths.", ExampleGraphStateObject.DefaultValue()));
builder.Services.AddScoped<ExampleGraph>();

// Add services to the container.

builder.Services.AddControllers();
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

app.UseAuthorization();

app.MapControllers();
app.MapGraphFlowHosting();

app.Run();



