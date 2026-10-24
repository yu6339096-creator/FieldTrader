using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FieldTrader.Market;
public sealed record TradingSettings(FeePolicy Fees,TradingPolicy Policy,InventoryLot[] Inventory,ResaleRule[] SpecialRules);
public sealed class TradingAnalysis(MarketDatabase database,TradingSettings? settings) {
    private bool? replayMode;
    public ReferenceSpread? Reference {get;private set;}
    private readonly ReferenceAnalysis reference=new();
    private readonly Dictionary<string,CurrentPrice> buyPrices=[],sellPrices=[];
    private readonly Dictionary<string,string> emitted=[];
    private readonly Dictionary<string,TradeDecision> latest=[];
    public TradeDecision[] LastDecisions=>latest.Values.ToArray();
    public static readonly JsonSerializerOptions JsonOptions=new(){WriteIndented=true,PropertyNameCaseInsensitive=true,Converters={new JsonStringEnumConverter()}};
    public static TradingSettings? Load(string path)=>File.Exists(path)?JsonSerializer.Deserialize<TradingSettings>(File.ReadAllText(path),JsonOptions):null;
    public TradeDecision[] Observe(PerceptionResult perception,bool replay,DateTimeOffset now) {
        if(perception.Paused){Reference=null;latest.Clear();return [];}
        if(replayMode!=replay){buyPrices.Clear();sellPrices.Clear();latest.Clear();reference.Clear();replayMode=replay;}
        var affected=new HashSet<string>();var catalog=database.Catalog();
        Reference=reference.Observe(perception,catalog,replay,now,settings?.Policy.RiskReserve??0,database.Recipes());
        foreach(var q in perception.Quotes) {
            var item=catalog.SingleOrDefault(i=>i.Confirmed&&Geometry.Normalize(i.Name)==Geometry.Normalize(q.ItemName));if(item is null)continue;
            var price=new CurrentPrice(item.Id,q.Price,q.Quantity,perception.Vision.CapturedAt,perception.Vision.FrameId,Replay:replay);affected.Add(item.Id);
            if(q.Context=="purchase-unit")buyPrices[item.Id]=price;
            if(q.Context=="sell-displayed")sellPrices[item.Id]=price;
        }
        var recipes=affected.SelectMany(database.Recipes).DistinctBy(r=>r.Id).ToArray();
        var results=new List<TradeDecision>();
        foreach(var recipe in recipes) {
            var procurement=new Dictionary<string,CurrentPrice>(buyPrices);
            // The product sale quote has a separate key even when it is also a material.
            var saleKey="sale:"+recipe.ProductId;
            if(sellPrices.TryGetValue(recipe.ProductId,out var product))procurement[saleKey]=product with{ItemId=saleKey};
            var decision=settings is null?new TradeDecision(DecisionKind.SKIP,recipe.Id,recipe.ProductId,recipe.OutputQuantity,[],0,0,0,0,0,0,"未配置经确认的费率与预算",now)
                :ExchangeProfitEngine.Evaluate(recipe with{ProductId=saleKey},procurement,settings.Inventory,database.ConservativeSaleHistory(recipe.ProductId),settings.Fees,settings.Policy,now,simulation:replay);
            results.Add(decision with{ProductId=recipe.ProductId});
        }
        foreach(var rule in settings?.SpecialRules.Where(r=>affected.Contains(r.ItemId))??[]) {
            var decision=buyPrices.TryGetValue(rule.ItemId,out var buy)&&sellPrices.TryGetValue(rule.ItemId,out var sell)
                ?ExchangeProfitEngine.EvaluateSpecial(rule,buy,sell,database.ConservativeSaleHistory(rule.ItemId),settings!.Fees,settings.Policy,now,replay)
                :new TradeDecision(DecisionKind.SKIP,"special:"+rule.ItemId,rule.ItemId,1,[],0,0,0,0,0,0,"特殊商品缺少独立的当前购买价或出售价",now);
            results.Add(decision);
        }
        foreach(var decision in results){latest[decision.RecipeId]=decision;string fingerprint=JsonSerializer.Serialize(decision with{At=default});
            if(emitted.GetValueOrDefault(decision.RecipeId)!=fingerprint){database.SaveDecision(decision);emitted[decision.RecipeId]=fingerprint;}}
        return results.ToArray();
    }
}
public static class SimulationRunner {
    // All post-action proofs here are explicitly synthesized for forecasting. No captured screenshot is a simulated receipt.
    public static async Task<SimulationState> Run(MarketDatabase db,string session,ExchangeRecipe recipe,TradeDecision decision,
        TradingSettings settings,ListingQuota quota,decimal displayedUnitPrice,CancellationToken token=default,ResaleRule? directRule=null) {
        await MarketOperationGate.TradeSession.WaitAsync(token);
        try{return await RunCore(db,session,recipe,decision,settings,quota,displayedUnitPrice,token,directRule);}
        finally{MarketOperationGate.TradeSession.Release();}
    }
    private static async Task<SimulationState> RunCore(MarketDatabase db,string session,ExchangeRecipe recipe,TradeDecision decision,
        TradingSettings settings,ListingQuota quota,decimal displayedUnitPrice,CancellationToken token,ResaleRule? directRule) {
        var controller=new TradingSimulation(db,session);var now=DateTimeOffset.UtcNow;
        if(!quota.Valid||quota.WeekKey!=ListingQuotaManager.Week(now)||now<quota.At||now-quota.At>TimeSpan.FromMinutes(5))throw new InvalidOperationException("没有新鲜额度，不能预演上架");
        if(directRule is null)controller.Begin(recipe,decision,settings.Policy.Budget,settings.Inventory,quota);
        else controller.BeginSpecial(directRule,decision,settings.Policy.Budget,quota);
        async Task Submit(DecisionKind kind,string item,int qty,decimal money,decimal fee,string? listing=null) {
            token.ThrowIfCancellationRequested();string id=Guid.NewGuid().ToString("N");now=DateTimeOffset.UtcNow;
            var page=kind==DecisionKind.BUY?MarketPage.Purchase:kind==DecisionKind.EXCHANGE?MarketPage.Exchange:MarketPage.Sell;
            var before=new PageEvidence(new(id+":before",now,"synthetic-simulation"),page,item,[new(kind==DecisionKind.BUY?"购买":kind==DecisionKind.EXCHANGE?"兑换":"上架",1,new(.5,.5,.1,.05))]);
            await controller.ExecuteAsync(new(id,kind,item,qty,money,fee,before,kind==DecisionKind.SELL?settings.Fees.SaleFee(money):null),_=>Task.FromResult(new TradeProof(id+":after",id+":synthetic-proof",now.AddMilliseconds(1),kind==DecisionKind.SELL?MarketPage.Listings:MarketPage.Receipt,item,qty,money,fee,ProofOutcome.Confirmed,listing)),now,token);
        }
        foreach(var p in decision.Purchases)await Submit(DecisionKind.BUY,p.ItemId,p.Quantity,p.UnitPrice*p.Quantity,0);
        if(directRule is null)await Submit(DecisionKind.EXCHANGE,recipe.ProductId,recipe.OutputQuantity,recipe.ExtraFee,0);
        var snapshot=controller.Snapshot;var lot=snapshot.Inventory.Single(l=>snapshot.SaleLotIds.Contains(l.Id));
        controller.VerifySaleEligibility(lot.Id,"synthetic-sale-eligibility",lot.Quantity);
        foreach(int qty in ListingQuotaManager.Batches(recipe.OutputQuantity)) {
            string listing=Guid.NewGuid().ToString("N");decimal gross=displayedUnitPrice*qty;
            await Submit(DecisionKind.SELL,recipe.ProductId,qty,gross,settings.Fees.ListingFee(gross,1),listing);
            controller.ResolveListing(listing,true,gross,settings.Fees.SaleFee(gross),listing+":synthetic-fill",DateTimeOffset.UtcNow);
        }
        return controller.Snapshot;
    }
}
