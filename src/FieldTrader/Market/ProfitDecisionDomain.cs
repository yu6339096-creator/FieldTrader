namespace FieldTrader.Market;

// Stage 3 outputs are advice. They deliberately have no execution command or trade decision kind.
public enum QuotePurpose { MaterialPurchaseUnit, ProductSaleUnit, MarketCardReference, ExchangeProductReference }
public sealed record ProfitPrice(string Id,string ItemId,QuotePurpose Purpose,decimal UnitPrice,DateTimeOffset At,
 PriceSource Source,string EvidenceId,bool Replay);
public sealed record ProfitInventory(DateTimeOffset At,string EvidenceId,bool Complete,bool Replay,InventoryLot[] Lots);
public sealed record ProfitAvailability(string RecipeId,bool Repeatable,int? Remaining,DateTimeOffset? NextEligibleAt,
 DateTimeOffset? ExpiresAt,bool Limited,int? RefreshRemaining,DateTimeOffset? NextRefreshAt,DateTimeOffset At,string EvidenceId,bool Replay);
public sealed record ProfitExchangeQuote(string RecipeId,decimal MaterialTotal,decimal? SupplementCash,int OutputQuantity,
 DateTimeOffset At,string EvidenceId,bool Replay,decimal? ProductReferenceTotal=null);
public sealed record ProfitSaleCostQuote(string ItemId,int Quantity,decimal Gross,decimal SaleFee,decimal ManagementFee,DateTimeOffset At,string EvidenceId,bool Replay);
public sealed record ProfitFeeTerms(FeePolicy Policy,DateTimeOffset At,bool Replay);
public sealed record ProfitAdviceContext(TradingPolicy Policy,ProfitFeeTerms? Fees,ProfitInventory? Inventory,ProfitAvailability[] Availability) {
 public static ProfitAdviceContext Empty()=>new(new(0),null,null,[]);
}
public sealed record ProfitBreakdown(decimal? RecipeMaterialValue,decimal? OwnedMaterialValue,decimal? MissingMaterialValue,
 decimal? SupplementCash,decimal? HistoricalInventoryCost,decimal ExtraExchangeFee,decimal? ProductReferenceUnit,
 decimal? ConservativeSaleUnit,decimal? GrossRevenue,decimal? SaleFee,decimal? ManagementFee,decimal RiskReserve,
 decimal? EconomicCost,decimal? CashRequired,decimal? ExpectedNetProfit,decimal? CashSurplusAfterSale,decimal? ProfitRate,
 decimal? ScreenMaterialValue,decimal? ProductReferenceTotal,string SupplementSource,decimal? HistoricalCostProfit);
public sealed record ProfitScore(decimal NetProfit,decimal Roi,decimal Urgency,decimal Inventory,decimal Freshness,decimal RemainingQuota,decimal Total);
public sealed record ProfitAdvice(string RecipeId,string ProductId,string ProductName,string Contact,string Status,ProfitBreakdown Amounts,
 ProfitScore Score,int? RemainingExchanges,int? RefreshRemaining,DateTimeOffset? NextEligibleAt,DateTimeOffset? ExpiresAt,
 string[] Reasons,string[] Evidence,DateTimeOffset At,bool Replay,bool RecommendationOnly=true,int Rank=0,bool? Repeatable=null,bool? Limited=null,DateTimeOffset? NextRefreshAt=null);
