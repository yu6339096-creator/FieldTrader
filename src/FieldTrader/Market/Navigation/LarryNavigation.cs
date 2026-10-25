using System.Security.Cryptography;
using System.Text;
using FieldTrader.Market;
namespace FieldTrader.Navigation;

// Calibrated layout evidence remains explicitly separate from the unchanged 14 YOLO classes.
public sealed class LarryPageClassifier : IPageClassifier {
    public const string Contact = "拉里·马里亚姆";
    public static readonly string[] Categories = ["医疗包", "伤害救治", "手雷"];
    public static string? ActiveCategory(VisionResult vision) {
        var matches=Categories.Where(c=>vision.PixelMeasurements?.TryGetValue("LarryTabOrange:"+c,out var fraction)==true&&fraction>=.18).ToArray();
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
        if(vision.Regions.Any(r=>r.Label=="confirm_dialog"&&r.Confidence>=.6))return p with{Page="Unknown",Errors=["Unexpected confirmation dialog; stop Larry navigation"]};
        var words = vision.Texts.Where(t => t.Confidence >= OcrPolicy.MinimumConfidence).ToArray();
        VisualToken[] Find(string name, Region area) => words.Where(t => Geometry.Contains(area,t.Bounds) && Geometry.Normalize(t.Label)==Geometry.Normalize(name)).ToArray();
        bool Has(string name) => words.Any(t=>Geometry.Normalize(t.Label)==Geometry.Normalize(name));
        var regions = new List<LayoutRegion>(p.LayoutRegions ?? []);
        string page = p.Page; var full = new Region(0,0,1,1);
        void Add(string kind,Region box,string rule,params string[] anchors) { if(box.Valid)regions.Add(new(new(kind,.95,box),"LayoutRule:larry:"+rule+"; NOT YOLO",anchors)); }
        bool detail = Has("道具兑换") && Has("需要提交以下道具") && Has("兑换列表") && Has("取消")
            && vision.Regions.Any(r=>r.Label=="primary_button" && r.Confidence>=.8)
            && vision.Regions.Any(r=>r.Label=="detail_panel" && r.Confidence>=.25);
        if(detail) {
            page="Exchange";
            var heads=Find("兑换列表",full);var totals=words.Where(t=>Geometry.Normalize(t.Label).StartsWith("合计价格")).ToArray();
            if(heads.Length==1&&totals.Length==1&&totals[0].Bounds.Y>heads[0].Bounds.Y+.1) {
                double left=heads[0].Bounds.X-.006,top=heads[0].Bounds.Y+heads[0].Bounds.Height+.004;
                double right=vision.Regions.Where(r=>r.Label is "primary_button" or "material_row").Select(r=>r.Bounds.X+r.Bounds.Width).DefaultIfEmpty(.95).Max();
                Add("larry_material_viewport",new(left,top,right-left,totals[0].Bounds.Y-top-.02),"material-viewport","兑换列表","合计价格");
            }
            // Footer two-button layout: a detected primary button and independent cancel label.
            // The permitted box stays on the left of the midpoint between both labels.
            var cancel=Find("取消",full);var topup=Find("补齐道具",full);
            if(cancel.Length==1 && topup.Length==1) {
                var a=cancel[0].Bounds;var b=topup[0].Bounds;
                var primary=vision.Regions.Where(r=>r.Label=="primary_button"&&r.Confidence>=.8&&Geometry.Contains(r.Bounds,b)).ToArray();
                if(primary.Length==1 && Math.Abs(Geometry.CenterY(a)-Geometry.CenterY(b))<.015 && a.X<b.X) {
                    double mid=(Geometry.CenterX(a)+Geometry.CenterX(b))/2;
                    var box=new Region(Math.Max(0,2*Geometry.CenterX(a)-mid+.012),primary[0].Bounds.Y,mid-.012-Math.Max(0,2*Geometry.CenterX(a)-mid+.012),primary[0].Bounds.Height);
                    if(Geometry.Contains(box,a))Add("larry_cancel",box,"exchange-footer-left-cancel", "道具兑换","取消","补齐道具");
                }
            }
            var titles=words.Where(t=>t.Bounds.X<.46&&t.Bounds.Y<.16&&LarryTargets.Items.Any(i=>Geometry.Normalize(i.Name)==Geometry.Normalize(t.Label))).ToArray();
            foreach(var title in titles) Add("larry_detail",new(Math.Max(0,title.Bounds.X-.006),Math.Max(0,title.Bounds.Y-.005),Math.Min(.44,title.Bounds.Width+.015),Math.Min(.07,title.Bounds.Height+.015)),"detail-title",title.Label,"道具兑换");
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
            && Categories.Count(c=>Find(c,new(.04,.115,.32,.04)).Length==1)>=2
            && vision.Regions.Count(r=>r.Label=="product_card"&&r.Confidence>=.8&&r.Bounds.X<.5)>=2) {
            page="ContactShop";
            foreach(var category in Categories) foreach(var t in Find(category,new(.04,.115,.32,.04))) {
                var b=t.Bounds;Add("larry_category",new(b.X-.012,.12,b.Width+.024,.035),"shop-category-bar",Contact,category);
            }
            // Scroll target is the whole goods viewport; its identifying anchor is category text.
            Add("larry_goods_viewport",new(.045,.16,.445,.75),"goods-viewport",Contact);
        }
        var layout=p.Layout+"|"+page+"|"+string.Join("|",regions.Select(r=>$"{r.Token.Label}:{r.Token.Bounds}"));
        return p with { Page=page, LayoutRegions=regions.ToArray(), Confidence=page=="Unknown"?0:.95,
            Layout=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(layout))), Sources=(p.Sources??[]).Append("Larry calibrated layout + spatial OCR duplicate suppression; raw output retained").ToArray() };
    }
}
public sealed record LarryTarget(string Id,string Name,string Category,string Limit,Dictionary<string,int>? ExpectedRecipe);
public static class LarryTargets {
    public static string? ItemClass(string name)=>Items.Select((i,n)=>(i,n)).FirstOrDefault(p=>Geometry.Normalize(p.i.Name)==Geometry.Normalize(name)) is var match&&match.i is not null
        ?new[]{"med_100d","med_sto","med_tmk","surgery_sto","painkiller_special","grenade_mkii","grenade_gho2"}[match.n]:null;
    public static readonly LarryTarget[] Items = [
        new("larry-100d","100D战地医疗包","医疗包","Repeatable",new(){["无线对讲机"]=1}),
        new("larry-sto","STO急救套装","医疗包","Daily",new(){["呢绒布"]=2,["炮队镜"]=1}),
        new("larry-tmk","TMK野战医疗包","医疗包","Daily",new(){["一叠便签"]=5,["宠物蛋"]=1}),
        new("larry-surgery","STO战地手术套装","伤害救治","Daily",new(){["香水"]=1,["固态硬盘"]=1}),
        new("larry-pain","特效止痛药","伤害救治","Daily",new(){["测光表"]=1,["发条小汽车"]=1}),
        new("larry-mkii","MK II破片手榴弹","手雷","RefreshWindow",null),
        new("larry-gho2","GHO 2进攻手榴弹","手雷","RefreshWindow",null)
    ];
    // These aliases are explicit display variants in supplied screenshots, not fuzzy name matching.
    public static string MaterialName(string value)=>Geometry.Normalize(value) switch {
        "便签" or "一叠便签"=>"一叠便签", "宠物蛋" or "电子宠物蛋"=>"宠物蛋", _=>value
    };
}
public static class LarryRoutes {
    public static readonly Region Goods=new(.04,.155,.46,.76), All=new(0,0,1,1);
    public static TargetQuery Query(string name,string kind,Region? area=null)=>new(name,kind,area??All,LarryPageClassifier.Contact,new(.04,.03,.25,.08),ItemClass:kind=="product_card"?LarryTargets.ItemClass(name):null);
    public static NavigationStep Step(string id,string page,TargetQuery target,string after,TargetQuery? result=null)=>new(id,page,target,InputOperation.Click,after,TimeSpan.FromSeconds(40),Effect:LayoutEffect.MayChange,ExpectedResultTarget:result);
    public static NavigationStep? Next(PageObservation page,LarryTarget target,string? currentCategory) => page.Page switch {
        "Home"=>Step("larry:contacts","Home",VisualNavigationRoutes.Menu("联络人"),"ContactList"),
        "ContactList"=>Step("larry:profile","ContactList",new(LarryPageClassifier.Contact,"contact_entry",new(.19,.21,.36,.52),"购买",new(.56,.67,.13,.06)),"ContactProfile"),
        "ContactProfile"=>Step("larry:shop","ContactProfile",new("购买","contact_shop_entry",new(.56,.67,.13,.06),LarryPageClassifier.Contact,new(.65,.23,.14,.06)),"ContactShop"),
        "ContactShop" when currentCategory!=target.Category=>Step("larry:category:"+target.Category,"ContactShop",Query(target.Category,"larry_category",new(.04,.115,.32,.045)),"ContactShop") with { RequiredActiveCategory=target.Category },
        "ContactShop"=>Step("larry:detail:"+target.Id,"ContactShop",Query(target.Name,"product_card",Goods),"Exchange",new(target.Name,"larry_detail",All,"道具兑换",All)),
        _=>null
    };
    public static NavigationStep Cancel(PageObservation page)=>Step("larry:cancel","Exchange",new("取消","larry_cancel",All,"道具兑换",All),"ContactShop");
    public static NavigationStep Scroll(PageObservation page)=>new("larry:scroll:"+Guid.NewGuid().ToString("N"),page.Page,
        page.Page=="ContactShop"?Query(LarryPageClassifier.Contact,"larry_goods_viewport"):new("兑换列表","larry_material_viewport",All,"道具兑换",All),
        InputOperation.Scroll,page.Page,TimeSpan.FromSeconds(40),ScrollDelta:-360);
    public static string ViewSignature(PageObservation page,Region area)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("|",page.Vision.Texts.Where(t=>t.Confidence >= OcrPolicy.MinimumConfidence&&Geometry.Contains(area,t.Bounds)).OrderBy(t=>t.Bounds.Y).ThenBy(t=>t.Bounds.X).Select(t=>$"{Geometry.Normalize(t.Label)}:{t.Bounds.X:F2}:{t.Bounds.Y:F2}")))));
}
