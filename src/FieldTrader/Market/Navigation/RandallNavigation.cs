using System.Security.Cryptography;
using System.Text;
using FieldTrader.Market;
namespace FieldTrader.Navigation;

// Calibrated layout evidence remains explicitly separate from the unchanged 14 YOLO classes.
public sealed class RandallPageClassifier : IPageClassifier {
    public const string Contact = "兰德尔·费舍";
    public static readonly string[] Categories = ["头套面罩", "耳机"];
    public static string? ActiveCategory(VisionResult vision) {
        var matches=Categories.Where(c=>vision.PixelMeasurements?.TryGetValue("RandallTabOrange:"+c,out var fraction)==true&&fraction>=.15).ToArray();
        return matches.Length==1?matches[0]:null;
    }
    public static VisualToken[] Unique(IEnumerable<VisualToken> tokens) {
        var result = new List<VisualToken>();
        foreach (var t in tokens.OrderByDescending(t => t.Confidence)) {
            if (!result.Any(r => Geometry.Normalize(r.Label) == Geometry.Normalize(t.Label)
                && Math.Abs(Geometry.CenterX(r.Bounds)-Geometry.CenterX(t.Bounds)) < .009
                && Math.Abs(Geometry.CenterY(r.Bounds)-Geometry.CenterY(t.Bounds)) < .009)) result.Add(t);
        }
        return result.ToArray();
    }
    public PageObservation Classify(CapturedScreen screen, VisionResult raw) {
        var vision = raw with { Texts = Unique(raw.Texts) };
        var p = new MarketPageClassifier().Classify(screen, vision);
        if (p.Errors.Length > 0) return p with { Page = "Unknown" };
        if(vision.Regions.Any(r=>r.Label=="confirm_dialog"&&r.Confidence>=.6))return p with{Page="Unknown",Errors=["Unexpected confirmation dialog; stop Randall navigation"]};
        var words = vision.Texts.Where(t => t.Confidence >= OcrPolicy.MinimumConfidence).ToArray();
        VisualToken[] Find(string name, Region area) => vision.Texts.Where(t => t.Confidence>=OcrPolicy.MinimumConfidence && Geometry.Contains(area,t.Bounds) && Geometry.Normalize(t.Label)==Geometry.Normalize(name)).ToArray();
        bool Has(string name) => words.Any(t=>Geometry.Normalize(t.Label)==Geometry.Normalize(name));
        var regions = new List<LayoutRegion>(p.LayoutRegions ?? []);
        string page = p.Page; var full = new Region(0,0,1,1);
        void Add(string kind,Region box,string rule,params string[] anchors) { if(box.Valid)regions.Add(new(new(kind,.95,box),"LayoutRule:randall:"+rule+"; NOT YOLO",anchors)); }
        bool detail = Has("道具兑换") && (Has("需要提交以下道具")||words.Any(t=>t.Bounds.Y>.76&&Geometry.Normalize(t.Label).StartsWith("合计价格"))) && Has("兑换列表") && Has("取消")
            && vision.Regions.Any(r=>r.Label=="primary_button" && r.Confidence>=.8)
            && vision.Regions.Any(r=>r.Label=="detail_panel" && r.Confidence>=.25);
        if(p.Page=="Unknown"&&words.Any(t=>t.Label=="每日任务")&&words.Any(t=>t.Label=="赛季手册")&&vision.Texts.Any(t=>t.Confidence >= OcrPolicy.MinimumConfidence&&t.Label=="进入暗区"&&t.Bounds.Y>.8)&&regions.Any(r=>r.Token.Label=="layout_menu"))page="Home";
        if(detail) {
            page="Exchange";
            var heads=Find("兑换列表",full);var totals=words.Where(t=>Geometry.Normalize(t.Label).StartsWith("合计价格")).ToArray();
            if(heads.Length==1&&totals.Length==1&&totals[0].Bounds.Y>heads[0].Bounds.Y+.1) {
                double left=heads[0].Bounds.X-.006,top=heads[0].Bounds.Y+heads[0].Bounds.Height+.004;
                double right=vision.Regions.Where(r=>r.Label is "primary_button" or "material_row").Select(r=>r.Bounds.X+r.Bounds.Width).DefaultIfEmpty(.95).Max();
                Add("randall_material_viewport",new(left,top,right-left,totals[0].Bounds.Y-top-.02),"material-viewport","兑换列表","合计价格");
            }
            // Footer two-button layout: a detected primary button and independent cancel label.
            // The permitted box stays on the left of the midpoint between both labels.
            var cancel=Find("取消",full);var topup=words.Where(t=>t.Label is "补齐道具" or "兑换").ToArray();
            if(cancel.Length==1 && topup.Length==1) {
                var a=cancel[0].Bounds;var b=topup[0].Bounds;
                var primary=vision.Regions.Where(r=>r.Label=="primary_button"&&r.Confidence>=.8&&Geometry.Contains(r.Bounds,b)).ToArray();
                if(primary.Length==1 && Math.Abs(Geometry.CenterY(a)-Geometry.CenterY(b))<.015 && a.X<b.X) {
                    double mid=(Geometry.CenterX(a)+Geometry.CenterX(b))/2;
                    var box=new Region(Math.Max(0,2*Geometry.CenterX(a)-mid+.012),primary[0].Bounds.Y,mid-.012-Math.Max(0,2*Geometry.CenterX(a)-mid+.012),primary[0].Bounds.Height);
                    if(Geometry.Contains(box,a))Add("randall_cancel",box,"exchange-footer-left-cancel", "道具兑换","取消",topup[0].Label);
                }
            }
            var titles=words.Where(t=>t.Bounds.X<.46&&t.Bounds.Y<.16&&RandallTargets.Items.Any(i=>Geometry.Normalize(i.Name)==Geometry.Normalize(t.Label))).ToArray();
            foreach(var title in titles) Add("randall_detail",new(Math.Max(0,title.Bounds.X-.006),Math.Max(0,title.Bounds.Y-.005),Math.Min(.44,title.Bounds.Width+.015),Math.Min(.07,title.Bounds.Height+.015)),"detail-title",title.Label,"道具兑换");
        } else if(p.Page=="ContactList") {
            // Locate each row from its observed name, never from its ordinal position.
            regions.RemoveAll(r=>r.Token.Label=="contact_entry");
            foreach(var name in NavigationSceneAdapter.Contacts) foreach(var t in Find(name,new(.2,.21,.35,.51))) {
                var box=new Region(.2,Math.Max(.21,Geometry.CenterY(t.Bounds)-.032),.34,.064);
                if(Geometry.Contains(box,t.Bounds))Add("contact_entry",box,"observed-contact-row",name,"购买","出售");
            }
            if(Find(Contact,new(.65,.23,.14,.06)).Length==1) {
                page="ContactProfile";
                Add("contact_shop_entry",new(.561,.68,.12,.045),"profile-buy-is-shop-entry",Contact,"购买","出售");
            }
        } else if(Find(Contact,new(.04,.03,.25,.08)).Length==1
            && Categories.Count(c=>Find(c,new(.04,.115,.38,.04)).Length==1)>=2
            && (vision.Regions.Count(r=>r.Label=="product_card"&&r.Confidence>=.8&&r.Bounds.X<.5)>=2)) {
            page="ContactShop";
            foreach(var category in Categories) foreach(var t in Find(category,new(.04,.115,.38,.04))) {
                var b=t.Bounds;Add("randall_category",new(b.X-.012,.12,b.Width+.024,.035),"shop-category-bar",Contact,category);
            }
            // Scroll target is the whole goods viewport; its identifying anchor is category text.
            Add("randall_goods_viewport",new(.045,.16,.445,.75),"goods-viewport",Contact);
        }
        var layout=p.Layout+"|"+page+"|"+string.Join("|",regions.Select(r=>$"{r.Token.Label}:{r.Token.Bounds}"));
        return p with { Page=page, LayoutRegions=regions.ToArray(), Confidence=page=="Unknown"?0:.95,
            Layout=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(layout))), Sources=(p.Sources??[]).Append("Randall calibrated layout + spatial OCR duplicate suppression; raw output retained").ToArray() };
    }
}
