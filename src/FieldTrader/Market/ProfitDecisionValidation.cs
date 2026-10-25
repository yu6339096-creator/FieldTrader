using System.IO;
using System.Text.Json;
namespace FieldTrader.Market;
public static class ProfitDecisionValidation {
 public static void ReplayMaterials(string source,string output) {
  Directory.CreateDirectory(output);var db=new MarketDatabase(Path.Combine(output,"historical-materials.sqlite"));foreach(var item in FieldTrader.Navigation.MaterialMarketTargets.Items)db.UpsertItem(new(item.Id,item.Name,item.Category,Material:true,Confirmed:true));
  using var doc=JsonDocument.Parse(File.ReadAllText(source));var service=new ProfitRecommendationService(db);var reads=new List<object>();
  foreach(var row in doc.RootElement.GetProperty("Rows").EnumerateArray()) {var raw=row.GetProperty("RawVision").Deserialize<VisionResult>(FieldTrader.Navigation.NavigationEvidenceLog.Json)!;var p=MarketPerception.Fuse(raw,db.VisionCatalog());db.Store(p,true);service.Observe(p,true);reads.Add(new{Image=row.GetProperty("Image").GetString(),p.Page,p.Quotes,service.Issues});}
  var prices=db.ProfitPrices(true,DateTimeOffset.MinValue);var expected=new[]{("chip",32249m),("radio",31916m),("rope",40504m)};bool passed=expected.All(e=>prices.Any(p=>p.ItemId==e.Item1&&p.Purpose==QuotePurpose.MaterialPurchaseUnit&&p.UnitPrice==e.Item2&&p.Replay));
  File.WriteAllText(Path.Combine(output,"actual-material-evidence.json"),JsonSerializer.Serialize(new{Passed=passed,HistoricalOnly=true,Prices=prices,Reads=reads,NativeInput=false,Transactions=0},TradingAnalysis.JsonOptions));if(!passed)throw new InvalidOperationException("Actual material observations failed canonical price mapping");
 }
 public static void Run(string output) {
  Directory.CreateDirectory(output);var tests=new List<object>();void Check(bool pass,string name){tests.Add(new{Name=name,Passed=pass});if(!pass)throw new InvalidOperationException(name);}
  var now=DateTimeOffset.Parse("2026-10-25T10:00:00+09:00");var recipe=new ExchangeRecipe("fixture","fixture contact","product",1,[new("m1",2),new("m2",1)],10,VerificationStatus.Verified,"verified fixture",1);
  ProfitPrice Price(string id,QuotePurpose purpose,decimal value,string evidence)=>new(evidence,id,purpose,value,now,PriceSource.OcrObservation,evidence,false);
  var prices=new[]{Price("m1",QuotePurpose.MaterialPurchaseUnit,100,"m1"),Price("m2",QuotePurpose.MaterialPurchaseUnit,200,"m2"),Price("product",QuotePurpose.ProductSaleUnit,1000,"sale")};
  var inventory=new ProfitInventory(now,"inventory",true,false,[new("lot","m1",1,30,Acquisition.ManualVerified,SaleEligibility.Unknown,"lot-receipt")]);
  var available=new ProfitAvailability(recipe.Id,false,1,null,now.AddHours(1),false,null,null,now,"quota",false);
  var context=new ProfitAdviceContext(new(10000,0,0,0,5,1,60),new(new(.1m,.02m,5,true,"fees"),now,false),inventory,[available]);
  ProfitAdvice Evaluate(ProfitAdviceContext? c=null,ProfitPrice[]? q=null,ExchangeRecipe? r=null,ProfitExchangeQuote? screen=null,bool replay=false)=>UnifiedProfitEngine.Evaluate(r??recipe,"fixture product",q??prices,c??context,screen,now,replay);
  var exact=Evaluate();Check(exact.Status=="Suggested"&&exact.Amounts.ExpectedNetProfit==460,"全成本净利润460，不是售价减补齐700");
  Check(exact.Amounts is{RecipeMaterialValue:400,OwnedMaterialValue:100,MissingMaterialValue:300,SupplementCash:300,HistoricalInventoryCost:30,EconomicCost:410,CashRequired:335,CashSurplusAfterSale:560},"材料总值、历史成本、经济成本、现金需求分别核算");
  Check(exact.Amounts.HistoricalCostProfit==530,"历史成本口径账面利润与经济净利、现金差额分别显示");
  Check(exact.Amounts is{SaleFee:100,ManagementFee:25}&&exact.Amounts.ProfitRate==460m/410,"两种费用各扣一次，利润率以经济成本为分母");
  var quoted=Evaluate(screen:new(recipe.Id,400,300,1,now,"screen",false,1000));Check(quoted.Amounts.ExpectedNetProfit==460&&quoted.Amounts.ScreenMaterialValue==400,"兑换页总值不与补齐价格重复相加");
  Check(Evaluate(context with{Inventory=inventory with{Lots=[]}}).Amounts is{OwnedMaterialValue:0,SupplementCash:400,EconomicCost:410},"库存为空但完整时，整套材料均计入缺口");
  Check(Evaluate(context with{Inventory=null}).Status=="NeedsData","未知库存不当作零库存");
  Check(Evaluate(context with{Inventory=inventory with{Complete=false}}).Amounts.ExpectedNetProfit is null,"不完整库存不产生虚假净利润");
  Check(Evaluate(context with{Inventory=inventory with{At=now.AddMinutes(-5)}}).Status=="NeedsData","库存过期需重读");
  Check(Evaluate(context with{Fees=null}).Amounts.ExpectedNetProfit is null,"费用未知时净利润为未知");
  Check(Evaluate(context with{Fees=context.Fees! with{Policy=context.Fees!.Policy with{Verified=false}}}).Status=="NeedsData","不套用未验证费率");
  Check(Evaluate(context with{Fees=context.Fees! with{At=now.AddMinutes(-5)}}).Amounts.ManagementFee is null,"旧费率不计为当前有效费用");
  Check(Evaluate(q:prices.Select(p=>p.ItemId=="m2"?p with{At=now.AddMinutes(-5)}:p).ToArray()).Status=="NeedsData","材料旧报价不能触发建议");
  Check(Evaluate(q:prices.Select(p=>p.ItemId=="m2"?p with{At=now.AddMinutes(1)}:p).ToArray()).Status=="NeedsData","未来时间报价拒绝");
  Check(Evaluate(q:prices.Select(p=>p.ItemId=="m2"?p with{Source=PriceSource.Reference}:p).ToArray()).Amounts.RecipeMaterialValue is null,"网站参考价不充当采购材料价");
  Check(Evaluate(q:prices.Select(p=>p.ItemId=="m2"?p with{Replay=true}:p).ToArray()).Status=="NeedsData","历史回放不串入实时成本");
  Check(Evaluate(q:prices.Select(p=>p.ItemId=="product"?p with{Purpose=QuotePurpose.MarketCardReference}:p).ToArray()) is{Status:"NeedsData",Amounts.GrossRevenue:null,Amounts.ProductReferenceUnit:1000},"成品市场参考与可售报价分开");
  Check(Evaluate(q:prices.Append(prices[0] with{Id="conflict",UnitPrice=101}).ToArray()).Reasons.Any(s=>s.Contains("冲突")),"同刻冲突报价不任取一个");
  Check(Evaluate(context with{Availability=[]}).Status=="NeedsData","次数未知不默认一次");
  Check(Evaluate(context with{Availability=[available with{Remaining=0}]}).Status=="Unavailable","次数用完不进入可优先建议");
  Check(Evaluate(context with{Availability=[available with{NextEligibleAt=now.AddHours(5)}]}).Status=="Cooling","冷却单列且显示下次时间");
  Check(Evaluate(context with{Availability=[available with{ExpiresAt=now.AddSeconds(-1)}]}).Status=="Unavailable","过期限时方案不可推荐");
  Check(Evaluate(context with{Availability=[available with{Repeatable=true,Remaining=null}]}).Status=="Suggested","无限兑换不假造次数");
  Check(Evaluate(context with{Availability=[available with{RefreshRemaining=2}]}).Status=="NeedsData","普通兑换不能借用限时刷新次数");
  Check(Evaluate(context with{Availability=[available with{Limited=true,RefreshRemaining=0}]}).Status=="Suggested","刷新用完不妨碍兑换本轮尚有次数的商品");
  Check(Evaluate(context with{Availability=[available with{At=now.AddMinutes(-5)}]}).Status=="NeedsData","次数和冷却证据也校验新鲜度");
  Check(Evaluate(context with{Policy=context.Policy with{Budget=334}}).Status=="OverBudget","预算包含补齐、兑换费与管理费");
  Check(Evaluate(context with{Policy=context.Policy with{MinimumProfit=461}}).Status=="LowProfit","净利润门槛生效");
  Check(Evaluate(context with{Policy=context.Policy with{MinimumRoi=2}}).Status=="LowProfit","利润率门槛生效");
  Check(Evaluate(r:recipe with{OutputQuantity=6}).Amounts is{GrossRevenue:6000,SaleFee:600,ManagementFee:130,ExpectedNetProfit:4855},"成品批量按每单最多5件分摊费用与固定管理费");
  Check(Evaluate(r:recipe with{OutputQuantity=5},screen:new(recipe.Id,400,300,5,now,"batch-screen",false,120310)).Amounts.ProductReferenceTotal==120310,"兑换页参考值不因x5再乘五");
  Check(Evaluate(screen:new(recipe.Id,999,300,1,now,"screen",false)).Status=="NeedsData","逐项估值与整套估值明显冲突需重核");
  Check(Evaluate(r:recipe with{Status=VerificationStatus.Unverified}).Status=="Blocked","未核验配方只列问题");
  var replayContext=context with{Fees=context.Fees! with{Replay=true},Inventory=inventory with{Replay=true},Availability=[available with{Replay=true}]};
  Check(Evaluate(replayContext,prices.Select(p=>p with{Replay=true}).ToArray(),replay:true).Status=="Historical","历史完整案例仍仅为历史建议");
  var ordered=UnifiedProfitEngine.Rank([Evaluate(context with{Availability=[available with{Remaining=0}]}),exact]);Check(ordered[0].RecipeId==recipe.Id&&ordered[0].Status=="Suggested"&&ordered[0].Rank==1,"可用建议优先于不可兑换案例");
  Check(exact.Score.RemainingQuota>Evaluate(context with{Availability=[available with{Repeatable=true,Remaining=null}]}).Score.RemainingQuota,"剩余兑换次数影响优先分项");
  Check(Evaluate(context with{Availability=[available with{ExpiresAt=now.AddMinutes(1)}]}).Score.Urgency>exact.Score.Urgency,"临近结束提高时间优先级");
  var feeQuote=new ProfitSaleCostQuote("product",1,1000,80,20,now,"exact-fees",false);
  var exactFeeAdvice=UnifiedProfitEngine.Evaluate(recipe,"fixture",prices,context with{Fees=null},null,now,saleCosts:[feeQuote]);
  Check(exactFeeAdvice.Status=="Suggested"&&exactFeeAdvice.Amounts.ExpectedNetProfit==485,"同商品数量总额的出售页两类实际费用优先于估算费率");
  Check(UnifiedProfitEngine.Evaluate(recipe,"fixture",prices,context with{Fees=null},null,now,saleCosts:[feeQuote with{Quantity=5}]).Amounts.ExpectedNetProfit is null,"其他出售数量的费用不能复用");
  Check(UnifiedProfitEngine.Evaluate(recipe,"fixture",prices,context with{Fees=null},null,now,saleCosts:[feeQuote,feeQuote with{SaleFee=81,EvidenceId="conflicting-fees"}]).Status=="NeedsData","同刻费用冲突不能任取");
  Check(UnifiedProfitEngine.Evaluate(recipe,"fixture",prices,context with{Fees=null},null,now,saleCosts:[feeQuote with{At=now.AddMinutes(-2)}]).Amounts.ExpectedNetProfit is null,"出售页费用过期失效");
  var highCurrent=prices.Where(p=>p.ItemId!="product").Concat(new[]{prices[2] with{Id="s1",EvidenceId="s1",At=now.AddSeconds(-2)},prices[2] with{Id="s2",EvidenceId="s2",At=now.AddSeconds(-1)},prices[2] with{Id="s3",EvidenceId="s3",UnitPrice=5000}}).ToArray();
  Check(Evaluate(q:highCurrent).Amounts.ConservativeSaleUnit==1000,"新鲜低分位限制短暂高报价造成的利润虚高");
  var dbPath=Path.Combine(output,"fixture-"+Guid.NewGuid().ToString("N")+".sqlite");var db=new MarketDatabase(dbPath);
  foreach(var i in new[]{new CatalogItem("m1","材料甲",Confirmed:true),new("m2","材料乙",Confirmed:true),new("product","成品",Confirmed:true)})db.UpsertItem(i);
  db.SaveAlias("材料别名","m1","manual-fixture");Check(db.ResolveCanonical("材料别名")?.Id=="m1","别名指向稳定商品ID");
  bool conflict=false;try{db.SaveAlias("材料别名","m2","conflict");}catch(InvalidDataException){conflict=true;}Check(conflict,"别名冲突不覆盖");
  db.UpsertItem(new("duplicate-id","材料别名",Confirmed:true));Check(db.Catalog().Length==3,"按别名导入不创建重复商品");
  db.SaveUnifiedRecipe(recipe with{ProductId="成品",Materials=[new("材料别名",2),new("m2",1)]});Check(db.Recipes().Single().Materials.Any(m=>m.ItemId=="m1"),"配方名称与别名统一映射商品ID");
  foreach(var price in prices)db.SaveProfitPrice(price);Check(!db.SaveProfitPrice(prices[0]),"报价凭证幂等");
  conflict=false;try{db.SaveProfitPrice(prices[0] with{UnitPrice=999});}catch(InvalidDataException){conflict=true;}Check(conflict,"相同报价ID不能悄悄改变金额");
  db.SaveProfitContext(context);db.SaveProfitExchangeQuote(new(recipe.Id,400,300,1,now,"quote",false));var stored=new MarketDatabase(dbPath);Check(stored.Recommend(now).Single().Amounts.ExpectedNetProfit==460,"数据库重开保持同一配方与完整成本");
  Check(stored.ProfitPrices(true,now.AddDays(-1)).Length==0,"历史价格与实时价格查询隔离");
  var before=JsonSerializer.Serialize(stored.SimulationSessions());stored.Recommend(now);Check(JsonSerializer.Serialize(stored.SimulationSessions())==before&&stored.SimulationLedger("fixture").Length==0,"建议计算不写交易或模拟账本");
  Check(stored.Recommend(now.AddHours(1)).Single().Status=="NeedsData","重开后旧建议按当前时间重新失效");
  var words=new[]{new VisualToken("材料别名",1,new(.1,.1,.1,.1))};var perception=new PerceptionResult(new("frame",now,"hash",100,100,[],words,0,0,0),MarketPage.Purchase,false,"",[new("材料别名",100,1,1,"row","purchase-unit",true)],[]);stored.Store(perception,false);Check(stored.Catalog().Length==3&&stored.Statistics("m1","purchase-unit").Samples==1,"市场采集按别名进入同一商品价格记录");
  var service=new ProfitRecommendationService(stored);service.Observe(perception,false);Check(stored.ProfitPrices(false,now.AddDays(-1)).Count(q=>q.ItemId=="m1"&&q.Purpose==QuotePurpose.MaterialPurchaseUnit)==2,"采集服务自动匹配材料用途与商品ID");
  var example=UnifiedProfitEngine.Rank([exact,Evaluate(context with{Fees=null}),Evaluate(context with{Availability=[available with{NextEligibleAt=now.AddHours(5)}]})]);
  File.WriteAllText(Path.Combine(output,"synthetic-examples.json"),JsonSerializer.Serialize(new{Synthetic=true,NotGameQuotes=true,Rows=example},TradingAnalysis.JsonOptions));
  File.WriteAllText(Path.Combine(output,"profit-context-template.json"),JsonSerializer.Serialize(ProfitAdviceContext.Empty(),TradingAnalysis.JsonOptions));
  File.WriteAllText(Path.Combine(output,"acceptance.json"),JsonSerializer.Serialize(new{Passed=true,Count=tests.Count,Checks=tests,RecommendationOnly=true,NativeInput=false,Transactions=0},TradingAnalysis.JsonOptions));Console.WriteLine($"{tests.Count} profit decision checks passed; recommendation only");
 }
}
