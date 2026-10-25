using FieldTrader.Navigation;
namespace FieldTrader.Market;

public sealed record DickBrowseContext(string Session,string ItemId,string Category,int? OutputQuantity,string FrameId,
    string Hash,DateTimeOffset At,long WindowHandle,bool Live);
public sealed record DickQuote(string Session,string ItemName,string Category,int? OutputQuantity,decimal? Total,
    decimal? TopUp,decimal? Market,decimal? ReferenceGross,string? PrimaryAction,string Eligibility,string FrameId,string CardFrameId);
public static class DickQuoteReader {
    public static DickBrowseContext Begin(string session,PageObservation p,DickCard card) {
        VisualTargetResolver.Validate(p);
        var category=DickPageClassifier.ActiveCategory(p.Vision);
        if(string.IsNullOrWhiteSpace(session)||p.Page!="ContactShop"||p.Errors.Length>0||category is null||card.SoldOut||card.Cooldown is not null
            ||!DickScreenReader.Cards(p).Contains(card)||(category=="限时物品"&&!card.ItemModelMatched))
            throw new NavigationEvidenceException("Fresh unique eligible card required");
        return new(session,card.Item.Id,category,card.OutputQuantity,p.Screen.Frame.Id,p.Screen.ContentHash,p.Screen.Frame.CapturedAt,p.Screen.WindowHandle,p.Screen.Live);
    }
    public static DickQuote Read(DickBrowseContext context,PageObservation p,decimal? minimumRegularGross=null) {
        VisualTargetResolver.Validate(p);
        var item=DickTargets.Items.Single(i=>i.Id==context.ItemId);
        if(p.Page!="Exchange"||p.Errors.Length>0||p.Screen.Frame.Id==context.FrameId||p.Screen.Frame.CapturedAt<=context.At
            ||p.Screen.Frame.CapturedAt-context.At>TimeSpan.FromMinutes(2)||p.Screen.WindowHandle!=context.WindowHandle||p.Screen.Live!=context.Live
            ||new VisualTargetResolver().Resolve(p,new(item.Name,"dick_detail",DickRoutes.All,"道具兑换",DickRoutes.All),true).Length!=1)
            throw new NavigationEvidenceException("Detail must match originating card in a newer frame of the same workflow");
        var words=p.Vision.Texts.Where(t=>t.Bounds.X>.48&&t.Bounds.Y>.76).ToArray();
        var total=ExchangePanelMoney.Read(words,"合计价格");var cash=ExchangePanelMoney.Read(words,"补齐价格");var market=ExchangePanelMoney.Read(words,"产品市场价");
        var action=p.Vision.Texts.Where(t=>t.Confidence >= OcrPolicy.MinimumConfidence&&t.Bounds.X>.65&&t.Bounds.Y>.85&&t.Label is "补齐道具" or "兑换").Select(t=>t.Label).Distinct().ToArray();
        bool blocked=p.Vision.Texts.Any(t=>t.Confidence >= OcrPolicy.MinimumConfidence&&t.Bounds.X<.6&&t.Label.Contains("已售罄"));
        var eligibility=blocked?"SoldOut":action.Length!=1||total is null||market is null?"IncompleteQuote":context.Category=="限时物品"?"LimitedTrainedCandidate":item.Mandatory?"MandatoryPreference":minimumRegularGross is null?"NeedsProfitThreshold":market-total>=minimumRegularGross?"MeetsGrossThreshold":"BelowGrossThreshold";
        return new(context.Session,item.Name,context.Category,context.OutputQuantity,total,cash,market,market-total,action.Length==1?action[0]:null,eligibility,p.Screen.Frame.Id,context.FrameId);
    }
}
