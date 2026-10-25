using FieldTrader.Market;

namespace FieldTrader.Navigation;

public enum ExecutionMode { RecognizeOnly, Simulated, Real }
public enum NavigationState { IDLE, CAPTURE, RECOGNIZE, LOCATE, PLAN, EXECUTE, VERIFY, SUCCESS, RETRY, FAILED, PAUSED }
public enum ExecutionShape { SINGLE_ACTION, BATCH_ACTION }
public enum InputOperation { Click, Move, Scroll }
public enum BatchMode { StablePage, DynamicPage }
public enum LayoutEffect { Stable, MayChange }
public enum FailurePolicy { Fail, Pause, RetryFresh }
public sealed record PixelPoint(int X, int Y);
public sealed record TargetQuery(string Name, string VisualClass, Region Area, string ContextText,
    Region ContextArea, double MinimumConfidence = .95,string? ItemClass=null,bool PreferOcr=true);
public sealed record VisualTarget(string Name, string Category, Region Bounds, PixelPoint Click,
    double Confidence, string FrameId, DateTimeOffset CapturedAt, string ContentHash,string[]? Sources=null);
public sealed record LayoutRegion(VisualToken Token,string Rule,string[] Anchors);
public sealed record PageObservation(CapturedScreen Screen, VisionResult Vision, string Page,
    string Layout, string[] Errors,double Confidence=1,string[]? Sources=null,LayoutRegion[]? LayoutRegions=null);
public sealed record NavigationStep(string Id, string ExpectedBefore, TargetQuery Target,
    InputOperation Operation, string ExpectedAfter, TimeSpan Timeout,
    FailurePolicy Failure = FailurePolicy.Pause, bool AllowBatch = false,
    LayoutEffect Effect = LayoutEffect.MayChange, bool Critical = false,
    string[]? RequiredAfterText = null, int ScrollDelta = 0, TargetQuery? ExpectedResultTarget = null,string? RequiredActiveCategory=null,int? RequiredPurchaseQuantity=null);
public sealed record NavigationTask(string Id, NavigationStep[] Steps, ExecutionMode Mode,
    BatchMode BatchMode = BatchMode.DynamicPage, int MaxActions = 8,
    TimeSpan? MaxBatchTime = null, TimeSpan? ActionInterval = null, int MaxRetries = 1,
    TimeSpan? MaximumFrameAge = null,bool UseCurrentFrameForInput=false);
public sealed record PlannedAction(int Order, NavigationStep Step, VisualTarget Target);
public sealed record ActionBatch(PageObservation Source, PlannedAction[] Actions, TimeSpan Deadline,
    TimeSpan Interval, BatchMode Mode);
public sealed record NavigationEvent(DateTimeOffset At, string TaskId, NavigationState State,
    string Reason, string? FrameId = null, string? StepId = null, ExecutionShape? Shape = null);
public sealed record NavigationResult(NavigationState State, int Completed, string Reason);
public sealed record Verification(bool Passed, bool Stop, string Reason);

// Implementations must observe cancellation before emitting input and return only after input settles.
// This is a cooperative boundary: never release mouse ownership while an adapter is still executing.
public interface IInputController {
    bool IsSimulation { get; }
    Task ExecuteAsync(PlannedAction action, CapturedScreen screen, CancellationToken token);
}
public interface IPageClassifier { PageObservation Classify(CapturedScreen screen, VisionResult vision); }
public interface INavigationObserver {
    void Input(PlannedAction action,CapturedScreen screen,string status) { }
    void Raw(CapturedScreen screen,VisionResult vision) { }
    void Observation(PageObservation page);
    void Plan(ActionBatch batch);
    void Verified(PageObservation before,PageObservation after,NavigationStep step,Verification result);
}
public enum LocationStatus { RESOLVED, NOT_FOUND, AMBIGUOUS, UNKNOWN }
public sealed record LocationResult(LocationStatus Status,VisualTarget[] Candidates,string Reason);
public interface INavigationLog { void Append(NavigationEvent entry); }
// Optional diagnostics; timings never alter navigation decisions or input ownership.
public interface INavigationTiming { void Measure(string stage, double milliseconds); }

public static class NavigationInputGate {
    // Shared with legacy MarketWorkflow.TradeSession; hold for a whole navigation task, not each click.
    public static SemaphoreSlim Shared { get; } = new(1, 1);
    private sealed class Ownership { public bool Active = true; }
    private static readonly AsyncLocal<Ownership?> Owner = new();
    public static bool IsOwnedByCurrentFlow => Owner.Value?.Active == true;
    internal static void Enter() => Owner.Value = new Ownership();
    internal static void Exit() { if (Owner.Value is { } owner) owner.Active = false; Owner.Value = null; }
}
public sealed class NavigationControl : IDisposable {
    private readonly CancellationTokenSource stop = new();
    private int paused;
    public bool IsPaused => Volatile.Read(ref paused) != 0;
    public CancellationToken EmergencyToken => stop.Token;
    public void Pause() => Interlocked.Exchange(ref paused, 1);
    public void Resume() => Interlocked.Exchange(ref paused, 0); // Start a NEW RunAsync; old coordinates are discarded.
    public void EmergencyStop() => stop.Cancel(); // Latched: create a new control for a new session.
    public void Dispose() => stop.Dispose();
    internal void Check(CancellationToken token) {
        token.ThrowIfCancellationRequested();
        if (IsPaused) throw new NavigationPausedException("User paused; fresh capture required on resume");
    }
}
public sealed class NavigationPausedException(string reason) : Exception(reason);
public sealed class NavigationEvidenceException(string reason) : Exception(reason);
