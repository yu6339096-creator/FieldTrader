using FieldTrader.Market;

namespace FieldTrader.Navigation;

public sealed class NavigationVerifier(VisualTargetResolver resolver) {
    private static string? Category(NavigationStep step,VisionResult v)=>step.Id.StartsWith("keys:",StringComparison.Ordinal)?KeyMarketClassifier.ActiveCategory(v):step.Id.StartsWith("materials:",StringComparison.Ordinal)?MaterialMarketClassifier.ActiveCategory(v):step.Id.StartsWith("randall:",StringComparison.Ordinal)?RandallPageClassifier.ActiveCategory(v):step.Id.StartsWith("dick:",StringComparison.Ordinal)?DickPageClassifier.ActiveCategory(v):LarryPageClassifier.ActiveCategory(v);
    public Verification Before(PageObservation source, PageObservation current, PlannedAction action,
        DateTimeOffset now, TimeSpan maxAge,bool newActionFromCurrentFrame=false) {
        VisualTargetResolver.Validate(source); VisualTargetResolver.Validate(current);
        var s = source.Screen; var c = current.Screen; var target = action.Target;
        // In stepwise mode the old plan is only a geometry/context comparison, and is
        // discarded after this check. The newly located current-frame action alone may
        // reach input. Legacy batches still require the original source to be fresh.
        if (maxAge <= TimeSpan.Zero || now < s.Frame.CapturedAt || !newActionFromCurrentFrame && now - s.Frame.CapturedAt > maxAge
            || now < c.Frame.CapturedAt || now - c.Frame.CapturedAt > maxAge)
            return new(false, true, "Expired/future frame; recapture required");
        if (target.FrameId != s.Frame.Id || target.ContentHash != s.ContentHash || target.CapturedAt != s.Frame.CapturedAt
            || target.Click != CoordinateTransform.ToScreen(target.Bounds, s)) return new(false, true, "Target provenance mismatch");
        if (current.Errors.Length > 0 || current.Page != action.Step.ExpectedBefore || !VisualTargetResolver.OcrPrimary(action.Step.Target)&&source.Layout != current.Layout
            || s.WindowHandle != c.WindowHandle || s.ScreenX != c.ScreenX || s.ScreenY != c.ScreenY
            || s.Width != c.Width || s.Height != c.Height || s.Live != c.Live)
            return new(false, true, "Page/layout/window changed; discard remaining coordinates");
        var found = resolver.Resolve(current, action.Step.Target);
        if (found.Length != 1 || found[0].Bounds != target.Bounds || found[0].Click != target.Click)
            return new(false, true, "Target moved/disappeared/ambiguous");
        if(found[0].Category is "materials_quantity_plus" or "materials_quantity_minus") {
            var n=MaterialMarketQuantity.Read(current.Vision);
            if(n is null||action.Step.RequiredPurchaseQuantity!=n+(found[0].Category=="materials_quantity_plus"?1:-1))return new(false,true,"Quantity changed before action; discard old increment");
        }
        NavigationSafety.Check(action.Step, found[0]);
        return new(true, false, "Before-action evidence verified");
    }
    public Verification After(PageObservation before, PageObservation after, NavigationStep step) {
        VisualTargetResolver.Validate(after);
        if (after.Screen.Frame.Id == before.Screen.Frame.Id || after.Screen.Frame.CapturedAt <= before.Screen.Frame.CapturedAt)
            return new(false, true, "Verification requires a newer independent frame");
        if (before.Screen.WindowHandle != after.Screen.WindowHandle || before.Screen.ScreenX != after.Screen.ScreenX
            || before.Screen.ScreenY != after.Screen.ScreenY || before.Screen.Width != after.Screen.Width
            || before.Screen.Height != after.Screen.Height || before.Screen.Live != after.Screen.Live)
            return new(false, true, "Window changed during action");
        if (after.Errors.Length > 0) return new(false, true, string.Join("; ", after.Errors));
        if (after.Page != step.ExpectedAfter) return new(false, true, "Expected page not reached");
        if(step.RequiredPurchaseQuantity is {} quantity&&MaterialMarketQuantity.Read(after.Vision)!=quantity)return new(false,true,"Requested quantity not observed in fresh frame");
        if(step.RequiredActiveCategory is {} category&&Category(step,after.Vision)!=category)return new(false,true,"Expected category not selected in fresh pixels");
        if (step.ExpectedResultTarget is { } expected && resolver.Resolve(after, expected, allowModalEvidence: true).Length != 1)
            return new(false, true, "Expected result target is absent, ambiguous or outside its visual context");
        if (step.Effect == LayoutEffect.Stable && !VisualTargetResolver.OcrPrimary(step.Target) && before.Layout != after.Layout)
            return new(false, true, "Unexpected layout change inside stable batch");
        if ((step.RequiredAfterText ?? []).Any(text => !after.Vision.Texts.Any(t => t.Confidence >= OcrPolicy.MinimumConfidence
            && Geometry.Normalize(t.Label) == Geometry.Normalize(text)))) return new(false, true, "Expected result evidence missing");
        if (step.Operation == InputOperation.Click && step.ExpectedAfter == step.ExpectedBefore
            && !(step.RequiredPurchaseQuantity is {} expectedQuantity&&MaterialMarketQuantity.Read(before.Vision)!=expectedQuantity&&MaterialMarketQuantity.Read(after.Vision)==expectedQuantity)
            && !(step.RequiredActiveCategory is {} selected&&Category(step,before.Vision)!=selected&&Category(step,after.Vision)==selected)
            && !(step.RequiredAfterText ?? []).Any(text => !before.Vision.Texts.Any(t => t.Confidence >= OcrPolicy.MinimumConfidence
                && Geometry.Normalize(t.Label) == Geometry.Normalize(text)))
            && !(step.ExpectedResultTarget is {} changedTarget&&resolver.Resolve(before,changedTarget,allowModalEvidence:true).Length!=1))
            return new(false, true, "Same-page click lacks a new positive result anchor");
        if(step.Operation==InputOperation.Scroll&&step.ExpectedAfter==step.ExpectedBefore) {
            var target=resolver.Resolve(before,step.Target);
            bool contentChanged=false;
            if(target.Length==1) {
                var key=VisualRegionKey.For(target[0].Category,target[0].Bounds);
                contentChanged=before.Vision.RegionContent?.TryGetValue(key,out var oldHash)==true&&after.Vision.RegionContent?.TryGetValue(key,out var newHash)==true&&oldHash!=newHash;
            }
            bool newAnchor=(step.RequiredAfterText??[]).Any(text=>after.Vision.Texts.Any(t=>t.Confidence >= OcrPolicy.MinimumConfidence&&Geometry.Normalize(t.Label)==Geometry.Normalize(text))&&!before.Vision.Texts.Any(t=>t.Confidence >= OcrPolicy.MinimumConfidence&&Geometry.Normalize(t.Label)==Geometry.Normalize(text)));
            if(target.Length==1&&target[0].Category is "larry_goods_viewport" or "larry_material_viewport" or "dick_goods_viewport" or "dick_material_viewport" or "randall_goods_viewport" or "randall_material_viewport" or "materials_goods_viewport")
                contentChanged=LarryRoutes.ViewSignature(before,target[0].Bounds)!=LarryRoutes.ViewSignature(after,target[0].Bounds);
            if(!contentChanged&&!newAnchor)return new(false,true,"No observed result change in scroll viewport; unrelated pixels/hover are insufficient");
        }
        // Same-page dynamic operations need positive change evidence, not just a completed click.
        if (step.Effect == LayoutEffect.MayChange && step.ExpectedAfter == step.ExpectedBefore
            && before.Screen.ContentHash == after.Screen.ContentHash) return new(false, true, "No observed result change");
        return new(true, false, "Page, result anchors and layout verified; no transaction claim");
    }
}
