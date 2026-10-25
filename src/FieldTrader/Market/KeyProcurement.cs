using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using FieldTrader.Navigation;
namespace FieldTrader.Market;

public sealed record KeyChoice(string Id,bool Selected,int Quantity);
public sealed record KeyProcurementSettings(bool Enabled,KeyChoice[] Choices,int ObservationWindowSeconds=300,int MinimumObservations=2) {
    public static KeyProcurementSettings Default()=>new(false,KeyMarketTargets.Items.Select(i=>new KeyChoice(i.Id,false,1)).ToArray());
    public KeyChoice Choice(string id)=>Choices.SingleOrDefault(c=>c.Id==id)??new(id,false,1);
    public void Validate() {
        if(Choices is null||Choices.Any(c=>!KeyMarketTargets.Items.Any(i=>i.Id==c.Id)||c.Quantity<=0)||Choices.Select(c=>c.Id).Distinct().Count()!=Choices.Length
            ||ObservationWindowSeconds<30||ObservationWindowSeconds>86400||MinimumObservations<2||MinimumObservations>100)throw new InvalidDataException("钥匙选择、数量或观察设置无效");
    }
}
public static class KeySettingsStore {
    public static KeyProcurementSettings Load(string file) {
        if(!File.Exists(file))return KeyProcurementSettings.Default();
        var value=JsonSerializer.Deserialize<KeyProcurementSettings>(File.ReadAllText(file))??throw new InvalidDataException("钥匙设置为空");value.Validate();return value;
    }
    public static void Save(string file,KeyProcurementSettings value) {
        value.Validate();Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
        var temporary=file+".new";File.WriteAllText(temporary,JsonSerializer.Serialize(value,NavigationEvidenceLog.Json));File.Move(temporary,file,true);
    }
}
public sealed record KeyPurchaseQuote(string ItemId,string Name,string Category,int? RemainingUses,int? MaximumUses,int? PurchaseQuantity,
    decimal? UnitPrice,decimal? ExactTotal,string? DisplayedTotal,string TotalPrecision,decimal? AbbreviatedDisplayValue,string FrameId,string Hash,DateTimeOffset At,bool Live);
