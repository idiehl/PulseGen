using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;

namespace PulseGen.ModelHost.Client;

public sealed record ModelHostProgress(string JobId, double Progress, string Message);

public sealed record ModelHostAnalysisResult(string JobId, string OutputDirectory, JsonElement Manifest);

public enum ModelHostChannelMode
{
    LinkedStereo,
    IndependentChannels,
    MidSide
}

public sealed class ModelHostClient : IAsyncDisposable
{
    private const int MaximumProtocolLineCharacters = 16 * 1024 * 1024;
    private readonly Process process;
    private readonly SemaphoreSlim writeGate = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> responses = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> jobs = new();
    private readonly TaskCompletionSource<JsonElement> ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task readerTask;
    private readonly Task errorTask;
    private readonly Queue<string> recentErrors = new();
    private bool disposed;

    private ModelHostClient(Process process)
    {
        this.process = process;
        readerTask = ReadProtocolAsync();
        errorTask = ReadErrorsAsync();
    }

    public event EventHandler<ModelHostProgress>? ProgressChanged;

    public static async Task<ModelHostClient> StartAsync(CancellationToken cancellationToken = default)
    {
        var (python, modelHostRoot) = LocateRuntime();
        var startInfo = new ProcessStartInfo
        {
            FileName = python,
            WorkingDirectory = modelHostRoot,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("-m");
        startInfo.ArgumentList.Add("pulsegen_model_host");
        startInfo.Environment["PYTHONUTF8"] = "1";
        startInfo.Environment["PYTHONUNBUFFERED"] = "1";
        startInfo.Environment["PYTHONDONTWRITEBYTECODE"] = "1";
        startInfo.Environment["PULSEGEN_LOCAL_ONLY"] = "1";
        var process = Process.Start(startInfo) ?? throw new InvalidOperationException("PulseGen.ModelHost did not start.");
        var client = new ModelHostClient(process);
        try
        {
            await client.ready.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
            var hello = await client.SendRequestAsync(new { operation = "hello" }, cancellationToken);
            if (!hello.GetProperty("ok").GetBoolean() || !hello.GetProperty("localOnly").GetBoolean())
            {
                throw new InvalidDataException("ModelHost did not confirm its local-only protocol boundary.");
            }
            return client;
        }
        catch
        {
            await client.DisposeAsync();
            throw;
        }
    }

    public async Task<JsonElement> GetPackInventoryAsync(CancellationToken cancellationToken = default)
    {
        var response = await SendRequestAsync(new { operation = "pack.inventory" }, cancellationToken);
        EnsureSuccess(response);
        return response.GetProperty("packs").Clone();
    }

    public async Task<ModelHostAnalysisResult> AnalyzeAsync(
        string inputPath,
        string outputDirectory,
        ModelHostChannelMode channelMode = ModelHostChannelMode.LinkedStereo,
        int maxPartials = 24,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var protocolChannelMode = channelMode switch
        {
            ModelHostChannelMode.LinkedStereo => "linked",
            ModelHostChannelMode.IndependentChannels => "independent",
            ModelHostChannelMode.MidSide => "mid-side",
            _ => throw new ArgumentOutOfRangeException(nameof(channelMode), channelMode, "Unsupported channel analysis mode.")
        };
        if (maxPartials is < 1 or > 256)
        {
            throw new ArgumentOutOfRangeException(nameof(maxPartials), maxPartials, "Partial density must be in 1..256.");
        }

        var jobId = Guid.NewGuid().ToString("N");
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!jobs.TryAdd(jobId, completion))
        {
            throw new InvalidOperationException("Could not register a unique ModelHost job.");
        }

        try
        {
            var accepted = await SendRequestAsync(new
            {
                operation = "analysis.submit",
                jobId,
                inputPath = Path.GetFullPath(inputPath),
                outputDirectory = Path.GetFullPath(outputDirectory),
                channelMode = protocolChannelMode,
                windowSizes = new[] { 1024, 4096, 16384 },
                synthesisWindow = 2048,
                maxPartials
            }, cancellationToken);
            EnsureSuccess(accepted);

            using var cancellationRegistration = cancellationToken.Register(() => _ = RequestCancellationAsync(jobId));
            var completed = await completion.Task.WaitAsync(cancellationToken);
            if (completed.GetProperty("event").GetString() == "job.failed")
            {
                var error = completed.GetProperty("error");
                if (error.GetProperty("type").GetString() == "AnalysisCancelled")
                {
                    throw new OperationCanceledException(error.GetProperty("message").GetString(), cancellationToken);
                }
                throw new InvalidOperationException($"ModelHost job failed: {error.GetProperty("message").GetString()}");
            }

            return new(jobId, Path.GetFullPath(outputDirectory), completed.GetProperty("result").Clone());
        }
        finally
        {
            jobs.TryRemove(jobId, out _);
        }
    }

    private async Task RequestCancellationAsync(string jobId)
    {
        try
        {
            _ = await SendRequestAsync(new { operation = "job.cancel", jobId }, CancellationToken.None);
        }
        catch
        {
            // The primary analysis task reports process and protocol failures.
        }
    }

    private async Task<JsonElement> SendRequestAsync(object message, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var requestId = Guid.NewGuid().ToString("N");
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!responses.TryAdd(requestId, completion))
        {
            throw new InvalidOperationException("Could not register a unique ModelHost request.");
        }

