using System.Text.RegularExpressions;
using System.IO;
using System.Text.Json;
namespace FieldTrader.Market;
public sealed record MaterialNeed(string? Name,int? Owned,int? Required,string RegionKey);
public sealed record ExchangeTerms(string? ItemName,MaterialNeed[] Materials,decimal? MaterialValuation,decimal? TopUpSpend,decimal? ProductMarketPrice,string State);
public static class ExchangeScreenReader {
    public static string[] Events(VisualToken[] texts) {
        var labels=texts.Select(t=>Geometry.Normalize(t.Label)).ToArray();var events=new List<string>();
        void Add(string kind,params string[] words){if(labels.Any(t=>words.Any(t.Contains)))events.Add(kind);}
        Add("partial_failure","部分购买失败");Add("failed","交易失败","购买失败","兑换失败");Add("sold_out","售罄");
        Add("price_changed","价格发生变化");Add("quantity_limit","数量超","超过购买","超过该物品购买数量","大于该物品最大购买数量");Add("cheaper_materials","低价购入","低价购买","价格更低");
        Add("exchange_success_candidate","已兑换");Add("listing_success_candidate","上架成功");
        return events.ToArray();
    }
    public static ExchangeTerms? Read(PerceptionResult p) {
        if(p.Page is not (MarketPage.Exchange or MarketPage.ExchangeConfirm))return null;
        var v=p.Vision;var text=v.Texts.Where(t=>t.Confidence>=.9&&t.Bounds.Valid).DistinctBy(t=>(Geometry.Normalize(t.Label),Math.Round(t.Bounds.X,2),Math.Round(t.Bounds.Y,2))).ToArray();
        var kind=p.Page==MarketPage.ExchangeConfirm?"confirm_dialog":"trade_panel";
        var panels=v.Regions.Where(t=>t.Label==kind&&t.Confidence>=.6).ToArray();if(panels.Length!=1)return null;
        var inside=text.Where(t=>Geometry.Contains(panels[0].Bounds,t.Bounds)).ToArray();
        var details=v.Regions.Where(t=>t.Label=="detail_panel"&&t.Confidence>=.6).ToArray();
        var titles=details.Length==1?text.Where(t=>Geometry.Contains(details[0].Bounds,t.Bounds)&&t.Bounds.Y<details[0].Bounds.Y+details[0].Bounds.Height*.12&&t.Label.Any(char.IsLetter)).OrderBy(t=>t.Bounds.Y).ToArray():[];
        var needs=new List<MaterialNeed>();
        foreach(var row in v.Regions.Where(t=>t.Label=="material_card"&&t.Confidence>=.6&&Geometry.Contains(panels[0].Bounds,t.Bounds))) {
            var tokens=inside.Where(t=>Geometry.Contains(row.Bounds,t.Bounds)).ToArray();
            var names=tokens.Where(t=>t.Bounds.Y<row.Bounds.Y+row.Bounds.Height*.5&&Regex.IsMatch(t.Label,@"[\u4e00-\u9fff]")).DistinctBy(t=>Geometry.Normalize(t.Label)).ToArray();
            var counts=tokens.DistinctBy(t=>Geometry.Normalize(t.Label)).Select(t=>Regex.Match(Geometry.Normalize(t.Label),@"^(\d{1,5})/(\d{1,5})$")).Where(m=>m.Success).ToArray();
            needs.Add(new(names.Length==1?names[0].Label:null,counts.Length==1?int.Parse(counts[0].Groups[1].Value):null,counts.Length==1?int.Parse(counts[0].Groups[2].Value):null,$"{row.Bounds.X:F3}:{row.Bounds.Y:F3}"));
        }
        return new(titles.FirstOrDefault()?.Label,needs.ToArray(),TradeScreenReader.RowNumber(inside,"合计价格"),TradeScreenReader.RowNumber(inside,"补齐价格"),TradeScreenReader.RowNumber(inside,"产品市场价"),p.Page==MarketPage.ExchangeConfirm?"等待确认；未完成兑换":"材料估值与补齐报价；未支付");
    }
}
public sealed record ReferenceSpread(string ItemName,int? Quantity,decimal? Cost,decimal? Gross,decimal? Net,decimal? ManagementFee,decimal? Spread,string[] Sources,string[] Missing);
public sealed class ReferenceAnalysis {
    private sealed record Profile(string Name,string[] Aliases,Dictionary<string,int>? Recipe,int? Output);
    private static readonly Profile[] profiles=LoadProfiles();
    private static Profile[] LoadProfiles() {
        var path=Path.Combine(AppContext.BaseDirectory,"MarketSeed","reference-profiles.json");
        if(!File.Exists(path))return [];
        using var json=JsonDocument.Parse(File.ReadAllText(path));
        return json.RootElement.GetProperty("items").Deserialize<Profile[]>(new JsonSerializerOptions{PropertyNameCaseInsensitive=true})??[];
    }
    private static string Key(string name) {
        var normalized=Geometry.Normalize(name);
        var matches=profiles.Where(p=>Geometry.Normalize(p.Name)==normalized||p.Aliases.Any(a=>Geometry.Normalize(a)==normalized)).ToArray();
        return matches.Length==1?Geometry.Normalize(matches[0].Name):normalized;
    }
    private sealed record CostBasis(decimal Unit,DateTimeOffset At,string Source);
    private readonly Dictionary<string,CostBasis> costs=[];
    public void Clear()=>costs.Clear();
    public ReferenceSpread? Observe(PerceptionResult p,IReadOnlyList<CatalogItem> catalog,bool replay,DateTimeOffset now,decimal reserve=0,IReadOnlyList<ExchangeRecipe>? recipes=null) {
        if(p.Paused)return null;
        var facts=TradeScreenReader.Read(p,catalog);
        string provenance=$"{(replay?"历史回放":"实时OCR")} · {p.Vision.FrameId} · {p.Vision.CapturedAt:O}";
        foreach(var q in p.Quotes.Where(q=>q.Context=="purchase-unit")) {
            var panels=p.Vision.Regions.Where(t=>t.Label=="trade_panel"&&t.Confidence>=.6).ToArray();
            var total=panels.Length==1?TradeScreenReader.RowNumber(p.Vision.Texts.Where(t=>t.Confidence>=.9&&Geometry.Contains(panels[0].Bounds,t.Bounds)).ToArray(),"合计"):null;
            costs[Key(q.ItemName)]=new(total is >0&&q.Quantity is >0?total.Value/q.Quantity.Value:q.Price,p.Vision.CapturedAt,(total is >0&&q.Quantity is >0?"购买合计/独立数量":"购买单价报价")+"；非实际成交 · "+provenance);
        }
        if(facts.Receipt is {Quantity:>0,Total:>0} r)costs[Key(r.ItemName)]=new(r.Total.Value/r.Quantity.Value,p.Vision.CapturedAt,"购买结果总价/独立数量；仍需关联操作、冲突待对账 · "+provenance);
        var configured=recipes??[];
        CatalogItem? Item(string name){var found=catalog.Where(i=>i.Confirmed&&Key(i.Name)==Key(name)).ToArray();return found.Length==1?found[0]:null;}
        CostBasis? Basis(string name,HashSet<string> visited) {
            if(!visited.Add(Key(name)))return null;
            if(costs.TryGetValue(Key(name),out var direct))return direct;
            var item=Item(name);var matches=configured.Where(r=>r.ProductId==item?.Id&&r.OutputQuantity>0&&r.ExtraFee>=0&&r.Materials.Length>0&&r.Materials.All(m=>m.Quantity>0)).ToArray();
            if(matches.Length!=1) {
                var profile=profiles.SingleOrDefault(p=>Key(p.Name)==Key(name));
                if(profile?.Recipe is null||profile.Output is null or <=0)return null;
                decimal total=0;var oldest=p.Vision.CapturedAt;var profileEvidence=new List<string>();
                foreach(var material in profile.Recipe) {
                    var materialBasis=Basis(material.Key,new(visited));if(materialBasis is null)return null;
                    total+=materialBasis.Unit*material.Value;if(materialBasis.At<oldest)oldest=materialBasis.At;profileEvidence.Add(materialBasis.Source);
                }
                return new(total/profile.Output.Value,oldest,"既有原型明确配方（仅参考，未授予决策资格） · "+string.Join("；",profileEvidence));
            }
            var recipe=matches[0];decimal sum=recipe.ExtraFee;var at=p.Vision.CapturedAt;var evidence=new List<string>();
            foreach(var material in recipe.Materials) {
                var materialItem=catalog.SingleOrDefault(i=>i.Id==material.ItemId);if(materialItem is null)return null;
                var basis=Basis(materialItem.Name,new(visited));if(basis is null)return null;
                sum+=basis.Unit*material.Quantity;if(basis.At<at)at=basis.At;evidence.Add(basis.Source);
            }
            return new(sum/recipe.OutputQuantity,at,$"配置配方 {recipe.Id}（{recipe.Status}，仅参考） · "+string.Join("；",evidence));
        }
        if(facts.Exchange is {} exchange) {
            var item=exchange.ItemName is {} name?Item(name):null;
            var matches=configured.Where(r=>r.ProductId==item?.Id&&r.OutputQuantity>0).ToArray();
            var count=matches.Length==1?(int?)matches[0].OutputQuantity:profiles.SingleOrDefault(x=>exchange.ItemName is {} name&&Key(x.Name)==Key(name))?.Output;
            if(exchange.ItemName is {} product&&exchange.MaterialValuation is {} valuation&&count is >0)
                costs[Key(product)]=new(valuation/count.Value,p.Vision.CapturedAt,"兑换材料合计估值（含已有材料，非实际现金扣款）；产出量来自配置配方 · "+provenance);
            decimal? exchangeGross=count*exchange.ProductMarketPrice;
            decimal? exchangeNet=exchangeGross is {} g?g-decimal.Ceiling(g*.08m):null;
            return new(exchange.ItemName??"未唯一关联商品",count,exchange.MaterialValuation,exchangeGross,exchangeNet,null,exchangeNet-exchange.MaterialValuation-reserve,
                [provenance,"材料合计为全部材料估值；补齐报价不是全部成本",$"新增补齐支出报价 {exchange.TopUpSpend?.ToString()??"未读到"}；尚未支付","产品市场价为参考；未验证8%临时估税；管理费未知未计入；不授予执行资格"],
                count is null?["每次产出数量及商品身份未唯一关联，不能默认1或计算整套收益"]:[]);
        }
        if(facts.Sale is not {} sale)return null;
        var sources=new List<string>{provenance,"出售面板显示价（可被人工修改）"};var missing=new List<string>();
        decimal? cost=null;int? qty=sale.Quantity;
        var basis=Basis(sale.ItemName,[]);
        if(qty is >0&&basis is not null) {
            cost=decimal.Ceiling(basis.Unit*qty.Value);sources.Add(basis.Source);
            if(now<basis.At||now-basis.At>TimeSpan.FromMinutes(15))sources.Add("成本超过15分钟或时间异常，仅供历史参考");
        } else missing.Add("对应数量采购成本");
        if(qty is null or <=0)missing.Add("出售数量（不得默认1）");
        decimal? gross=sale.Gross??(qty is >0?sale.DisplayedUnitPrice*qty:null);decimal? net=null;
        if(gross is >=0) {
            if(sale.NetAfterSaleFee is >=0&&sale.NetAfterSaleFee<=gross){net=sale.NetAfterSaleFee;sources.Add("预计到账OCR，已含成交税，不再扣8%");if(sale.SaleFee is {} fee&&gross-fee!=net)sources.Add("手续费与到账冲突，请重读");}
            else if(sale.SaleFee is >=0&&sale.SaleFee<=gross){net=gross-sale.SaleFee;sources.Add("总额减OCR实际手续费");}
            else {net=gross-decimal.Ceiling(gross.Value*.08m);sources.Add("未验证8%临时估税（向上取整）");}
        }else missing.Add("出售总额");
        if(gross is >0&&net is {} knownNet&&sale.NetAfterSaleFee is not null||gross is >0&&net is not null&&sale.SaleFee is not null)
            sources.Add($"本页有效手续费率 {(gross!.Value-net!.Value)/gross.Value:P4}（由页面总额与到账计算，非固定费率）");
        sources.Add(sale.ManagementFee is null?"管理费未知，尚未计入":"管理费OCR，独立扣除一次");
        sources.Add("仅参考差价；不改变预算、库存、配方或执行资格");
        return new(sale.ItemName,qty,cost,gross,net,sale.ManagementFee,net-cost-(sale.ManagementFee??0)-reserve,sources.ToArray(),missing.ToArray());
    }
}
