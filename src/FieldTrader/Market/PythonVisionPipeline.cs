using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace FieldTrader.Market;

// One request in flight, separate OS process, bounded CPU threads, timeout kills worker.
public sealed class PythonVisionPipeline : IVisionPipeline, IAsyncDisposable {
    private readonly string configPath, script;
    private readonly SemaphoreSlim gate = new(1);
    private Process? process;
    private readonly Queue<string> diagnostics = new();
    private bool disposed;
    public int WorkerStarts {get;private set;}
    public Dictionary<string,double> StartupStages {get;private set;}=[];
    public double StartupMs { get; private set; }
    public string DiagnosticText { get { lock (diagnostics) return string.Join("\n", diagnostics); } }
    public PythonVisionPipeline(string configPath, string? script = null) {
        this.configPath = Path.GetFullPath(configPath);
        this.script = script ?? Path.Combine(AppContext.BaseDirectory, "Market", "vision_worker.py");
    }
    private async Task StartAsync(CancellationToken token) {
        if (process is { HasExited: false }) return;
        process?.Dispose();
        var cfg = VisionConfiguration.Load(configPath);
        var info = new ProcessStartInfo(cfg.PythonExecutable) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardInputEncoding = new UTF8Encoding(false), StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(configPath)! };
        info.ArgumentList.Add("-u"); info.ArgumentList.Add(script); info.ArgumentList.Add("--config"); info.ArgumentList.Add(configPath);
        info.Environment["PYTHONIOENCODING"] = "utf-8";
        process = new Process { StartInfo = info };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (diagnostics) {
            diagnostics.Enqueue(e.Data); while (diagnostics.Count > 40) diagnostics.Dequeue();
        } };
        process.Start();WorkerStarts++; process.BeginErrorReadLine();
        try { process.PriorityClass = ProcessPriorityClass.BelowNormal; } catch (InvalidOperationException) { }
        using var ready = JsonDocument.Parse(await ReadAsync(token));
        if (!ready.RootElement.TryGetProperty("Ready", out var value) || !value.GetBoolean())
            throw new InvalidDataException("模型初始化失败：" + ready.RootElement);
        StartupMs = ready.RootElement.GetProperty("StartupMs").GetDouble();
        if(ready.RootElement.TryGetProperty("Stages",out var stages))StartupStages=stages.Deserialize<Dictionary<string,double>>()??[];
    }
    private async Task<string> ReadAsync(CancellationToken token) =>
        await process!.StandardOutput.ReadLineAsync(token) ?? throw new IOException("视觉进程退出：" + DiagnosticText);
    public async Task<VisionResult> AnalyzeAsync(CapturedScreen screen, CancellationToken token) {
        await gate.WaitAsync(token);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        try {
            ObjectDisposedException.ThrowIf(disposed, this);
            var hostClock=Stopwatch.StartNew();var cold=process is null||process.HasExited;
            await StartAsync(timeout.Token);
            double initialization=cold?hostClock.Elapsed.TotalMilliseconds:0;
            var transfer=Stopwatch.StartNew();
            var request = JsonSerializer.Serialize(new { screen.Frame.Id, screen.Frame.CapturedAt, screen.Frame.ScreenshotPath, screen.ContentHash });
            await process!.StandardInput.WriteLineAsync(request.AsMemory(), timeout.Token);
            await process.StandardInput.FlushAsync(timeout.Token);
            string line = await ReadAsync(timeout.Token);
            double roundtrip=transfer.Elapsed.TotalMilliseconds;var deserialize=Stopwatch.StartNew();
            using var json = JsonDocument.Parse(line);
            if (json.RootElement.TryGetProperty("Error", out var error)) throw new InvalidDataException(error.GetString());
            var result = JsonSerializer.Deserialize<VisionResult>(line) ?? throw new InvalidDataException("视觉返回为空");
            if (result.FrameId != screen.Frame.Id || result.ContentHash != screen.ContentHash || result.CapturedAt != screen.Frame.CapturedAt
                || result.Width <= 0 || result.Height <= 0 || result.Regions is null || result.Texts is null
                || result.Regions.Concat(result.Texts).Any(t => !t.Bounds.Valid || !double.IsFinite(t.Confidence) || t.Confidence < 0 || t.Confidence > 1))
                throw new InvalidDataException("视觉结果与截图不一致或坐标非法");
            if(result.Performance is {} profile) {
                var stages=new Dictionary<string,double>(profile.Stages){["WorkerInitializationWaitMs"]=initialization,["IpcResidualMs"]=Math.Max(0,roundtrip-result.TotalMs),["DeserializeMs"]=deserialize.Elapsed.TotalMilliseconds,["HostRecognitionMs"]=hostClock.Elapsed.TotalMilliseconds};
                if(cold)foreach(var pair in StartupStages)stages[pair.Key]=pair.Value;
                result=result with{Performance=profile with{Stages=stages}};
            }
            return result;
        } catch(OperationCanceledException) when(!token.IsCancellationRequested) {Kill();throw new TimeoutException("视觉进程60秒内未响应，已终止并暂停采集");}
        catch { Kill(); throw; } finally { gate.Release(); }
    }
    private void Kill() { try { if (process is { HasExited: false }) process.Kill(true); } catch (InvalidOperationException) { } }
    public async ValueTask DisposeAsync() {
        await gate.WaitAsync();
        try { disposed = true; Kill(); process?.Dispose(); process = null; } finally { gate.Release(); }
    }
}
