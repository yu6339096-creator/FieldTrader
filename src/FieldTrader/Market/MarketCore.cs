namespace FieldTrader.Market;

public enum MarketPage { Unknown, Market, Purchase, Warehouse, Contact, Exchange, Sell, Listings, Receipt, ExchangeConfirm, SellBundle }
public enum Route { MaterialExchange, Ammunition, Key, Direct }
public enum TradeState { Ready, Simulated, Rejected, Unknown, Stopped }
public sealed record Region(double X, double Y, double Width, double Height) {
    public bool Valid => new[] { X, Y, Width, Height }.All(double.IsFinite) && X >= 0 && Y >= 0 && Width > 0 && Height > 0 && X + Width <= 1 && Y + Height <= 1;
}
public sealed record VisualToken(string Label, double Confidence, Region Bounds);
public sealed record Frame(string Id, DateTimeOffset CapturedAt, string ScreenshotPath);
public sealed record Detection(string FrameId, MarketPage Page, string ItemId, VisualToken[] Tokens);
public sealed record PageEvidence(Frame Frame, MarketPage Page, string ItemId, VisualToken[] Targets);
public interface IMarketCapture { Task<Frame> CaptureAsync(CancellationToken cancellation); }
public interface IMarketVision { Task<Detection> DetectAsync(Frame frame, CancellationToken cancellation); }
public static class EvidenceFusion {
    // Both engines must independently agree on this frame, page and canonical item ID.
    public static PageEvidence Fuse(Frame frame, Detection yolo, Detection ocr) {
        var valid = yolo.FrameId == frame.Id && ocr.FrameId == frame.Id && yolo.Page == ocr.Page
            && yolo.Page != MarketPage.Unknown && yolo.ItemId == ocr.ItemId && !string.IsNullOrWhiteSpace(ocr.ItemId);
        var targets = ocr.Tokens.Where(t => t.Confidence >= OcrPolicy.MinimumConfidence && t.Confidence <= 1 && t.Bounds.Valid
            && yolo.Tokens.Any(v => v.Label == t.Label && v.Confidence >= .9 && v.Confidence <= 1 && v.Bounds.Valid
                && Math.Abs(v.Bounds.X - t.Bounds.X) < .03 && Math.Abs(v.Bounds.Y - t.Bounds.Y) < .03)).ToArray();
        return new(frame, valid ? ocr.Page : MarketPage.Unknown, valid ? ocr.ItemId : "", valid ? targets : []);
    }
}
public sealed record Economics(decimal PurchaseCost, decimal RecommendedRevenue, decimal FeeRate,
    decimal ListingFee, decimal Reserve, decimal Discount = .1m) {
    public decimal ConservativeProfit() {
        if (PurchaseCost < 0 || RecommendedRevenue <= 0 || FeeRate < 0 || FeeRate > 1 || ListingFee < 0 || Reserve < 0 || Discount < 0 || Discount >= 1)
            throw new ArgumentOutOfRangeException(nameof(Economics));
        return RecommendedRevenue * (1 - Discount) * (1 - FeeRate) - PurchaseCost - ListingFee - Reserve;
    }
}
public sealed record ScanTarget(string Id, DateTimeOffset LastVisited, int Priority, int PreferredHour);
public static class Patrol {
    // Oldest first guarantees broad coverage; time preference orders equal-age targets.
    public static IEnumerable<ScanTarget> Order(IEnumerable<ScanTarget> targets, DateTimeOffset localTime) =>
        targets.OrderBy(t => t.LastVisited).ThenByDescending(t => t.Priority + (t.PreferredHour == localTime.Hour ? 100 : 0)).ThenBy(t => t.Id, StringComparer.Ordinal);
}
public sealed record TradeStep(string Id, string ItemId, MarketPage Before, MarketPage After, string Target, int Quantity, decimal Cost, bool Listing, bool PriceChangedRejected = false);
public sealed record SimulationEvent(string StepId, TradeState State, string Reason, DateTimeOffset At);
public sealed class MarketExecutor {
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly HashSet<string> attempted = [];
    private int stopped;
    public TradeState State { get; private set; } = TradeState.Ready;
    public decimal ReservedBudget { get; private set; }
    public int ReservedListings { get; private set; }
    public void Stop() => Interlocked.Exchange(ref stopped, 1);
    public async Task<SimulationEvent> SimulateAsync(TradeStep step, PageEvidence before, PageEvidence after,
        decimal budget, int? ocrRemainingListings, DateTimeOffset now, CancellationToken cancellation) {
        await gate.WaitAsync(cancellation);
        try {
            cancellation.ThrowIfCancellationRequested();
            if (Volatile.Read(ref stopped) != 0) return Result(step, TradeState.Stopped, "stop switch", now);
            if (State == TradeState.Unknown) return Result(step, State, "manual reconciliation required", now);
            if (!attempted.Add(step.Id)) return Result(step, TradeState.Rejected, "duplicate step", now);
            if (step.PriceChangedRejected) return Result(step, TradeState.Rejected, "price changed; normal rejection, do not chase", now);
            bool Fresh(PageEvidence e) => now >= e.Frame.CapturedAt && now - e.Frame.CapturedAt <= TimeSpan.FromSeconds(3);
            if (!Fresh(before) || !Fresh(after) || before.Page != step.Before || after.Page != step.After
                || before.Page == MarketPage.Unknown || after.Page == MarketPage.Unknown
                || before.ItemId != step.ItemId || after.ItemId != step.ItemId || before.Frame.Id == after.Frame.Id
                || after.Frame.CapturedAt <= before.Frame.CapturedAt
                || before.Targets.Count(t => t.Label == step.Target && t.Bounds.Valid && t.Confidence >= OcrPolicy.MinimumConfidence) != 1)
                return Result(step, TradeState.Unknown, "unverified page or ambiguous target", now);
            if (step.Quantity < 1 || step.Cost < 0 || budget < 0 || step.Cost + ReservedBudget > budget
                || (step.Listing && (step.Quantity > 5 || ocrRemainingListings is null || ocrRemainingListings <= ReservedListings)))
                return Result(step, TradeState.Rejected, "quantity, budget or OCR quota", now);
            ReservedBudget += step.Cost;
            if (step.Listing) ReservedListings++;
            return Result(step, TradeState.Simulated, "simulation only; no input emitted", now);
        } catch { State = TradeState.Unknown; throw; }
        finally { gate.Release(); }
    }
    private SimulationEvent Result(TradeStep step, TradeState state, string reason, DateTimeOffset now) {
        State = state; return new(step.Id, state, reason, now);
    }
}
