using System.IO;
using FieldTrader.Market;
using System.Text.Json;
using System.Diagnostics;

namespace FieldTrader.Navigation;

public sealed class JsonNavigationLog(string path) : INavigationLog {
    private readonly object gate = new();
    public void Append(NavigationEvent entry) {
        lock (gate) {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
            var bytes = System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(entry) + "\n");
            stream.Write(bytes); stream.Flush(true);
        }
    }
}
public sealed class SimulatedInputController : IInputController {
    private readonly List<PlannedAction> actions = [];
    public bool IsSimulation => true;
    public PlannedAction[] Actions { get { lock (actions) return actions.ToArray(); } }
    public Task ExecuteAsync(PlannedAction action, CapturedScreen screen, CancellationToken token) {
        token.ThrowIfCancellationRequested(); NavigationSafety.Check(action.Step, action.Target);
        lock (actions) actions.Add(action);
        return Task.CompletedTask;
    }
}

// Owns the mouse for an entire task. No audio dependency, UI thread wait, or automatic trade commit.
public sealed class NavigationExecutor(IScreenSource capture, IVisionPipeline vision, IPageClassifier classifier,
    IInputController input, INavigationLog log) {
    public async Task<NavigationResult> RunAsync(NavigationTask task, NavigationControl control, CancellationToken token = default) {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, control.EmergencyToken);
        var ct = linked.Token;
        int completed = 0, retries = 0;
        bool acquired = false, uncertainInput = false;
        var frameIds = new HashSet<string>(StringComparer.Ordinal);
        DateTimeOffset lastCapture = DateTimeOffset.MinValue;
        var resolver = new VisualTargetResolver();
        var planner = new BatchActionPlanner(new NavigationPlanner(resolver));
        var verifier = new NavigationVerifier(resolver);
        var context=new OcrNavigationContext();
        T Measure<T>(string stage, Func<T> operation) {
            var timer = Stopwatch.StartNew();
            try { return operation(); } finally { (log as INavigationTiming)?.Measure(stage, timer.Elapsed.TotalMilliseconds); }
        }
        async Task<T> MeasureAsync<T>(string stage, Func<Task<T>> operation) {
            var timer = Stopwatch.StartNew();
            try { return await operation(); } finally { (log as INavigationTiming)?.Measure(stage, timer.Elapsed.TotalMilliseconds); }
        }
        void Record(NavigationState state, string reason, PageObservation? p = null, string? step = null, ExecutionShape? shape = null) =>
            log.Append(new(DateTimeOffset.UtcNow, task.Id, state, reason, p?.Screen.Frame.Id, step, shape));
        NavigationResult Finish(NavigationState state, string reason) { Record(state, reason); return new(state, completed, reason); }
        async Task<PageObservation> Observe(CancellationToken cancellation) {
            control.Check(cancellation);
            Record(NavigationState.CAPTURE, "Capture independent evidence");
            var screen = await MeasureAsync("Capture", () => capture.CaptureAsync(cancellation).WaitAsync(cancellation));
            if (!frameIds.Add(screen.Frame.Id) || screen.Frame.CapturedAt <= lastCapture)
                throw new NavigationEvidenceException("Capture reused an old frame ID/time");
            lastCapture = screen.Frame.CapturedAt;
            Record(NavigationState.RECOGNIZE, "Reuse IVisionPipeline; both engines share this frame");
            var raw = await MeasureAsync("Recognition", () => vision.AnalyzeAsync(screen, cancellation).WaitAsync(cancellation));
            // File replays have no dimensions until inference decodes them.
            if (!screen.Live && screen.Width == 0 && screen.Height == 0) screen = screen with { Width = raw.Width, Height = raw.Height };
            (log as INavigationObserver)?.Raw(screen,raw);
            var p = Measure("Fusion", () => context.Reconcile(classifier.Classify(screen, raw)));
            VisualTargetResolver.Validate(p);
            (log as INavigationObserver)?.Observation(p);
            var now = DateTimeOffset.UtcNow;
            if (task.Mode != ExecutionMode.RecognizeOnly && (now < screen.Frame.CapturedAt
                || now - screen.Frame.CapturedAt > (task.MaximumFrameAge ?? TimeSpan.FromSeconds(5))))
                throw new NavigationPausedException("Expired/future observation; recapture required");
            if (task.Mode == ExecutionMode.Real && !screen.Live)
                throw new NavigationEvidenceException("Real navigation requires live capture");
            control.Check(cancellation);
            return p;
        }
        try {
            Record(NavigationState.IDLE, "Waiting for exclusive navigation ownership");
            if (!Enum.IsDefined(task.Mode) || !Enum.IsDefined(task.BatchMode) || task.Steps.Length == 0
                || task.Steps.Select(s => s.Id).Distinct().Count() != task.Steps.Length)
                throw new ArgumentException("Task requires unique steps and valid modes");
            if (task.Mode == ExecutionMode.Real && input.IsSimulation || task.Mode == ExecutionMode.Simulated && !input.IsSimulation)
                throw new NavigationEvidenceException("Execution mode/input adapter mismatch");
            await NavigationInputGate.Shared.WaitAsync(ct); acquired = true; NavigationInputGate.Enter();
            while (completed < task.Steps.Length) {
                var first = task.Steps[completed];
                using var planningTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                planningTimeout.CancelAfter(first.Timeout > TimeSpan.Zero ? first.Timeout : TimeSpan.FromMilliseconds(1));
                try {
                    control.Check(ct);
                    var source = await Observe(planningTimeout.Token);
                    Record(NavigationState.LOCATE, "Locate current OCR text in bounded context; YOLO auxiliary", source, first.Id);
                    var batch = Measure("Planning", () => planner.Plan(source, task, completed));
                    (log as INavigationObserver)?.Plan(batch);
                    Record(NavigationState.PLAN, $"{batch.Mode}: {batch.Actions.Length} ordered actions", source);
                    if (task.Mode == ExecutionMode.RecognizeOnly)
                        return Finish(NavigationState.SUCCESS, "Recognition/plan only; no input and no navigation completion claimed");
                    using var batchTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    batchTimeout.CancelAfter(batch.Deadline);
                    var shape = batch.Actions.Length == 1 ? ExecutionShape.SINGLE_ACTION : ExecutionShape.BATCH_ACTION;
                    foreach (var action in batch.Actions) {
                        using var actionTimeout = CancellationTokenSource.CreateLinkedTokenSource(batchTimeout.Token);
                        actionTimeout.CancelAfter(action.Step.Timeout);
                        var at = actionTimeout.Token;
                        control.Check(at);
                        // Guard every boundary (including critical pre-check). Reuse the planned coordinates
                        // only while a NEW observation confirms exact geometry, target identity and context.
                        var before = await Observe(at);
                        var valid = Measure("BeforeVerification", () => verifier.Before(source, before, action, DateTimeOffset.UtcNow,
                            task.MaximumFrameAge ?? TimeSpan.FromSeconds(5),task.UseCurrentFrameForInput));
                        if (!valid.Passed) throw new NavigationPausedException(valid.Reason);
                        // Optional stepwise mode: after equality checks on page/window/layout/target,
                        // resolve on the newly recognized boundary frame. Native freshness remains
                        // measured from that actual frame; never rewrite an old target's provenance.
                        var executionAction=task.UseCurrentFrameForInput?new NavigationPlanner(resolver).Locate(before,action.Step,action.Order):action;
                        var executionScreen=task.UseCurrentFrameForInput?before.Screen:source.Screen;
                        if(task.UseCurrentFrameForInput)(log as INavigationObserver)?.Plan(new(before,[executionAction],batch.Deadline,batch.Interval,BatchMode.DynamicPage));
                        Record(NavigationState.EXECUTE, action.Step.Operation.ToString(), before, action.Step.Id, shape);
                        control.Check(at);
                        uncertainInput = true;
                        (log as INavigationObserver)?.Input(executionAction,executionScreen,"Attempted");
                        await MeasureAsync("Mouse", async () => { await input.ExecuteAsync(executionAction, executionScreen, at); return true; });
                        context.Issued(executionAction,executionScreen);
                        (log as INavigationObserver)?.Input(executionAction,executionScreen,"AdapterReturned; result unverified");
                        control.Check(at);
                        if (batch.Interval > TimeSpan.Zero) await Task.Delay(batch.Interval, at);
                        Record(NavigationState.VERIFY, "Independent post-action capture (also batch boundary guard)", before, action.Step.Id);
                        var verificationTimer = Stopwatch.StartNew();
                        var after = await Observe(at);
                        var result = Measure("AfterVerification", () => verifier.After(before, after, action.Step));
                        (log as INavigationTiming)?.Measure("PageVerification", verificationTimer.Elapsed.TotalMilliseconds);
                        (log as INavigationObserver)?.Verified(before,after,action.Step,result);
                        if (!result.Passed) throw new NavigationPausedException(result.Reason);
                        control.Check(at);
                        completed++; uncertainInput = false; retries = 0;
                        // BatchActionPlanner guarantees layout-changing/critical action is the last action.
                    }
                } catch (NavigationEvidenceException ex) when (!uncertainInput && task.Steps[completed].Failure == FailurePolicy.RetryFresh && retries < task.MaxRetries) {
                    retries++; Record(NavigationState.RETRY, ex.Message + "; new capture mandatory", step: first.Id);
                }
            }
            return Finish(NavigationState.SUCCESS, "All navigation steps independently verified");
        } catch (NavigationPausedException ex) { return Finish(NavigationState.PAUSED, ex.Message); }
        catch (OperationCanceledException) { return Finish(NavigationState.PAUSED,
            ct.IsCancellationRequested ? "Cancelled/emergency stop; discard batch" : "Timeout; discard batch and inspect current page"); }
        catch (Exception ex) {
            var policy = completed < task.Steps.Length ? task.Steps[completed].Failure : FailurePolicy.Fail;
            return Finish(uncertainInput || policy == FailurePolicy.Pause ? NavigationState.PAUSED : NavigationState.FAILED, ex.Message);
        } finally { if (acquired) { NavigationInputGate.Exit(); NavigationInputGate.Shared.Release(); } }
    }
}
