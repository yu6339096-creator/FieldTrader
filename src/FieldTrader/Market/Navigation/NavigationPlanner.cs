using FieldTrader.Market;

namespace FieldTrader.Navigation;

public static class NavigationSafety {
    private static readonly HashSet<string> AllowedClasses = new(StringComparer.Ordinal) {
        "product_card", "material_card", "material_row", "market_entry", "secondary_button", "listed_item",
        "layout_menu", "contact_entry", "category_tab", "next_page",
        "materials_quantity_plus", "materials_quantity_minus", "materials_root", "materials_category", "materials_close", "materials_goods_viewport", "randall_category", "randall_cancel", "randall_goods_viewport", "randall_material_viewport", "dick_category", "dick_cancel", "dick_goods_viewport", "dick_material_viewport", "contact_shop_entry", "larry_category", "larry_cancel", "larry_goods_viewport", "larry_material_viewport"
    };
    public static void Check(NavigationStep step, VisualTarget target) {
        bool shopEntry=target.Category=="contact_shop_entry" && target.Name=="购买" && (step.Id=="larry:shop"||step.Id=="dick:shop"||step.Id=="randall:shop")
            && step.ExpectedBefore=="ContactProfile" && step.ExpectedAfter=="ContactShop" && step.Operation==InputOperation.Click
            && target.Sources?.Any(s=>(step.Id=="larry:shop"&&s.StartsWith("LayoutRule:larry:profile-buy-is-shop-entry;",StringComparison.Ordinal)||step.Id=="dick:shop"&&s.StartsWith("LayoutRule:dick:profile-buy-is-shop-entry;",StringComparison.Ordinal)||step.Id=="randall:shop"&&s.StartsWith("LayoutRule:randall:profile-buy-is-shop-entry;",StringComparison.Ordinal)))==true;
        if (!Enum.IsDefined(step.Operation) || !AllowedClasses.Contains(target.Category)
            || target.Category != step.Target.VisualClass || Geometry.Normalize(target.Name) != Geometry.Normalize(step.Target.Name)
            || new[] { "购买", "出售", "兑换", "上架", "确认", "确定", "补齐", "刷新", "获取限时商品", "提交", "buy", "sell", "confirm", "exchange", "purchase" }
                .Any(word => Geometry.Normalize(target.Name).Contains(word, StringComparison.Ordinal)) && !shopEntry)
            throw new NavigationEvidenceException("Transaction/unknown target forbidden in navigation queue");
        if(target.Category=="contact_shop_entry"&&!shopEntry)throw new NavigationEvidenceException("Shop entry requires Larry profile role and exact navigation transition");
        if(target.Category is "materials_quantity_plus" or "materials_quantity_minus") {
            if(!step.Id.StartsWith("materials:quantity:",StringComparison.Ordinal)||step.Operation!=InputOperation.Click||step.ExpectedBefore!="Purchase"||step.ExpectedAfter!="Purchase"||step.RequiredPurchaseQuantity is not >0)throw new NavigationEvidenceException("Quantity controls require explicit before/after quantity contract");
        }
        if(target.Category=="materials_close"&&(target.Name!="关闭"||step.ExpectedBefore!="Purchase"||(step.ExpectedAfter!="MaterialMarket"&&step.ExpectedAfter!="KeyMarket")||step.Operation!=InputOperation.Click))throw new NavigationEvidenceException("Only close may leave material purchase detail");
        if((target.Category is "larry_cancel" or "dick_cancel" or "randall_cancel")&&(target.Name!="取消"||step.ExpectedBefore!="Exchange"||step.ExpectedAfter!="ContactShop"||step.Operation!=InputOperation.Click))throw new NavigationEvidenceException("Only cancel may leave Larry exchange detail");
        if(target.Category is "larry_goods_viewport" or "larry_material_viewport" or "dick_goods_viewport" or "dick_material_viewport" or "randall_goods_viewport" or "randall_material_viewport" or "materials_goods_viewport" && step.Operation!=InputOperation.Scroll)throw new NavigationEvidenceException("Viewport is scroll-only; cannot submit or click");
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
        if(targets[0].Category is "materials_quantity_plus" or "materials_quantity_minus") {
            var current=MaterialMarketQuantity.Read(page.Vision);
            if(current is null||step.RequiredPurchaseQuantity!=current+(targets[0].Category=="materials_quantity_plus"?1:-1))throw new NavigationEvidenceException("Quantity step must be one observed increment/decrement");
        }
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
