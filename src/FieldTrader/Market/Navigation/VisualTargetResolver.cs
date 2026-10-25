using FieldTrader.Market;
using System.Security.Cryptography;
using System.Text;

namespace FieldTrader.Navigation;

public static class VisualRegionKey { public static string For(string label,Region b)=>FormattableString.Invariant($"{label}|{b.X:F6}|{b.Y:F6}|{b.Width:F6}|{b.Height:F6}"); }
public static class CoordinateTransform {
    // Model boxes are normalized to original client pixels, NOT WPF DIPs or YOLO letterbox pixels.
    public static PixelPoint ToScreen(Region box, CapturedScreen screen) {
        if (!box.Valid || screen.Width <= 0 || screen.Height <= 0) throw new NavigationEvidenceException("Invalid geometry");
        return new(checked(screen.ScreenX + Math.Min(screen.Width - 1, (int)(Geometry.CenterX(box) * screen.Width))),
            checked(screen.ScreenY + Math.Min(screen.Height - 1, (int)(Geometry.CenterY(box) * screen.Height))));
    }
    public static Region FromLetterbox(double x, double y, double width, double height,
        int originalWidth, int originalHeight, double scale, double padX, double padY) {
        if (originalWidth <= 0 || originalHeight <= 0 || !double.IsFinite(scale) || scale <= 0)
            throw new NavigationEvidenceException("Invalid model transform");
        var r = new Region((x-padX)/scale/originalWidth, (y-padY)/scale/originalHeight,
            width/scale/originalWidth, height/scale/originalHeight);
        if (!r.Valid) throw new NavigationEvidenceException("Model box outside original image");
        return r;
    }
}

