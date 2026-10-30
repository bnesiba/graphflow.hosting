using System.Reflection;

namespace GraphFlow.Hosting;

public static class GraphHostingUI
{
    public static RouteGroupBuilder MapGraphFlowUI(this IEndpointRouteBuilder endpoints,
        string prefix = "/graphflow/ui", string apiPrefix = "/graphflow")
    {
        if (!prefix.StartsWith('/') || prefix.Contains('?') || prefix.Contains('#'))
            throw new ArgumentException("Use an application-relative UI path beginning with /.", nameof(prefix));
        if (!apiPrefix.StartsWith('/') || apiPrefix.StartsWith("//") || apiPrefix.Contains('?') || apiPrefix.Contains('#'))
            throw new ArgumentException("Use an application-relative API path beginning with /.", nameof(apiPrefix));
        var group = endpoints.MapGroup(prefix.TrimEnd('/'));
        group.MapGet("/", (HttpContext context) =>
        {
            if (!context.Request.Path.Value!.EndsWith('/'))
                return Results.LocalRedirect(context.Request.PathBase + context.Request.Path + "/" + context.Request.QueryString);
            return Asset("index.html", "text/html; charset=utf-8");
        });
        group.MapGet("/config", (HttpContext context) => Results.Json(new
        {
            apiPrefix = context.Request.PathBase + apiPrefix.TrimEnd('/')
        }));
        group.MapGet("/app.js", () => Asset("app.js", "text/javascript; charset=utf-8"));
        group.MapGet("/sse.mjs", () => Asset("sse.mjs", "text/javascript; charset=utf-8"));
        group.MapGet("/style.css", () => Asset("style.css", "text/css; charset=utf-8"));
        return group;
    }

    private static IResult Asset(string name, string contentType)
    {
        var stream = typeof(GraphHostingUI).Assembly.GetManifestResourceStream("GraphFlow.Hosting.UI." + name)
            ?? throw new InvalidOperationException($"Missing embedded UI asset: {name}");
        return Results.Stream(stream, contentType);
    }
}
