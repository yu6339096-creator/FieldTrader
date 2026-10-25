using System.Text.RegularExpressions;
using FieldTrader.Market;
namespace FieldTrader.Navigation;

public sealed record DickTarget(string Id,string Name,string Category,string ItemClass,bool Mandatory=false,int? CooldownSeconds=null);
public static class DickTargets {
    public static readonly DickTarget[] Items = [
        new("o3","03重型战术头盔","头盔","helmet_o3",true,43200),
        new("m4","防卫者M4重装弹挂甲","弹挂","armor_defender_m4",true),
        new("926","926高级野战胸挂","弹挂","rig_926_advanced"),
        new("a8","H-Tac A8弹挂甲","弹挂","armor_htac_a8"),
        new("a9","H-Tac A9弹挂甲","弹挂","armor_htac_a9"),
        new("sh12","SH12军用头盔","头盔","helmet_sh12"),
        new("mkii","MK II破片手榴弹","手雷","grenade_mkii"),
        new("gho2","GHO 2进攻手榴弹","手雷","grenade_gho2"),
        new("m995","5.56×45毫米 M995子弹","弹药","ammo_556_m995"),
        new("dvc12","5.8×42毫米 DVC12子弹","弹药","ammo_58_dvc12"),
        new("762ap","7.62×39毫米 AP子弹","弹药","ammo_76239_ap"),
        new("338ap",".338 AP","弹药","ammo_338_ap")
    ];
    public static string? ItemClass(string name)=>Items.SingleOrDefault(i=>Geometry.Normalize(i.Name)==Geometry.Normalize(name))?.ItemClass;
}
public enum DickRefreshState { Unknown, NotAcquired, Cooling, Exhausted, Ready }
public sealed record DickLimitedState(DickRefreshState State,int? Remaining,int? WaitSeconds,bool HasGoods,string SourceFrame);
public sealed record DickCard(DickTarget Item,int? OutputQuantity,bool SoldOut,string? Cooldown,Region Bounds,bool ItemModelMatched,double? QuantityConfidence=null);
public static class DickScreenReader {
    public static DickLimitedState Limited(PageObservation p) {
        var unknown=new DickLimitedState(DickRefreshState.Unknown,null,null,false,p.Screen.Frame.Id);
        if(p.Page!="ContactShop"||p.Errors.Length>0||DickPageClassifier.ActiveCategory(p.Vision)!="限时物品")return unknown;
        var words=p.Vision.Texts.Where(t=>t.Confidence >= OcrPolicy.MinimumConfidence).ToArray();
        bool Has(string s,Region area)=>words.Any(t=>Geometry.Contains(area,t.Bounds)&&Geometry.Normalize(t.Label)==Geometry.Normalize(s));
        var footer=new Region(.38,.12,.10,.035);
        var labels=words.Where(t=>Geometry.Contains(footer,t.Bounds)).Select(t=>Geometry.Normalize(t.Label)).Distinct().ToArray();
        bool empty=Has("限时商品未刷新",new(.15,.47,.27,.07))&&Has("获取限时商品",new(.20,.52,.18,.06));
        bool goods=p.Vision.Regions.Count(t=>t.Label=="product_card"&&t.Confidence>=.8&&Geometry.Contains(DickRoutes.Goods,t.Bounds))>=2;
        bool exhausted=labels.Any(t=>t=="已用尽");
        var clocks=labels.Select(t=>Regex.Match(t,@"^(\d{1,2}):(\d{2})后可刷新$")).Where(m=>m.Success&&int.Parse(m.Groups[2].Value)<60).Select(m=>int.Parse(m.Groups[1].Value)*60+int.Parse(m.Groups[2].Value)).Distinct().ToArray();
        var refreshCounts=labels.Select(t=>Regex.Match(t,@"^刷新商品([0-3])/3$")).Where(m=>m.Success).Select(m=>int.Parse(m.Groups[1].Value)).Distinct().ToArray();
        if(refreshCounts.Length>1)return unknown;
        if(refreshCounts.Length==1&&refreshCounts[0]==0)exhausted=true;
        bool ready=labels.Any(t=>t is "刷新" or "刷新限时商品" or "获取限时商品")||(refreshCounts.Length==1&&refreshCounts[0]>0);
        int indicators=(empty?1:0)+(exhausted?1:0)+(clocks.Length>0?1:0)+(ready?1:0);
        if(indicators!=1||clocks.Length>1)return unknown;
        // No remaining-count glyph in supplied screenshots: do not invent a quota balance.
        return new(empty?DickRefreshState.NotAcquired:exhausted?DickRefreshState.Exhausted:clocks.Length==1?DickRefreshState.Cooling:DickRefreshState.Ready,
            exhausted?0:refreshCounts.Length==1?refreshCounts[0]:null,clocks.Length==1?clocks[0]:null,goods,p.Screen.Frame.Id);
    }
    public static DickCard[] Cards(PageObservation p) {
        if(p.Page!="ContactShop"||p.Errors.Length>0||DickPageClassifier.ActiveCategory(p.Vision) is not {} category)return [];
        var result=new List<DickCard>();
        foreach(var card in p.Vision.Regions.Where(t=>t.Label=="product_card"&&t.Confidence>=.8&&Geometry.Contains(DickRoutes.Goods,t.Bounds))) {
            var words=LarryPageClassifier.Unique(p.Vision.Texts.Where(t=>t.Confidence >= OcrPolicy.MinimumConfidence&&Geometry.Contains(card.Bounds,t.Bounds)));
            var matches=DickTargets.Items.Where(i=>(category=="限时物品"||i.Category==category)&&words.Any(t=>Geometry.Normalize(t.Label)==Geometry.Normalize(i.Name))).ToArray();
            if(matches.Length!=1)continue;
            var item=matches[0];
            var quantityTokens=p.Vision.Texts.Where(t=>t.Confidence >= OcrPolicy.MinimumConfidence&&Geometry.Contains(card.Bounds,t.Bounds)&&t.Bounds.X>=card.Bounds.X+card.Bounds.Width*.6&&t.Bounds.Y>=card.Bounds.Y+card.Bounds.Height*.65&&t.Bounds.Y<card.Bounds.Y+card.Bounds.Height*.87&&Regex.IsMatch(Geometry.Normalize(t.Label),@"^x([1-9]\d{0,4})$")).ToArray();
            var nums=quantityTokens.Select(t=>Regex.Match(Geometry.Normalize(t.Label),@"^x([1-9]\d{0,4})$")).Where(m=>m.Success).Select(m=>int.Parse(m.Groups[1].Value)).Distinct().ToArray();
            bool model=p.Vision.ItemModel is not null&&(p.Vision.Items??[]).Count(t=>t.Label==item.ItemClass&&t.Confidence>=.8&&Geometry.Contains(card.Bounds,t.Bounds))==1;
            var art=(p.Vision.Items??[]).Where(t=>t.Label==item.ItemClass&&t.Confidence>=.8&&Geometry.Contains(card.Bounds,t.Bounds)).ToArray();
            if(art.Any(a=>(p.Vision.Items??[]).Any(b=>b.Label!=a.Label&&b.Confidence>=.8&&Geometry.IoU(a.Bounds,b.Bounds)>.5)))model=false;
            var clocks=words.Select(t=>Regex.Match(Geometry.Normalize(t.Label),@"\d{2}:\d{2}:\d{2}")).Where(m=>m.Success).Select(m=>m.Value).Distinct().ToArray();
            result.Add(new(item,nums.Length==1?nums[0]:null,words.Any(t=>t.Label=="已售罄"),clocks.Length==1?clocks[0]:null,card.Bounds,model,nums.Length==1?quantityTokens.Max(t=>t.Confidence):null));
        }
        return result.GroupBy(c=>c.Item.Id).Where(g=>g.Count()==1).Select(g=>g.Single()).ToArray();
    }
}
public static class DickRoutes {
    public static readonly Region Goods=new(.04,.155,.46,.76), All=new(0,0,1,1);
    public static TargetQuery Query(string name,string kind,Region? area=null)=>new(name,kind,area??All,DickPageClassifier.Contact,new(.04,.03,.25,.08),MinimumConfidence:.94,ItemClass:kind=="product_card"?DickTargets.ItemClass(name):null);
    static NavigationStep Step(string id,string page,TargetQuery target,string after,TargetQuery? result=null)=>new("dick:"+id,page,target,InputOperation.Click,after,TimeSpan.FromSeconds(40),Effect:LayoutEffect.MayChange,ExpectedResultTarget:result);
    public static NavigationStep? Next(PageObservation p,DickTarget item,string category) {
        if(!DickPageClassifier.Categories.Contains(category))throw new ArgumentException("Unrecognized Dick category");
        if(category!="限时物品"&&item.Category!=category)throw new ArgumentException("Wrong item category");
        return p.Page switch {
            "Home"=>Step("contacts","Home",VisualNavigationRoutes.Menu("联络人"),"ContactList"),
            "ContactList"=>Step("profile","ContactList",new(DickPageClassifier.Contact,"contact_entry",new(.19,.21,.36,.52),"购买",new(.56,.67,.13,.06)),"ContactProfile"),
            "ContactProfile"=>Step("shop","ContactProfile",new("购买","contact_shop_entry",new(.56,.67,.13,.06),DickPageClassifier.Contact,new(.65,.23,.14,.06),MinimumConfidence:.94),"ContactShop"),
            "ContactShop" when DickPageClassifier.ActiveCategory(p.Vision)!=category=>Step("category:"+category,"ContactShop",Query(category,"dick_category",new(.04,.115,.32,.045)),"ContactShop") with{RequiredActiveCategory=category},
            "ContactShop" when DickScreenReader.Cards(p).Any(c=>c.Item.Id==item.Id&&!c.SoldOut&&c.Cooldown is null&&(category!="限时物品"||c.ItemModelMatched))=>Step("detail:"+item.Id,"ContactShop",Query(item.Name,"product_card",Goods),"Exchange",new(item.Name,"dick_detail",All,"道具兑换",All)),
            _=>null
        };
    }
    public static NavigationStep Cancel()=>Step("cancel","Exchange",new("取消","dick_cancel",All,"道具兑换",All),"ContactShop");
    public static NavigationStep Scroll()=>new("dick:scroll","ContactShop",Query(DickPageClassifier.Contact,"dick_goods_viewport"),InputOperation.Scroll,"ContactShop",TimeSpan.FromSeconds(40),ScrollDelta:-360);
}
