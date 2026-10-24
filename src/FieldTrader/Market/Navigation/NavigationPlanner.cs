using FieldTrader.Market;

namespace FieldTrader.Navigation;

public static class NavigationSafety {
    private static readonly HashSet<string> AllowedClasses = new(StringComparer.Ordinal) {
        "product_card", "material_card", "material_row", "market_entry", "secondary_button", "listed_item",
        "layout_menu", "contact_entry", "category_tab", "next_page" // Last two require calibrated future models, not assumed available today.
    };
    public static void Check(NavigationStep step, VisualTarget target) {
        if (!Enum.IsDefined(step.Operation) || !AllowedClasses.Contains(target.Category)
            || target.Category != step.Target.VisualClass || Geometry.Normalize(target.Name) != Geometry.Normalize(step.Target.Name)
            || new[] { "购买", "出售", "兑换", "上架", "确认", "提交", "buy", "sell", "confirm", "exchange", "purchase" }
                .Any(word => Geometry.Normalize(target.Name).Contains(word, StringComparison.Ordinal)))
            throw new NavigationEvidenceException("Transaction/unknown target forbidden in navigation queue");
        if (target.Category == "secondary_button" && !new[] { "关闭", "返回", "取消", "close", "back", "cancel" }.Contains(Geometry.Normalize(target.Name)))
            throw new NavigationEvidenceException("Secondary button must be an explicit close/back/cancel action");
        if (step.Operation == InputOperation.Scroll && (step.ScrollDelta == 0 || Math.Abs((long)step.ScrollDelta) > 1200 || step.Effect != LayoutEffect.MayChange))
            throw new NavigationEvidenceException("Scroll must be bounded and end the batch");
        if (step.Timeout <= TimeSpan.Zero || step.Timeout > TimeSpan.FromMinutes(2))
            throw new NavigationEvidenceException("Invalid step timeout");
    }
}

public sealed class NavigationPlanner(VisualTargetResolver resolver) {
    public PlannedAction Locate(PageObservation page, NavigationStep step, int order) {
        if (page.Page != step.ExpectedBefore || page.Errors.Length > 0) throw new NavigationEvidenceException("Unexpected starting page or error prompt");
        var located=resolver.ResolveDetailed(page,step.Target);
        var targets=located.Candidates;
        if(located.Status!=LocationStatus.RESOLVED)throw new NavigationEvidenceException(located.Status+": Target absent or ambiguous: "+step.Target.Name+" · "+located.Reason);
        NavigationSafety.Check(step, targets[0]);
        return new(order, step, targets[0]);
    }
}

public sealed class BatchActionPlanner(NavigationPlanner planner) {
    public ActionBatch Plan(PageObservation source, NavigationTask task, int offset) {
        if (task.MaxActions < 1 || task.MaxActions > 32 || task.MaxRetries < 0 || task.MaxRetries > 5)
            throw new ArgumentException("Invalid batch/retry limits");
        var duration = task.MaxBatchTime ?? TimeSpan.FromSeconds(10);
        var interval = task.ActionInterval ?? TimeSpan.FromMilliseconds(100);
        if (duration <= TimeSpan.Zero || duration > TimeSpan.FromMinutes(2) || interval < TimeSpan.Zero || interval > duration)
            throw new ArgumentException("Invalid timing limits");
        var result = new List<PlannedAction>();
        foreach (var step in task.Steps.Skip(offset).Take(task.MaxActions)) {
            if (result.Count > 0 && (!step.AllowBatch || step.Critical || step.ExpectedBefore != source.Page)) break;
            if (result.Count > 0 && task.BatchMode == BatchMode.StablePage && step.Effect == LayoutEffect.MayChange) break;
            // Locate only the current segment: future pages MUST be resolved after a new capture.
            result.Add(planner.Locate(source, step, result.Count));
            if (!step.AllowBatch || step.Critical || step.Effect == LayoutEffect.MayChange || step.ExpectedAfter != source.Page) break;
        }
        if (result.Count == 0) throw new NavigationEvidenceException("Empty batch");
        return new(source, result.ToArray(), duration, interval, result.Any(a => a.Step.Effect == LayoutEffect.MayChange) ? BatchMode.DynamicPage : task.BatchMode);
    }
}
