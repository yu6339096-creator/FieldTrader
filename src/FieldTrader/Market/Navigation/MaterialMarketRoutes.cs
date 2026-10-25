using System.Security.Cryptography;
using System.Text;
using FieldTrader.Market;
namespace FieldTrader.Navigation;

public sealed record MaterialMarketTarget(string Id,string Name,string Category,string ExchangeProduct);
public static class MaterialMarketTargets {
    public static readonly MaterialMarketTarget[] Items = [
        new("chip","芯片处理器","电脑配件","DK8军用防弹面罩"),
        new("radio","无线对讲机","军用杂物","100D战地医疗包"),
        new("rope","伞绳","军用杂物","SH12军用头盔")
    ];
}
public static class MaterialMarketQuantity {
    public static int? Read(VisionResult v) {
        var panels=v.Regions.Where(t=>t.Label=="trade_panel"&&t.Confidence>=.8).ToArray();
        if(panels.Length!=1||!v.Texts.Any(t=>t.Confidence >= OcrPolicy.MinimumConfidence&&Geometry.Normalize(t.Label)=="购买数量"&&Geometry.Contains(panels[0].Bounds,t.Bounds)))return null;
        var fields=v.Regions.Where(t=>t.Label=="quantity_field"&&t.Confidence>=.5&&Geometry.Contains(panels[0].Bounds,t.Bounds)).ToArray();
        var counts=fields.Length==1?v.Texts.Where(t=>t.Confidence >= OcrPolicy.MinimumConfidence&&Geometry.Contains(fields[0].Bounds,t.Bounds)).Select(t=>MarketPerception.TryInteger(t.Label,out var n)?(decimal?)n:null).Where(n=>n is not null).Distinct().ToArray():[];
        return counts.Length==1&&counts[0]<=int.MaxValue?(int?)counts[0]:null;
    }
}
public sealed class MaterialMarketClassifier(string root="杂物",string[]? permittedCategories=null,string[]? permittedNames=null,string marketPage="MaterialMarket",string pixelPrefix="MaterialCategoryOrange:") : IPageClassifier {
    private readonly string[] leaves=permittedCategories??Categories;
    private readonly string[] names=permittedNames??MaterialMarketTargets.Items.Select(i=>i.Name).ToArray();
    private string? SelectedCategory(VisionResult v)=>PickCategory(v,leaves,pixelPrefix);
    public static readonly string[] Categories=["电脑配件","军用杂物"];
    public static string? ActiveCategory(VisionResult v) {
        return PickCategory(v,Categories,"MaterialCategoryOrange:");
    }
    public static string? PickCategory(VisionResult v,string[] categories,string prefix) {
        var selected=categories.Where(c=>v.PixelMeasurements?.TryGetValue(prefix+c,out var n)==true&&n>=.08).ToArray();
        return selected.Length==1?selected[0]:null;
    }
    public PageObservation Classify(CapturedScreen screen,VisionResult raw) {
        var v=raw with{Texts=LarryPageClassifier.Unique(raw.Texts)};
        var p=new MarketPageClassifier().Classify(screen,v);
        if(p.Errors.Length>0)return p with{Page="Unknown"};
        if(v.Regions.Any(t=>t.Label=="confirm_dialog"&&t.Confidence>=.6))return p with{Page="Unknown",Errors=["Unexpected confirmation; no background market navigation"]};
        var regions=new List<LayoutRegion>(p.LayoutRegions??[]);var page=p.Page;
        var words=v.Texts.Where(t=>t.Confidence >= OcrPolicy.MinimumConfidence).ToArray();
        VisualToken[] Find(string name,Region area)=>words.Where(t=>Geometry.Contains(area,t.Bounds)&&Geometry.Normalize(t.Label)==Geometry.Normalize(name)).ToArray();
        void Add(string kind,Region b,string rule,params string[] anchors){if(b.Valid)regions.Add(new(new(kind,.95,b),"LayoutRule:materials:"+rule+"; NOT YOLO",anchors));}
        if(page=="Unknown"&&words.Any(t=>t.Label=="每日任务")&&words.Any(t=>t.Label=="赛季手册")&&v.Texts.Any(t=>t.Confidence >= OcrPolicy.MinimumConfidence&&t.Label=="进入暗区"&&t.Bounds.Y>.8)&&regions.Any(r=>r.Token.Label=="layout_menu"))page="Home";
        var trade=v.Regions.Where(t=>t.Label=="trade_panel"&&t.Confidence>=.8).ToArray();
        var detail=v.Regions.Where(t=>t.Label=="detail_panel"&&t.Confidence>=.35).ToArray();
        bool purchase=trade.Length==1&&detail.Length==1&&Find("购买数量",trade[0].Bounds).Length==1&&Find("平均单价",trade[0].Bounds).Length==1;
        if(purchase) {
            page="Purchase";
            var fields=v.Regions.Where(t=>t.Label=="quantity_field"&&t.Confidence>=.5&&Geometry.Contains(trade[0].Bounds,t.Bounds)).ToArray();
            if(fields.Length==1&&MaterialMarketQuantity.Read(v) is not null) {
                var field=fields[0].Bounds;double side=field.Height*screen.Height/screen.Width;
                var plus=new Region(field.X+field.Width,field.Y,side,field.Height);
                var minus=new Region(field.X-side,field.Y,side,field.Height);
                if(Geometry.Contains(trade[0].Bounds,plus)&&Geometry.Contains(trade[0].Bounds,minus)) {
                    Add("materials_quantity_plus",plus,"quantity-right-square","购买数量");
                    Add("materials_quantity_minus",minus,"quantity-left-square","购买数量");
                }
            }
            foreach(var itemName in names)foreach(var title in Find(itemName,detail[0].Bounds).Where(t=>t.Bounds.Y<detail[0].Bounds.Y+detail[0].Bounds.Height*.18))
                Add("materials_detail",new(title.Bounds.X-.004,title.Bounds.Y-.004,title.Bounds.Width+.008,title.Bounds.Height+.008),"purchase-identity",itemName,"购买数量");
            var close=Find("关闭",trade[0].Bounds);var buy=Find("购买",trade[0].Bounds).Where(t=>t.Bounds.Y>trade[0].Bounds.Y+trade[0].Bounds.Height*.7).ToArray();
            if(close.Length==1&&buy.Length==1&&close[0].Bounds.X<buy[0].Bounds.X&&Math.Abs(Geometry.CenterY(close[0].Bounds)-Geometry.CenterY(buy[0].Bounds))<.015) {
                var button=v.Regions.Where(t=>t.Label=="primary_button"&&t.Confidence>=.8&&Geometry.Contains(t.Bounds,buy[0].Bounds)).ToArray();
                if(button.Length==1) {
                    double mid=(Geometry.CenterX(close[0].Bounds)+Geometry.CenterX(buy[0].Bounds))/2;
                    double left=2*Geometry.CenterX(close[0].Bounds)-mid+.01;
                    var box=new Region(left,button[0].Bounds.Y,mid-.01-left,button[0].Bounds.Height);
                    if(Geometry.Contains(box,close[0].Bounds))Add("materials_close",box,"purchase-left-close","关闭","购买数量");
                }
            }
        } else if(!v.Regions.Any(t=>t.Confidence>=.6&&t.Label is "trade_panel" or "confirm_dialog")
            &&new[]{"购买","出售","我的交易"}.All(n=>Find(n,MaterialMarketRoutes.Top).Length==1)
            &&v.PixelMeasurements?.TryGetValue("MarketBuyUnderline",out var selected)==true&&selected>=.15) {
            page=marketPage;
            foreach(var name in leaves.Append(root))foreach(var t in Find(name,MaterialMarketRoutes.Left)) {
                var b=t.Bounds;var box=new Region(.046,Math.Max(.16,b.Y-.006),.141,Math.Min(.038,b.Height+.012));
                if(Geometry.Contains(box,b))Add(name==root?"materials_root":"materials_category",box,"category-row",name,"我的交易");
            }
            if(SelectedCategory(v) is {} active&&Find(active,new(.195,.10,.25,.055)).Length==1&&v.Regions.Count(t=>t.Label=="product_card"&&t.Confidence>=.8)>=2)
                Add("materials_goods_viewport",MaterialMarketRoutes.Goods,"goods-viewport",active,"我的交易");
        }
        var layout=p.Layout+"|"+page+"|"+string.Join("|",regions.Select(r=>$"{r.Token.Label}:{r.Token.Bounds}"));
        return p with{Page=page,LayoutRegions=regions.ToArray(),Layout=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(layout))),Sources=(p.Sources??[]).Append("Material-market calibrated layout; raw model/OCR retained").ToArray()};
    }
}
public static class MaterialMarketRoutes {
    public static readonly Region Top=new(.025,0,.22,.08),Left=new(.045,.16,.143,.72),Goods=new(.195,.16,.76,.725),All=new(0,0,1,1);
    static NavigationStep Step(string id,string page,TargetQuery q,string after,TargetQuery? result=null)=>new("materials:"+id,page,q,InputOperation.Click,after,TimeSpan.FromSeconds(40),Effect:LayoutEffect.MayChange,ExpectedResultTarget:result);
    public static TargetQuery Category(string name)=>new(name,"materials_category",Left,"我的交易",Top);
    public static bool FullyVisible(PageObservation p,MaterialMarketTarget item)=>p.Vision.Regions.Count(r=>r.Label=="product_card"&&r.Confidence>=.95&&Geometry.Contains(Goods,r.Bounds)&&r.Bounds.Y+r.Bounds.Height<.87
        &&p.Vision.Texts.Any(t=>t.Confidence >= OcrPolicy.MinimumConfidence&&Geometry.Normalize(t.Label)==Geometry.Normalize(item.Name)&&Geometry.Contains(r.Bounds,t.Bounds)))==1;
    public static NavigationStep? Next(PageObservation p,MaterialMarketTarget item) {
        if(!MaterialMarketTargets.Items.Contains(item))throw new ArgumentException("Only three exchange materials are configured");
        if(p.Page=="Home")return Step("market","Home",VisualNavigationRoutes.Menu("市场"),"MaterialMarket");
        if(p.Page!="MaterialMarket"||p.Errors.Length>0)return null;
        if(!p.Vision.Texts.Any(t=>t.Confidence >= OcrPolicy.MinimumConfidence&&Geometry.Contains(Left,t.Bounds)&&t.Label==item.Category))return Step("expand-misc","MaterialMarket",new("杂物","materials_root",Left,"我的交易",Top),"MaterialMarket",Category(item.Category));
        if(MaterialMarketClassifier.ActiveCategory(p.Vision)!=item.Category)return Step("category:"+item.Category,"MaterialMarket",Category(item.Category),"MaterialMarket") with{RequiredActiveCategory=item.Category};
        if(!FullyVisible(p,item))return Scroll(item.Category);
        return Step("detail:"+item.Id,"MaterialMarket",new(item.Name,"product_card",Goods,item.Category,new(.195,.10,.25,.055)),"Purchase",new(item.Name,"materials_detail",All,"购买数量",All));
    }
    public static NavigationStep Scroll(string category)=>new("materials:scroll","MaterialMarket",new(category,"materials_goods_viewport",Goods,category,new(.195,.10,.25,.055)),InputOperation.Scroll,"MaterialMarket",TimeSpan.FromSeconds(40),ScrollDelta:-360);
    public static NavigationStep Close()=>Step("close","Purchase",new("关闭","materials_close",All,"购买数量",All),"MaterialMarket");
    public static NavigationStep? Quantity(PageObservation p,MaterialMarketTarget item,int desired=5) {
        if(desired<=0)throw new ArgumentException("Positive requested quantity required");
        if(p.Page!="Purchase"||p.Errors.Length>0||new VisualTargetResolver().Resolve(p,new(item.Name,"materials_detail",All,"购买数量",All),true).Length!=1)return null;
        if(MaterialMarketQuantity.Read(p.Vision) is not {} current||current==desired)return null;
        bool plus=current<desired;
        return Step("quantity:"+item.Id,"Purchase",new(plus?"增加数量":"减少数量",plus?"materials_quantity_plus":"materials_quantity_minus",All,"购买数量",All),"Purchase") with{RequiredPurchaseQuantity=current+(plus?1:-1)};
    }
}
