using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace Archie.Cli;

internal static class ProductLauncher
{
    public static async Task<int> RunHostAsync(
        LocalRepositoryState state,
        bool openBrowser,
        CancellationToken cancellationToken)
    {
        var hostAssembly = Path.Combine(AppContext.BaseDirectory, "host", "Archie.Api.dll");
        if (!File.Exists(hostAssembly))
            throw new InvalidDataException("The bundled graph host is missing from this Archie installation.");
        var webRoot = Path.Combine(AppContext.BaseDirectory, "web");
        if (!File.Exists(Path.Combine(webRoot, "index.html")))
            throw new InvalidDataException("The bundled web application is missing from this Archie installation.");

        Directory.CreateDirectory(state.ViewsPath);
        var port = AvailableLoopbackPort();
        var origin = $"http://127.0.0.1:{port}";
        var explorerUrl = $"{origin}/app";
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = AppContext.BaseDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        start.ArgumentList.Add(hostAssembly);
        start.Environment["AIP_GRAPH_PATH"] = state.GraphPath;
        start.Environment["AIP_VIEW_PATH"] = state.ViewsPath;
        start.Environment["AIP_WEB_PATH"] = webRoot;
        start.Environment["ASPNETCORE_URLS"] = origin;

        using var process = Process.Start(start) ?? throw new InvalidDataException("Could not start the bundled graph host.");
        var output = ForwardAsync(process.StandardOutput, Console.Out);
        var error = ForwardAsync(process.StandardError, Console.Error);
        try
        {
            await WaitUntilReadyAsync(process, origin, cancellationToken);
            Console.WriteLine($"Archie is ready at {explorerUrl}");
            Console.WriteLine($"State: {state.Directory}");
            if (openBrowser) TryOpenBrowser(explorerUrl);
            await process.WaitForExitAsync(cancellationToken);
            await Task.WhenAll(output, error);
            return process.ExitCode;
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            await Task.WhenAll(output, error);
            throw;
        }
        catch
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            await Task.WhenAll(output, error);
            throw;
        }
    }

    private static int AvailableLoopbackPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static async Task WaitUntilReadyAsync(Process process, string url, CancellationToken cancellationToken)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(1) };
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (process.HasExited) throw new InvalidDataException($"The bundled graph host exited with code {process.ExitCode} before becoming ready.");
            try
            {
                using var response = await client.GetAsync($"{url}/health", cancellationToken);
                if (response.IsSuccessStatusCode) return;
            }
            catch (HttpRequestException) { }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested) { }
            await Task.Delay(100, cancellationToken);
        }
        throw new InvalidDataException("The bundled graph host did not become ready within 30 seconds.");
    }

    private static async Task ForwardAsync(StreamReader reader, TextWriter writer)
    {
        while (await reader.ReadLineAsync() is { } line) await writer.WriteLineAsync(line);
    }

    private static void TryOpenBrowser(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo("xdg-open", url) { UseShellExecute = false });
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Console.Error.WriteLine($"Could not open a browser automatically: {exception.Message}");
        }
    }
}