public static class UnifiedProfitEngine {
 public static ProfitAdvice Evaluate(ExchangeRecipe recipe,string productName,IReadOnlyList<ProfitPrice> prices,ProfitAdviceContext context,
 ProfitExchangeQuote? screen,DateTimeOffset now,bool replay=false,IReadOnlyList<ProfitSaleCostQuote>? saleCosts=null) {
  var reasons=new List<string>();var evidence=new List<string>{"配方:"+recipe.EvidenceId};var p=context.Policy;
  bool Fresh(DateTimeOffset at)=>now>=at&&now-at<=TimeSpan.FromSeconds(p.PriceMaxAgeSeconds);
  bool Eligible(ProfitPrice q)=>q.UnitPrice>0&&q.Source==PriceSource.OcrObservation&&q.Replay==replay&&!string.IsNullOrWhiteSpace(q.EvidenceId)&&Fresh(q.At);
  ProfitPrice? Price(string id,QuotePurpose purpose) {
   var candidates=prices.Where(q=>q.ItemId==id&&q.Purpose==purpose&&Eligible(q)).OrderByDescending(q=>q.At).ToArray();
   if(candidates.Length==0)return null;var at=candidates[0].At;var newest=candidates.Where(q=>q.At==at).ToArray();
   if(newest.Select(q=>q.UnitPrice).Distinct().Count()!=1){reasons.Add("同一时刻报价冲突:"+id);return null;}
   evidence.AddRange(newest.Select(q=>$"{purpose}:{q.Id}:{q.EvidenceId}:{q.At:O}"));return newest[0];
  }
  bool validPolicy=p.Budget>=0&&p.MinimumProfit>=0&&p.MinimumRoi>=0&&p.RiskReserve>=0&&p.PriceDiscount>=0&&p.PriceDiscount<1&&p.PriceMaxAgeSeconds>0&&p.MinimumSamples>0;
  bool validRecipe=recipe.Status==VerificationStatus.Verified&&!string.IsNullOrWhiteSpace(recipe.EvidenceId)&&recipe.OutputQuantity>0&&recipe.ExtraFee>=0&&recipe.Materials.Length>0&&recipe.Materials.All(m=>m.Quantity>0&&m.ItemId!=recipe.ProductId)&&recipe.Materials.Select(m=>m.ItemId).Distinct().Count()==recipe.Materials.Length;
  if(!validPolicy)reasons.Add("决策配置无效");if(!validRecipe)reasons.Add("配方身份、数量或凭证待核验");
  var availabilityRows=context.Availability.Where(a=>a.RecipeId==recipe.Id).ToArray();var availability=availabilityRows.Length==1?availabilityRows[0]:null;
  bool availabilityValid=availability is not null&&availability.Replay==replay&&!string.IsNullOrWhiteSpace(availability.EvidenceId)&&Fresh(availability.At)
    &&(availability.Repeatable?availability.Remaining is null:availability.Remaining is >=0)&&availability.RefreshRemaining is not <0
    &&(!availability.Limited||(availability.RefreshRemaining is >=0 and <=3))&&(availability.Limited||availability.RefreshRemaining is null&&availability.NextRefreshAt is null);
  if(!availabilityValid)reasons.Add("兑换次数、刷新或冷却状态待更新");else evidence.Add("可用状态:"+availability!.EvidenceId);
  var inv=context.Inventory;bool inventoryValid=inv is{Complete:true}&&inv.Replay==replay&&!string.IsNullOrWhiteSpace(inv.EvidenceId)&&Fresh(inv.At)
   &&inv.Lots.All(l=>l.Quantity>0&&l.TotalCost>=0&&!string.IsNullOrWhiteSpace(l.EvidenceId))&&inv.Lots.Select(l=>l.Id).Distinct().Count()==inv.Lots.Length;
  if(!inventoryValid)reasons.Add("完整库存快照待更新；不把未知库存当作零");else evidence.Add("库存:"+inv!.EvidenceId);
  decimal? materialTotal=0,ownedValue=inventoryValid?0:null,missingValue=inventoryValid?0:null,historical=inventoryValid?0:null;decimal ownedUnits=0,allUnits=0;
  var observedTimes=new List<DateTimeOffset>();
  foreach(var m in recipe.Materials) {
   var buy=validPolicy?Price(m.ItemId,QuotePurpose.MaterialPurchaseUnit):null;
   if(buy is null){materialTotal=null;ownedValue=null;missingValue=null;reasons.Add("材料采购价待更新:"+m.ItemId);}else{materialTotal+=buy.UnitPrice*m.Quantity;observedTimes.Add(buy.At);}
   if(!inventoryValid)continue;
   int need=m.Quantity;foreach(var lot in inv!.Lots.Where(l=>l.ItemId==m.ItemId).OrderBy(l=>l.Id,StringComparer.Ordinal)) {int take=Math.Min(need,lot.Quantity);historical+=lot.TotalCost*take/lot.Quantity;need-=take;if(need==0)break;}
   ownedUnits+=m.Quantity-need;allUnits+=m.Quantity;if(buy is not null){ownedValue+=buy.UnitPrice*(m.Quantity-need);missingValue+=buy.UnitPrice*need;}
  }
  bool screenValid=screen is not null&&screen.RecipeId==recipe.Id&&screen.OutputQuantity==recipe.OutputQuantity&&screen.MaterialTotal>=0&&screen.SupplementCash is null or >=0&&screen.Replay==replay&&!string.IsNullOrWhiteSpace(screen.EvidenceId)&&validPolicy&&Fresh(screen.At);
  decimal? supplement=missingValue;
  if(screenValid){evidence.Add("兑换页:"+screen!.EvidenceId);observedTimes.Add(screen.At);if(screen.SupplementCash is {} cash)supplement=cash;}
  if(materialTotal is {} total&&screenValid&&Math.Abs(total-screen!.MaterialTotal)>Math.Max(1,total*.05m))reasons.Add("材料逐项报价与兑换页合计相差超过5%，请重新核价");
  if(ownedValue is not null&&supplement is not null&&missingValue==0&&supplement!=0)reasons.Add("库存已齐但补齐报价非零，请核对同一配方与库存");
  var product=validPolicy?Price(recipe.ProductId,QuotePurpose.ProductSaleUnit):null;
  var reference=product??(validPolicy?Price(recipe.ProductId,QuotePurpose.ExchangeProductReference)??Price(recipe.ProductId,QuotePurpose.MarketCardReference):null);
  var distinctSaleQuotes=prices.Where(q=>q.ItemId==recipe.ProductId&&q.Purpose==QuotePurpose.ProductSaleUnit&&Eligible(q)).DistinctBy(q=>q.EvidenceId).ToArray();
  decimal? sale=product is null?null:decimal.Floor(Math.Min(product.UnitPrice,MarketDatabase.Summarize(distinctSaleQuotes.Select(q=>q.UnitPrice)).P25)*(1-p.PriceDiscount));
  var saleSamples=product is null?0:prices.Where(q=>q.ItemId==recipe.ProductId&&q.Purpose==QuotePurpose.ProductSaleUnit&&Eligible(q)).DistinctBy(q=>q.EvidenceId).Count();
  if(product is null)reasons.Add("缺少可售成品报价；市场参考价单独显示");else {observedTimes.Add(product.At);if(saleSamples<p.MinimumSamples)reasons.Add("成品售价独立样本不足");}
  decimal? gross=sale*recipe.OutputQuantity;
  var fees=context.Fees;bool feeValid=fees is not null&&fees.Policy.Valid&&fees.Replay==replay&&validPolicy&&Fresh(fees.At);
  decimal? saleFee=null,management=null;
  var exactFees=(saleCosts??[]).Where(f=>f.ItemId==recipe.ProductId&&f.Quantity==recipe.OutputQuantity&&f.Gross==gross&&f.SaleFee>=0&&f.ManagementFee>=0&&f.SaleFee+f.ManagementFee<f.Gross&&f.Replay==replay&&Fresh(f.At)&&!string.IsNullOrWhiteSpace(f.EvidenceId)).OrderByDescending(f=>f.At).ToArray();
  bool exactFeeConflict=exactFees.Length>0&&exactFees.Where(f=>f.At==exactFees[0].At).Select(f=>(f.SaleFee,f.ManagementFee)).Distinct().Count()>1;
  if(exactFeeConflict){feeValid=false;reasons.Add("同一时刻费用报价冲突");}
  else if(exactFees.Length>0){var f=exactFees[0];feeValid=true;saleFee=f.SaleFee;management=f.ManagementFee;evidence.Add("出售页精确费用:"+f.EvidenceId);observedTimes.Add(f.At);}
  if(!feeValid)reasons.Add("挂单手续费或管理费待核验/已过期");
  else if(saleFee is null) {evidence.Add("费用:"+fees!.Policy.EvidenceId);observedTimes.Add(fees.At);if(sale is {} unit&&recipe.OutputQuantity>0){var batches=ListingQuotaManager.Batches(recipe.OutputQuantity);saleFee=batches.Sum(q=>fees.Policy.SaleFee(unit*q));management=batches.Sum(q=>fees.Policy.ListingFee(unit*q,1));}}
  // Owned materials have replacement/opportunity value. Supplement cash replaces the missing
  // material estimate, it is never added on top of the full material value a second time.
  decimal? cost=ownedValue+supplement+recipe.ExtraFee;
  decimal? cashRequired=supplement+recipe.ExtraFee+management;
  decimal? profit=gross-saleFee-management-cost-p.RiskReserve;
  decimal? cashSurplus=gross-saleFee-management-supplement-recipe.ExtraFee-p.RiskReserve;
  decimal? roi=cost is >0?profit/cost:null;
  if(cost==0)reasons.Add("成本为零，利润率不可定义");
  if(cashRequired>p.Budget)reasons.Add("本次现金需求超过预算");
  bool cooled=availabilityValid&&availability!.NextEligibleAt>now,expired=availabilityValid&&availability!.ExpiresAt<=now,exhausted=availabilityValid&&!availability!.Repeatable&&availability.Remaining==0;
  if(cooled)reasons.Add("兑换冷却中");if(expired)reasons.Add("本轮商品已到期");if(exhausted)reasons.Add("本轮兑换次数已用完");
  if(replay)reasons.Add("历史回放，仅作历史分析");
  string status=!validPolicy||!validRecipe?"Blocked":expired?"Unavailable":cooled?"Cooling":exhausted?"Unavailable":!inventoryValid||!availabilityValid||!feeValid||product is null||materialTotal is null||saleSamples<p.MinimumSamples||reasons.Any(r=>r.Contains("冲突")||r.Contains("相差")||r.Contains("请核对"))?"NeedsData":cashRequired>p.Budget?"OverBudget":profit>=p.MinimumProfit&&roi>=p.MinimumRoi?"Suggested":"LowProfit";
  if(replay&&status=="Suggested")status="Historical";
  decimal netFactor=profit is >0?profit.Value/(profit.Value+100000):0,roiFactor=roi is >0?Math.Min(roi.Value,1):0;
  decimal urgency=availabilityValid&&availability!.ExpiresAt is {} end&&end>now?(decimal)(1/(1+(end-now).TotalMinutes/60)):0;
  if(inventoryValid)observedTimes.Add(inv!.At);if(availabilityValid)observedTimes.Add(availability!.At);
  decimal quota=availabilityValid&&!availability!.Repeatable&&availability.Remaining>0?1m/availability.Remaining.Value:0;
  if(availabilityValid&&availability!.Limited&&availability.RefreshRemaining>0&&availability.NextRefreshAt is{} refresh)urgency=Math.Max(urgency,(decimal)(.5/(1+Math.Max(0,(refresh-now).TotalMinutes)/60)));
  decimal stock=allUnits>0?ownedUnits/allUnits:0,freshness=observedTimes.Count>0&&validPolicy?Math.Max(0,1-(decimal)(now-observedTimes.Min()).TotalSeconds/p.PriceMaxAgeSeconds):0;
  var score=new ProfitScore(netFactor*45,roiFactor*20,urgency*15,stock*5,freshness*10,quota*5,netFactor*45+roiFactor*20+urgency*15+stock*5+freshness*10+quota*5);
  var amounts=new ProfitBreakdown(materialTotal,ownedValue,missingValue,supplement,historical,recipe.ExtraFee,reference?.UnitPrice,sale,gross,saleFee,management,p.RiskReserve,cost,cashRequired,profit,cashSurplus,roi,screenValid?screen!.MaterialTotal:null,screenValid?screen!.ProductReferenceTotal:null,screenValid&&screen!.SupplementCash is not null?"兑换页补齐报价":missingValue==0?"库存已齐，无补齐需求":"缺口数量×市场采购单价（估算）",gross-saleFee-management-historical-supplement-recipe.ExtraFee-p.RiskReserve);
  return new(recipe.Id,recipe.ProductId,productName,recipe.Contact,status,amounts,score,availabilityValid?availability!.Remaining:null,availabilityValid?availability!.RefreshRemaining:null,availabilityValid?availability!.NextEligibleAt:null,availabilityValid?availability!.ExpiresAt:null,reasons.Distinct().ToArray(),evidence.Distinct().ToArray(),now,replay,Repeatable:availabilityValid?availability!.Repeatable:null,Limited:availabilityValid?availability!.Limited:null,NextRefreshAt:availabilityValid?availability!.NextRefreshAt:null);
 }
 public static ProfitAdvice[] Rank(IEnumerable<ProfitAdvice> items) {
  int Group(string s)=>s switch{"Suggested"=>0,"Historical"=>1,"Cooling"=>2,"LowProfit"=>3,"OverBudget"=>4,"Unavailable"=>5,_=>6};
  return items.OrderBy(x=>Group(x.Status)).ThenByDescending(x=>x.Score.Total).ThenBy(x=>x.RecipeId,StringComparer.Ordinal).Select((a,n)=>a with{Rank=n+1}).ToArray();
 }
}
