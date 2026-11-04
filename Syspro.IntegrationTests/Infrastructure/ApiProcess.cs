using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace Syspro.IntegrationTests.Infrastructure;

// The real API as its own operating system process, so that tests can do what really
// happens to one: kill it, stop it gracefully, or freeze it. Connection strings only come
// from the test's container, so this can never reach another database.
public sealed class ApiProcess : IAsyncDisposable
{
    private const string ApiProcessIdPrefix = "SYSPRO_API_PID=";

    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(60);

    // The API runs under this shell, not directly under the test host. When a direct child of
    // a .NET process is frozen, the runtime's signal handling spins on the stopped child, and
    // starting the process that would resume it then never completes.
    private const string Wrapper = $"""
        "$@" &
        pid=$!
        echo "{ApiProcessIdPrefix}$pid"
        wait "$pid"
        exit $?
        """;

    private readonly Process _process;
    private readonly ConcurrentQueue<string> _output = new();
    private readonly TaskCompletionSource<int> _apiProcessId = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private ApiProcess(string name, Process process, Uri address)
    {
        Name = name;
        _process = process;
        Client = new HttpClient { BaseAddress = address };
    }

    public string Name { get; }

    public HttpClient Client { get; }

    // The API itself, which is what signals go to and what a lease owner names.
    public int Id => _apiProcessId.Task.Result;

    public bool HasExited => _process.HasExited;

    public int ExitCode => _process.ExitCode;

    public string Output => string.Join('\n', _output);

    public static async Task<ApiProcess> StartAsync(
        string name,
        string connectionString,
        string contentRoot,
        int batchSize,
        TimeSpan leaseDuration)
    {
        var address = new Uri($"http://127.0.0.1:{FindFreePort()}");

        var startInfo = new ProcessStartInfo("/bin/sh")
        {
            WorkingDirectory = contentRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(Wrapper);
        startInfo.ArgumentList.Add("sh");
        startInfo.ArgumentList.Add("dotnet");
        startInfo.ArgumentList.Add(FindApiAssembly());
        startInfo.ArgumentList.Add("--contentRoot");
        startInfo.ArgumentList.Add(contentRoot);

        // "Testing" means user secrets, which only load in Development, are never read.
        startInfo.Environment["ASPNETCORE_ENVIRONMENT"] = "Testing";
        startInfo.Environment["ASPNETCORE_URLS"] = address.ToString();
        startInfo.Environment["ConnectionStrings__DefaultConnection"] = connectionString;
        startInfo.Environment["CustomerImport__BatchSize"] = batchSize.ToString();
        startInfo.Environment["CustomerImport__LeaseDuration"] = leaseDuration.ToString();
        startInfo.Environment["CustomerImportWorker__PollInterval"] = "00:00:00.100";
        startInfo.Environment["Logging__LogLevel__Default"] = "Warning";
        startInfo.Environment["Logging__LogLevel__Syspro"] = "Information";

        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Process {name} did not start.");

        var api = new ApiProcess(name, process, address);

        process.OutputDataReceived += (_, line) => api.Record(line.Data);
        process.ErrorDataReceived += (_, line) => api.Record(line.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        await api._apiProcessId.Task.WaitAsync(StartTimeout);
        await api.WaitUntilServingAsync();

        return api;
    }

    // What a crash or an out-of-memory kill does: no shutdown code gets to run.
    public void KillNow()
    {
        Signal("KILL");

        _process.WaitForExit();
    }

    // What a container platform sends to stop a process gracefully.
    public Task StopGracefullyAsync(TimeSpan timeout)
    {
        Signal("TERM");

        return _process.WaitForExitAsync().WaitAsync(timeout);
    }

    // Freezes every thread, like a long pause or a stalled virtual machine. The connections
    // stay open and any transaction stays uncommitted, until the process resumes.
    public void Freeze()
    {
        Signal("STOP");
    }

    public void Resume()
    {
        Signal("CONT");
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();

        if (!_process.HasExited)
        {
            Signal("KILL");

            await _process.WaitForExitAsync();
        }

        _process.Dispose();
    }

    private void Record(string? line)
    {
        if (line is null)
        {
            return;
        }

        if (line.StartsWith(ApiProcessIdPrefix, StringComparison.Ordinal))
        {
            _apiProcessId.TrySetResult(int.Parse(line[ApiProcessIdPrefix.Length..]));

            return;
        }

        _output.Enqueue(line);
    }

    private void Signal(string signal)
    {
        using var kill = Process.Start("/bin/kill", $"-{signal} {Id}");

        kill.WaitForExit();
    }

    private async Task WaitUntilServingAsync()
    {
        using var timeout = new CancellationTokenSource(StartTimeout);

        while (true)
        {
            if (_process.HasExited)
            {
                throw new InvalidOperationException(
                    $"Process {Name} exited with code {_process.ExitCode} while starting:\n{Output}");
            }

            try
            {
                // An unknown job proves that routing is up, and it needs a database as well.
                var response = await Client.GetAsync("/api/import/jobs/0", timeout.Token);

                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    return;
                }
            }
            catch (HttpRequestException)
            {
                // Not listening yet.
            }
            catch (OperationCanceledException)
            {
                throw new TimeoutException(
                    $"Process {Name} was not serving after {StartTimeout.TotalSeconds} s:\n{Output}");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100), CancellationToken.None);
        }
    }

    private static int FindFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);

        listener.Start();

        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    // The test project's reference builds the API, leaving its host files in its own output.
    private static string FindApiAssembly()
    {
        var testOutput = new DirectoryInfo(AppContext.BaseDirectory);

        // net8.0, then the configuration, then bin, then the test project, then the solution.
        var configuration = testOutput.Parent!.Name;
        var solution = testOutput.Parent!.Parent!.Parent!.Parent!.FullName;

        var path = Path.Combine(solution, "Syspro.Api", "bin", configuration, testOutput.Name, "Syspro.Api.dll");

        return File.Exists(path)
            ? path
            : throw new FileNotFoundException("The API has not been built where it was expected.", path);
    }
}