public static class KeyPurchaseReader {
    public static KeyPurchaseQuote? Read(PageObservation p) {
        VisualTargetResolver.Validate(p);if(p.Page!="Purchase"||p.Errors.Length>0)return null;
        var items=KeyMarketTargets.Items.Where(i=>new VisualTargetResolver().Resolve(p,new(i.Name,"materials_detail",MaterialMarketRoutes.All,"购买数量",MaterialMarketRoutes.All),true).Length==1).ToArray();
        var trades=p.Vision.Regions.Where(t=>t.Label=="trade_panel"&&t.Confidence>=.8).ToArray();var details=p.Vision.Regions.Where(t=>t.Label=="detail_panel"&&t.Confidence>=.35).ToArray();
        if(items.Length!=1||trades.Length!=1||details.Length!=1)return null;
        var words=p.Vision.Texts.Where(t=>t.Confidence >= OcrPolicy.MinimumConfidence&&Geometry.Contains(trades[0].Bounds,t.Bounds)).ToArray();
        var unit=TradeScreenReader.RowNumber(words,"平均单价");var total=DisplayTotal(words);
        var b=details[0].Bounds;
        var counts=p.Vision.Texts.Where(t=>t.Confidence >= OcrPolicy.MinimumConfidence&&Geometry.Contains(b,t.Bounds)&&t.Bounds.X>b.X+b.Width*.75&&t.Bounds.Y<b.Y+b.Height*.32)
            .Select(t=>Regex.Match(Geometry.Normalize(t.Label),@"^[x×]?(\d{1,4})/(\d{1,4})$")).Where(m=>m.Success).Select(m=>(Used:int.Parse(m.Groups[1].Value),Max:int.Parse(m.Groups[2].Value))).Distinct().ToArray();
        var item=items[0];
        return new(item.Id,item.Name,item.Category,counts.Length==1?counts[0].Used:null,counts.Length==1?counts[0].Max:null,
            MaterialMarketQuantity.Read(p.Vision),unit,total.Exact,total.Text,total.Precision,total.Approximate,p.Screen.Frame.Id,p.Screen.ContentHash,p.Screen.Frame.CapturedAt,p.Screen.Live);
    }
    public static (decimal? Exact,string? Text,string Precision,decimal? Approximate) DisplayTotal(VisualToken[] words) {
        var labels=words.Where(t=>Geometry.Normalize(t.Label).TrimEnd(':','：')=="合计").ToArray();
        if(labels.Length!=1)return(null,null,"Missing",null);
        var anchor=labels[0];var values=words.Where(t=>t.Bounds.X>anchor.Bounds.X+anchor.Bounds.Width&&Math.Abs(Geometry.CenterY(t.Bounds)-Geometry.CenterY(anchor.Bounds))<Math.Max(t.Bounds.Height,anchor.Bounds.Height)*.7
            &&Regex.IsMatch(Geometry.Normalize(t.Label),@"^\d[\d,]*k?$",RegexOptions.IgnoreCase)).OrderByDescending(t=>t.Bounds.X).ToArray();
        if(values.Length==0)return(null,null,"Missing",null);
        var token=values[0];var text=Geometry.Normalize(token.Label);
        if(text.EndsWith('k')) {
            if(MarketPerception.TryInteger(text[..^1],out var thousands))return(null,token.Label,"AbbreviatedK",thousands*1000);
            return(null,token.Label,"Unparsed",null);
        }
        return MarketPerception.TryInteger(text,out var exact)?(exact,token.Label,"Exact",null):(null,token.Label,"Unparsed",null);
    }
}
public sealed class KeyDemandLedger {
    readonly HashSet<string> receipts=[];
    readonly Dictionary<string,int> filled=[];
    public int Remaining(KeyProcurementSettings settings,string id)=>!settings.Enabled||!settings.Choice(id).Selected?0:Math.Max(0,settings.Choice(id).Quantity-filled.GetValueOrDefault(id));
    public bool VerifiedFill(string id,int quantity,string receiptId) {
        if(!KeyMarketTargets.Items.Any(i=>i.Id==id)||quantity<=0||string.IsNullOrWhiteSpace(receiptId))throw new ArgumentException("Verified product, quantity and receipt identity required");
        if(!receipts.Add(receiptId))return false;filled[id]=checked(filled.GetValueOrDefault(id)+quantity);return true;
    }
}
public sealed class KeyLowPricePolicy {
    readonly List<KeyPurchaseQuote> samples=[];
    public void Observe(KeyPurchaseQuote quote) {
        if(quote.Live&&quote.UnitPrice>0&&quote.PurchaseQuantity>0&&!samples.Any(s=>s.FrameId==quote.FrameId))samples.Add(quote);
    }
    public string Evaluate(KeyProcurementSettings settings,string id,KeyPurchaseQuote? quote,KeyDemandLedger ledger,DateTimeOffset now) {
        settings.Validate();if(!settings.Enabled)return "Disabled";if(!settings.Choice(id).Selected)return "NotSelected";
        var remaining=ledger.Remaining(settings,id);if(remaining==0)return "DemandFulfilled";
        if(quote is null||quote.ItemId!=id||!KeyMarketTargets.Items.Any(i=>i.Id==id&&i.Name==quote.Name&&i.Category==quote.Category))return "NeedsMatchingQuote";
        if(!quote.Live)return "HistoricalOnly";
        if(now<quote.At||now-quote.At>TimeSpan.FromSeconds(30))return "NeedsFreshQuote";
        if(quote.PurchaseQuantity!=remaining)return "NeedsRequestedQuantity";
        if(quote.RemainingUses is not >0||quote.MaximumUses is not >0||quote.RemainingUses>quote.MaximumUses)return "NeedsUseCount";
        if(quote.ExactTotal is null)return "NeedsExactTotal";
        if(quote.UnitPrice<=0||quote.ExactTotal!=quote.UnitPrice*quote.PurchaseQuantity)return "NeedsConsistentAmounts";
        var window=samples.Where(s=>s.ItemId==id&&s.RemainingUses==quote.RemainingUses&&s.MaximumUses==quote.MaximumUses&&s.PurchaseQuantity==remaining&&s.At<=now&&now-s.At<=TimeSpan.FromSeconds(settings.ObservationWindowSeconds)).ToArray();
        if(window.Length<settings.MinimumObservations)return "ObservingPrices";
        return quote.UnitPrice<=window.Min(s=>s.UnitPrice)?"ObservedLowQuoteCandidate;SubmissionNotConnected":"WaitForObservedLow";
    }
}
