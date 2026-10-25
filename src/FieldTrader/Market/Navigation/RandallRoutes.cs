using System.Text.RegularExpressions;
using FieldTrader.Market;
namespace FieldTrader.Navigation;

public sealed record RandallTarget(string Id,string Name,string Category,bool Repeatable,int? CooldownSeconds);
public static class RandallTargets {
    public static readonly RandallTarget[] Items = [
        new("dk8","DK8军用防弹面罩","头套面罩",true,null),
        new("gs2","GS2战术耳机","耳机",false,18000)
    ];
}
public sealed record RandallAvailability(bool Visible,bool SoldOut,string? Countdown,int? Remaining,int? Maximum);
public static class RandallRoutes {
    public static readonly Region Goods=new(.04,.155,.46,.76), All=new(0,0,1,1);
    public static TargetQuery Query(string name,string kind,Region? area=null)=>new(name,kind,area??All,RandallPageClassifier.Contact,new(.04,.03,.25,.08),MinimumConfidence:.94);
    static NavigationStep Step(string id,string page,TargetQuery target,string after,TargetQuery? result=null)=>new("randall:"+id,page,target,InputOperation.Click,after,TimeSpan.FromSeconds(40),Effect:LayoutEffect.MayChange,ExpectedResultTarget:result);
    public static RandallAvailability Availability(PageObservation p,RandallTarget item) {
        if(p.Page!="ContactShop"||p.Errors.Length>0||RandallPageClassifier.ActiveCategory(p.Vision)!=item.Category)return new(false,false,null,null,null);
        var cards=p.Vision.Regions.Where(r=>r.Label=="product_card"&&r.Confidence>=.8&&Geometry.Contains(Goods,r.Bounds)
            &&p.Vision.Texts.Any(t=>t.Confidence >= OcrPolicy.MinimumConfidence&&Geometry.Normalize(t.Label)==Geometry.Normalize(item.Name)&&Geometry.Contains(r.Bounds,t.Bounds))).ToArray();
        if(cards.Length!=1)return new(false,false,null,null,null);
        var words=p.Vision.Texts.Where(t=>t.Confidence >= OcrPolicy.MinimumConfidence&&Geometry.Contains(cards[0].Bounds,t.Bounds)).Select(t=>Geometry.Normalize(t.Label)).Distinct().ToArray();
        var clocks=words.Select(t=>Regex.Match(t,@"(?:^|[^\d])(\d{2}:\d{2}:\d{2})$")).Where(m=>m.Success).Select(m=>m.Groups[1].Value).Distinct().ToArray();
        var counts=words.Select(t=>Regex.Match(t,@"^限购[:：]?(\d+)/(\d+)$")).Where(m=>m.Success).Select(m=>(Left:int.Parse(m.Groups[1].Value),Max:int.Parse(m.Groups[2].Value))).Distinct().ToArray();
        return new(true,words.Any(t=>t.Contains("已售罄")),clocks.Length==1?clocks[0]:clocks.Length>1?"ConflictingCountdown":null,
            counts.Length==1?counts[0].Left:null,counts.Length==1?counts[0].Max:null);
    }
    // Repeatable means no per-item quota; it never means an unbounded execution loop.
    // Cooldown is a user-supplied rule, not a wall-clock job or a daily limit.
    public static bool Eligible(RandallTarget item,RandallAvailability observed,DateTimeOffset now,DateTimeOffset? lastVerifiedExchange=null)=>observed.Visible&&!observed.SoldOut&&observed.Countdown is null
        &&observed.Remaining!=0&&(lastVerifiedExchange is null||item.CooldownSeconds is null||now>=lastVerifiedExchange.Value.AddSeconds(item.CooldownSeconds.Value));
    public static NavigationStep? Next(PageObservation p,RandallTarget item,DateTimeOffset? lastVerifiedExchange=null) {
        if(!RandallTargets.Items.Contains(item))throw new ArgumentException("Only the two requested Randall items are configured");
        return p.Page switch {
            "Home"=>Step("contacts","Home",VisualNavigationRoutes.Menu("联络人"),"ContactList"),
            "ContactList"=>Step("profile","ContactList",new(RandallPageClassifier.Contact,"contact_entry",new(.19,.21,.36,.52),"购买",new(.56,.67,.13,.06)),"ContactProfile"),
            "ContactProfile"=>Step("shop","ContactProfile",new("购买","contact_shop_entry",new(.56,.67,.13,.06),RandallPageClassifier.Contact,new(.65,.23,.14,.06),MinimumConfidence:.94),"ContactShop"),
            "ContactShop" when RandallPageClassifier.ActiveCategory(p.Vision)!=item.Category=>Step("category:"+item.Category,"ContactShop",Query(item.Category,"randall_category",new(.04,.115,.38,.045)),"ContactShop") with{RequiredActiveCategory=item.Category},
            "ContactShop" when Eligible(item,Availability(p,item),p.Screen.Frame.CapturedAt,lastVerifiedExchange)=>Step("detail:"+item.Id,"ContactShop",Query(item.Name,"product_card",Goods),"Exchange",new(item.Name,"randall_detail",All,"道具兑换",All)),
            _=>null
        };
    }
    public static NavigationStep Cancel()=>Step("cancel","Exchange",new("取消","randall_cancel",All,"道具兑换",All),"ContactShop");
    public static NavigationStep Scroll()=>new("randall:scroll","ContactShop",Query(RandallPageClassifier.Contact,"randall_goods_viewport"),InputOperation.Scroll,"ContactShop",TimeSpan.FromSeconds(40),ScrollDelta:-360);
}