public sealed class VisualTargetResolver {
    private static readonly HashSet<string> TextRoles=["layout_menu","contact_entry","contact_profile","category_tab","product_card","detail_panel","secondary_button","contact_shop_entry","larry_category","dick_category","randall_category","materials_root","materials_category","materials_close","larry_cancel","dick_cancel","randall_cancel"];
    public static bool OcrPrimary(TargetQuery query)=>query.PreferOcr&&TextRoles.Contains(query.VisualClass);
    public static void Validate(PageObservation p) {
        var s = p.Screen; var v = p.Vision;
        if (string.IsNullOrWhiteSpace(s.Frame.Id) || string.IsNullOrWhiteSpace(s.ContentHash)
            || v.FrameId != s.Frame.Id || v.ContentHash != s.ContentHash || v.CapturedAt != s.Frame.CapturedAt
            || s.Width <= 0 || s.Height <= 0 || v.Width != s.Width || v.Height != s.Height
            || v.Regions.Concat(v.Texts).Concat(v.Items??[]).Any(t => !t.Bounds.Valid || !double.IsFinite(t.Confidence) || t.Confidence < 0 || t.Confidence > 1))
            throw new NavigationEvidenceException("Inference/frame provenance or geometry mismatch");
    }
    public LocationResult ResolveDetailed(PageObservation page,TargetQuery query) {
        Validate(page);
        if(page.Page=="Unknown"||page.Errors.Length>0)return new(LocationStatus.UNKNOWN,[],"Unknown page or unresolved prompt");
        var found=Resolve(page,query);
        var names=page.Vision.Texts.Where(t=>t.Confidence>=OcrPolicy.MinimumConfidence&&Geometry.Contains(query.Area,t.Bounds)&&Geometry.Normalize(t.Label)==Geometry.Normalize(query.Name)).ToArray();
        bool overlap=!OcrPrimary(query)&&names.Any(n=>page.Vision.Regions.Count(r=>r.Label==query.VisualClass&&r.Confidence>=query.MinimumConfidence&&Geometry.Contains(r.Bounds,n.Bounds))>1);
        var itemEvidence=query.ItemClass is {} cls&&page.Vision.ItemModel?.LocalNavigationClasses.Contains(cls)==true
            ?(page.Vision.Items??[]).Where(t=>t.Label==cls&&t.Confidence>=query.MinimumConfidence&&Geometry.Contains(query.Area,t.Bounds)).ToArray():[];
        bool itemAmbiguous=!OcrPrimary(query)&&(itemEvidence.Length>1||itemEvidence.Any(a=>(page.Vision.Items??[]).Any(b=>b.Label!=a.Label&&b.Confidence>=.8&&Geometry.IoU(a.Bounds,b.Bounds)>.5)));
        return found.Length==1&&!overlap&&!itemAmbiguous?new(LocationStatus.RESOLVED,found,"Unique class/text/context/region match"):
            found.Length>1||names.Length>1||overlap||itemAmbiguous?new(LocationStatus.AMBIGUOUS,found,"Duplicate or overlapping candidates; no click permitted"):
            new(LocationStatus.NOT_FOUND,[],"No unique visual region and OCR association");
    }
    public VisualTarget[] Resolve(PageObservation page, TargetQuery query, bool allowModalEvidence = false) {
        Validate(page);
        if (!query.Area.Valid || !query.ContextArea.Valid || string.IsNullOrWhiteSpace(query.ContextText)
            || !double.IsFinite(query.MinimumConfidence) || query.MinimumConfidence < .8 || query.MinimumConfidence > 1)
            throw new NavigationEvidenceException("Target requires valid area, visual class and context");
        if (page.Errors.Length != 0 || page.Page == "Unknown") return [];
        if (!allowModalEvidence && query.VisualClass is not ("secondary_button" or "larry_cancel" or "larry_material_viewport" or "dick_cancel" or "dick_material_viewport" or "randall_cancel" or "randall_material_viewport" or "materials_close" or "materials_quantity_plus" or "materials_quantity_minus") && page.Vision.Regions.Any(r => r.Confidence >= .6 && r.Label is "trade_panel" or "confirm_dialog")) return [];
        var words = page.Vision.Texts.Where(t => t.Confidence >= OcrPolicy.MinimumConfidence).ToArray();
        if (!words.Any(t => Geometry.Contains(query.ContextArea, t.Bounds)
            && Geometry.Normalize(t.Label) == Geometry.Normalize(query.ContextText))) return [];
        if(OcrPrimary(query)) {
            var names=words.Where(t=>Geometry.Contains(query.Area,t.Bounds)&&Geometry.Normalize(t.Label)==Geometry.Normalize(query.Name)).ToArray();
            if(names.Length!=1)return [];
            var name=names[0];var sources=new List<string>{"OCR-primary:exact text + bounded area + current context","OCR:"+name.Label};
            var support=page.LayoutRegions?.Where(r=>r.Token.Label==query.VisualClass&&Geometry.Contains(r.Token.Bounds,name.Bounds)).ToArray()??[];
            if(support.Length==1)sources.Add(support[0].Rule);
            var auxiliary=page.Vision.Regions.Where(r=>r.Label==query.VisualClass&&r.Confidence>=.6&&Geometry.Contains(r.Bounds,name.Bounds)).ToArray();
            if(auxiliary.Length==1)sources.Add("YOLO auxiliary:"+query.VisualClass);
            return [new(name.Label,query.VisualClass,name.Bounds,CoordinateTransform.ToScreen(name.Bounds,page.Screen),name.Confidence,page.Screen.Frame.Id,page.Screen.Frame.CapturedAt,page.Screen.ContentHash,sources.ToArray())];
        }
        var boxes = page.Vision.Regions.Concat((page.LayoutRegions??[]).Select(r=>r.Token)).Where(r => r.Confidence >= query.MinimumConfidence
            && r.Label == query.VisualClass && Geometry.Contains(query.Area, r.Bounds)).ToArray();
        var targets = new List<VisualTarget>();
        foreach (var box in boxes) {
            if(query.VisualClass is "materials_quantity_plus" or "materials_quantity_minus") {
                var rule=page.LayoutRegions?.SingleOrDefault(r=>r.Token==box);
                if(page.Page!="Purchase"||rule?.Rule.StartsWith("LayoutRule:materials:quantity-",StringComparison.Ordinal)!=true||MaterialMarketQuantity.Read(page.Vision) is null)continue;
                targets.Add(new(query.Name,box.Label,box.Bounds,CoordinateTransform.ToScreen(box.Bounds,page.Screen),box.Confidence,page.Screen.Frame.Id,page.Screen.Frame.CapturedAt,page.Screen.ContentHash,[rule.Rule,"OCR:购买数量 + independent quantity_field"]));
                continue;
            }
            if(query.VisualClass is "larry_goods_viewport" or "larry_material_viewport" or "dick_goods_viewport" or "dick_material_viewport" or "randall_goods_viewport" or "randall_material_viewport" or "materials_goods_viewport") {
                string expected=query.VisualClass=="materials_goods_viewport"?"MaterialMarket":(query.VisualClass is "larry_goods_viewport" or "dick_goods_viewport" or "randall_goods_viewport" or "materials_goods_viewport")?"ContactShop":"Exchange";
                var rule=page.LayoutRegions?.SingleOrDefault(r=>r.Token==box);
                if((page.Page!=expected&&!(query.VisualClass=="materials_goods_viewport"&&page.Page=="KeyMarket"))||rule?.Rule.StartsWith(query.VisualClass.StartsWith("materials_")?"LayoutRule:materials:":query.VisualClass.StartsWith("randall_")?"LayoutRule:randall:":query.VisualClass.StartsWith("dick_")?"LayoutRule:dick:":"LayoutRule:larry:",StringComparison.Ordinal)!=true)continue;
                targets.Add(new(query.Name,box.Label,box.Bounds,CoordinateTransform.ToScreen(box.Bounds,page.Screen),box.Confidence,page.Screen.Frame.Id,page.Screen.Frame.CapturedAt,page.Screen.ContentHash,[rule.Rule,"OCR context:"+query.ContextText]));
                continue;
            }
            // Optional independent item model. It never replaces the original UI detector,
            // changes OCR tokens, or supplies a click box. Its artwork must be uniquely
            // inside a high-confidence product card, with canonical OCR support on this frame.
            if(query.ItemClass is {} itemClass&&query.VisualClass=="product_card"&&page.Page=="ContactShop"
                &&page.Vision.ItemModel?.Scope=="LocalTestOnly"&&page.Vision.ItemModel.LocalNavigationClasses.Contains(itemClass)) {
                var art=(page.Vision.Items??[]).Where(t=>t.Label==itemClass&&t.Confidence>=query.MinimumConfidence&&Geometry.Contains(box.Bounds,t.Bounds)).ToArray();
                var support=page.Vision.Texts.Where(t=>t.Confidence>=OcrPolicy.MinimumConfidence&&Geometry.Normalize(t.Label)==Geometry.Normalize(query.Name)&&Geometry.Contains(box.Bounds,t.Bounds)).ToArray();
                if(art.Length==1&&support.Length==1&&boxes.Count(b=>Geometry.Contains(b.Bounds,art[0].Bounds))==1
                    &&!(page.Vision.Items??[]).Any(t=>t.Label!=itemClass&&t.Confidence>=.8&&Geometry.IoU(t.Bounds,art[0].Bounds)>.5)) {
                    targets.Add(new(query.Name,box.Label,box.Bounds,CoordinateTransform.ToScreen(box.Bounds,page.Screen),Math.Min(box.Confidence,art[0].Confidence),page.Screen.Frame.Id,page.Screen.Frame.CapturedAt,page.Screen.ContentHash,
                        ["UI-YOLO:product_card","Item-YOLO:"+itemClass+":"+page.Vision.ItemModel.Sha256,$"OCR canonical support {support[0].Confidence:F4}; independent item+UI confidence >= {query.MinimumConfidence:F2}","LocalTestOnly; no game input authorization"]));
                    continue;
                }
            }
            var names = words.Where(t => Geometry.Contains(box.Bounds, t.Bounds)
                && Geometry.Normalize(t.Label) == Geometry.Normalize(query.Name)).ToArray();
            // Multiple equal labels in one region or overlapping visual regions are ambiguous.
            if (names.Length != 1 || boxes.Count(b => Geometry.Contains(b.Bounds, names[0].Bounds)) != 1) continue;
            targets.Add(new(names[0].Label, box.Label, box.Bounds, CoordinateTransform.ToScreen(box.Bounds, page.Screen),
                Math.Min(names[0].Confidence, box.Confidence), page.Screen.Frame.Id, page.Screen.Frame.CapturedAt, page.Screen.ContentHash,[page.LayoutRegions?.FirstOrDefault(r=>r.Token==box)?.Rule??page.Vision.Recognizer+":"+box.Label,"OCR:"+names[0].Label,"context:"+query.ContextText]));
        }
        return targets.ToArray();
    }
}

