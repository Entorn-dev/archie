using Archie.Api;
using Microsoft.Extensions.FileProviders;

var builder = WebApplication.CreateBuilder(args);
var loadedGraph = GraphSnapshotLoader.Load(builder.Configuration["AIP_GRAPH_PATH"]);
builder.Services.AddSingleton(loadedGraph);
var app = builder.Build();

var webPath = builder.Configuration["AIP_WEB_PATH"];
if (!string.IsNullOrWhiteSpace(webPath))
{
    var webRoot = new DirectoryInfo(Path.GetFullPath(webPath));
    if (!webRoot.Exists || !File.Exists(Path.Combine(webRoot.FullName, "index.html")))
        throw new InvalidDataException("The configured web application directory does not contain index.html.");
    var files = new PhysicalFileProvider(webRoot.FullName);
    app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });
    app.UseStaticFiles(new StaticFileOptions { FileProvider = files });
}

app.MapGet("/health", () => Results.Ok(new { status = "ready" }));
app.MapGraphEndpoints();
if (!string.IsNullOrWhiteSpace(webPath))
{
    var indexPath = Path.Combine(Path.GetFullPath(webPath), "index.html");
    app.MapFallback(async context => await context.Response.SendFileAsync(indexPath));
}

app.Run();

public partial class Program;