        try
        {
            var document = JsonSerializer.SerializeToElement(message);
            var request = new Dictionary<string, JsonElement>();
            foreach (var property in document.EnumerateObject())
            {
                request[property.Name] = property.Value.Clone();
            }
            request["requestId"] = JsonSerializer.SerializeToElement(requestId);
            var line = JsonSerializer.Serialize(request);
            await writeGate.WaitAsync(cancellationToken);
            try
            {
                await process.StandardInput.WriteLineAsync(line.AsMemory(), cancellationToken);
                await process.StandardInput.FlushAsync(cancellationToken);
            }
            finally
            {
                writeGate.Release();
            }
            return await completion.Task.WaitAsync(cancellationToken);
        }
        finally
        {
            responses.TryRemove(requestId, out _);
        }
    }

    private async Task ReadProtocolAsync()
    {
        Exception? failure = null;
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                var line = await process.StandardOutput.ReadLineAsync(lifetime.Token);
                if (line is null)
                {
                    break;
                }
                if (line.Length > MaximumProtocolLineCharacters)
                {
                    throw new InvalidDataException("ModelHost protocol line exceeded the bounded message size.");
                }

                using var document = JsonDocument.Parse(line);
                var value = document.RootElement.Clone();
                if (value.TryGetProperty("requestId", out var requestProperty) && requestProperty.GetString() is { } requestId &&
                    responses.TryGetValue(requestId, out var response))
                {
                    response.TrySetResult(value);
                    continue;
                }
                if (value.TryGetProperty("event", out var eventProperty))
                {
                    var eventName = eventProperty.GetString();
                    if (eventName == "host.ready")
                    {
                        ready.TrySetResult(value);
                    }
                    else if (value.TryGetProperty("jobId", out var jobProperty) && jobProperty.GetString() is { } jobId)
                    {
                        if (eventName == "job.progress")
                        {
                            ProgressChanged?.Invoke(this, new(
                                jobId,
                                value.GetProperty("progress").GetDouble(),
                                value.GetProperty("message").GetString() ?? string.Empty));
                        }
                        else if (eventName is "job.completed" or "job.failed" && jobs.TryGetValue(jobId, out var job))
                        {
                            job.TrySetResult(value);
                        }
                    }
                }
            }
            if (!disposed)
            {
                failure = new EndOfStreamException($"ModelHost exited unexpectedly. {RecentErrorText()}");
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        finally
        {
            if (failure is not null)
            {
                ready.TrySetException(failure);
                foreach (var response in responses.Values)
                {
                    response.TrySetException(failure);
                }
                foreach (var job in jobs.Values)
                {
                    job.TrySetException(failure);
                }
            }
        }
    }

    private async Task ReadErrorsAsync()
    {
        while (!lifetime.IsCancellationRequested)
        {
            var line = await process.StandardError.ReadLineAsync(lifetime.Token);
            if (line is null)
            {
                break;
            }
            lock (recentErrors)
            {
                recentErrors.Enqueue(line);
                while (recentErrors.Count > 20)
                {
                    recentErrors.Dequeue();
                }
            }
        }
    }

    private string RecentErrorText()
    {
        lock (recentErrors)
        {
            return string.Join(" | ", recentErrors);
        }
    }

    private static void EnsureSuccess(JsonElement response)
    {
        if (!response.TryGetProperty("ok", out var ok) || !ok.GetBoolean())
        {
            var message = response.TryGetProperty("error", out var error) && error.TryGetProperty("message", out var detail)
                ? detail.GetString()
                : "unknown protocol failure";
            throw new InvalidOperationException($"ModelHost request failed: {message}");
        }
    }

    private static (string Python, string ModelHostRoot) LocateRuntime()
    {
        var explicitPython = Environment.GetEnvironmentVariable("PULSEGEN_MODELHOST_PYTHON");
        var explicitRoot = Environment.GetEnvironmentVariable("PULSEGEN_MODELHOST_ROOT");
        if (!string.IsNullOrWhiteSpace(explicitPython) && !string.IsNullOrWhiteSpace(explicitRoot) &&
            File.Exists(explicitPython) && Directory.Exists(explicitRoot))
        {
            return (Path.GetFullPath(explicitPython), Path.GetFullPath(explicitRoot));
        }

        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            var directory = new DirectoryInfo(start);
            while (directory is not null)
            {
                var python = Path.Combine(directory.FullName, ".runtime", "modelhost", "python311", "python.exe");
                var root = Path.Combine(directory.FullName, "modelhost");
                if (File.Exists(python) && Directory.Exists(root))
                {
                    return (python, root);
                }
                directory = directory.Parent;
            }
        }

        throw new FileNotFoundException("The application-managed Python 3.11 ModelHost runtime is missing. Run scripts/bootstrap-modelhost.ps1 from the PulseGen repository.");
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }
        try
        {
            if (!process.HasExited)
            {
                try
                {
                    _ = await SendRequestAsync(new { operation = "shutdown" }, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));
                }
                catch
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync();
                }
            }
        }
        finally
        {
            disposed = true;
            lifetime.Cancel();
            await Task.WhenAll(IgnoreCancellation(readerTask), IgnoreCancellation(errorTask));
            process.Dispose();
            writeGate.Dispose();
            lifetime.Dispose();
        }
    }

    private static async Task IgnoreCancellation(Task task)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
        }
    }
}