public sealed class MarketPageClassifier : IPageClassifier {
    public PageObservation Classify(CapturedScreen screen, VisionResult vision) {
        var fused = MarketPerception.Fuse(vision, []);
        var errors = new List<string>();
        if (fused.Paused) errors.Add(fused.Reason);
        if (vision.Regions.Any(r => r.Label == "notification" && r.Confidence >= .6)) errors.Add("Notification requires inspection");
        if (vision.Texts.Any(t => t.Confidence >= OcrPolicy.MinimumConfidence && new[] { "失败", "错误", "异常", "不足", "验证码" }.Any(t.Label.Contains)))
            errors.Add("Error or safety prompt");
        // Conservative fingerprint: any detected geometry/category change invalidates cached coordinates.
        // No rounding: this may split batches on detector jitter, but does not hide small shifts.
        var layout = string.Join("|", vision.Regions.OrderBy(r => r.Label).ThenBy(r => r.Bounds.X).ThenBy(r => r.Bounds.Y)
            .Select(r => FormattableString.Invariant($"{r.Label}:{r.Bounds.X:R}:{r.Bounds.Y:R}:{r.Bounds.Width:R}:{r.Bounds.Height:R}")));
        return NavigationSceneAdapter.Enrich(new(screen, vision, fused.Page.ToString(), Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(layout))), errors.ToArray(),fused.Page==MarketPage.Unknown?0:.9,["MarketPerception"]));
    }
}
