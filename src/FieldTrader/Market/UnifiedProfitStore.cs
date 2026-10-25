using System.IO;
using System.Text;
using System.Text.Json;
namespace FieldTrader.Market;
public sealed partial class MarketDatabase {
 private void InitializeUnifiedProfit() {
  using var db=Open();using var c=Command(db,"""
   CREATE TABLE IF NOT EXISTS CanonicalAliases(Alias TEXT PRIMARY KEY,ItemId TEXT NOT NULL REFERENCES ItemCatalog(Id),EvidenceId TEXT NOT NULL);
   CREATE TABLE IF NOT EXISTS ProfitPrices(Id TEXT PRIMARY KEY,ItemId TEXT NOT NULL REFERENCES ItemCatalog(Id),Purpose TEXT NOT NULL,ObservedAt TEXT NOT NULL,Replay INTEGER NOT NULL,Payload TEXT NOT NULL);
   CREATE INDEX IF NOT EXISTS IX_ProfitPriceScope ON ProfitPrices(ItemId,Purpose,Replay,ObservedAt);
   CREATE TABLE IF NOT EXISTS ProfitExchangeQuotes(RecipeId TEXT NOT NULL REFERENCES ExchangeRecipes(Id),Replay INTEGER NOT NULL,Payload TEXT NOT NULL,PRIMARY KEY(RecipeId,Replay));
   CREATE TABLE IF NOT EXISTS ProfitSaleCosts(EvidenceId TEXT PRIMARY KEY,Replay INTEGER NOT NULL,Payload TEXT NOT NULL);
   CREATE TABLE IF NOT EXISTS ProfitContext(Id INTEGER PRIMARY KEY CHECK(Id=1),Payload TEXT NOT NULL);
   """);c.ExecuteNonQuery();
 }
 public static string IdentityKey(string name)=>Geometry.Normalize(name.Normalize(NormalizationForm.FormKC));
 public CatalogItem? ResolveCanonical(string nameOrId) {
  var key=IdentityKey(nameOrId);var catalog=Catalog();var byId=catalog.Where(i=>i.Id==nameOrId).ToArray();if(byId.Length==1)return byId[0];
  var found=catalog.Where(i=>IdentityKey(i.Name)==key).ToList();using var db=Open();using var c=Command(db,"SELECT ItemId FROM CanonicalAliases WHERE Alias=$alias",("$alias",key));var id=c.ExecuteScalar() as string;
  if(id is not null)found.AddRange(catalog.Where(i=>i.Id==id));var unique=found.DistinctBy(i=>i.Id).ToArray();return unique.Length==1?unique[0]:null;
 }
 public CatalogItem[] VisionCatalog() {
  var catalog=Catalog();var names=new List<CatalogItem>(catalog);using var db=Open();using var c=Command(db,"SELECT Alias,ItemId FROM CanonicalAliases");using var r=c.ExecuteReader();while(r.Read()){var item=catalog.Single(i=>i.Id==r.GetString(1));names.Add(item with{Name=r.GetString(0)});}return names.DistinctBy(i=>(i.Id,IdentityKey(i.Name))).ToArray();
 }
 public string[] UnifiedCatalogIssues()=>Catalog().GroupBy(i=>IdentityKey(i.Name)).Where(g=>g.Count()>1).Select(g=>"名称归一后对应多个商品，待人工核对:"+string.Join(" / ",g.Select(i=>i.Id+"="+i.Name))).ToArray();
 public void BackfillRecentProfitPrices(DateTimeOffset now) {
  using var db=Open();using var c=Command(db,"SELECT p.Id,p.ItemId,p.Context,p.Price,p.ObservedAt,p.FrameId,p.Replay FROM PriceObservations p JOIN ItemCatalog i ON i.Id=p.ItemId WHERE i.Confirmed=1 AND p.Source='OcrObservation' AND p.Context IN('purchase-unit','sell-displayed','market-card') AND p.ObservedAt>=$since",("$since",now.AddDays(-1).ToUniversalTime().ToString("O")));
  using var r=c.ExecuteReader();var records=new List<ProfitPrice>();while(r.Read())records.Add(new("legacy-observation:"+r.GetInt64(0),r.GetString(1),r.GetString(2) switch{"purchase-unit"=>QuotePurpose.MaterialPurchaseUnit,"sell-displayed"=>QuotePurpose.ProductSaleUnit,_=>QuotePurpose.MarketCardReference},r.GetInt64(3),DateTimeOffset.Parse(r.GetString(4)),PriceSource.OcrObservation,r.GetString(5),r.GetBoolean(6)));r.Close();foreach(var price in records)SaveProfitPrice(price);
 }
 public void SaveAlias(string alias,string itemId,string evidenceId) {
  if(string.IsNullOrWhiteSpace(alias)||string.IsNullOrWhiteSpace(evidenceId)||Catalog().SingleOrDefault(i=>i.Id==itemId) is not{Confirmed:true})throw new InvalidDataException("别名需要已确认商品与来源");
  var key=IdentityKey(alias);using var db=Open();using var tx=db.BeginTransaction();
  using var lookup=Command(db,"SELECT ItemId FROM CanonicalAliases WHERE Alias=$key",("$key",key));lookup.Transaction=tx;var old=lookup.ExecuteScalar() as string;
  if(old is not null&&old!=itemId||Catalog().Any(i=>(IdentityKey(i.Name)==key||IdentityKey(i.Id)==key)&&i.Id!=itemId))throw new InvalidDataException("别名与另一商品冲突，需人工核对，未自动合并");
  using var c=Command(db,"INSERT INTO CanonicalAliases VALUES($key,$item,$source) ON CONFLICT(Alias) DO NOTHING",("$key",key),("$item",itemId),("$source",evidenceId));c.Transaction=tx;c.ExecuteNonQuery();tx.Commit();
 }
 public string[] ImportKnownAliases(string path) {
  if(!File.Exists(path))return[];using var doc=JsonDocument.Parse(File.ReadAllText(path));var issues=new List<string>();
  foreach(var row in doc.RootElement.GetProperty("items").EnumerateArray()) {
   var name=row.GetProperty("name").GetString()!;var candidates=row.GetProperty("aliases").EnumerateArray().Select(a=>a.GetString()!).Append(name).Select(ResolveCanonical).Where(i=>i is{Confirmed:true}).DistinctBy(i=>i!.Id).ToArray();if(candidates.Length!=1){if(candidates.Length>1)issues.Add("原型别名对应多个商品，未自动合并:"+name);continue;}var item=candidates[0]!;
   foreach(var a in row.GetProperty("aliases").EnumerateArray())try{SaveAlias(a.GetString()!,item.Id,"bundled-profile:"+name);}catch(InvalidDataException e){issues.Add(e.Message+":"+a.GetString());}
  }return issues.ToArray();
 }
 public void SaveUnifiedRecipe(ExchangeRecipe recipe) {
  string Resolve(string name)=>ResolveCanonical(name) is{Confirmed:true} item?item.Id:throw new InvalidDataException("配方商品无法唯一匹配:"+name);
  SaveRecipe(recipe with{ProductId=Resolve(recipe.ProductId),Materials=recipe.Materials.Select(m=>m with{ItemId=Resolve(m.ItemId)}).ToArray()});
 }
 public bool SaveProfitPrice(ProfitPrice price) {
  if(ResolveCanonical(price.ItemId) is not{Confirmed:true} item||item.Id!=price.ItemId||price.UnitPrice<=0||price.At==default||string.IsNullOrWhiteSpace(price.Id)||string.IsNullOrWhiteSpace(price.EvidenceId)||!Enum.IsDefined(price.Source)||!Enum.IsDefined(price.Purpose))throw new InvalidDataException("报价必须关联已确认商品、用途和凭证");
  using var db=Open();using var exists=Command(db,"SELECT Payload FROM ProfitPrices WHERE Id=$id",("$id",price.Id));var previous=exists.ExecuteScalar() as string;var payload=JsonSerializer.Serialize(price,TradingAnalysis.JsonOptions);
  if(previous is not null){if(previous!=payload)throw new InvalidDataException("同一报价ID出现不同内容");return false;}
  using var c=Command(db,"INSERT INTO ProfitPrices VALUES($id,$item,$purpose,$at,$replay,$json)",("$id",price.Id),("$item",price.ItemId),("$purpose",price.Purpose.ToString()),("$at",price.At.ToUniversalTime().ToString("O")),("$replay",price.Replay),("$json",payload));return c.ExecuteNonQuery()==1;
 }
 public ProfitPrice[] ProfitPrices(bool replay,DateTimeOffset since) {
  using var db=Open();using var c=Command(db,"SELECT Payload FROM ProfitPrices WHERE Replay=$replay AND ObservedAt>=$since ORDER BY ObservedAt",("$replay",replay),("$since",since.ToUniversalTime().ToString("O")));using var r=c.ExecuteReader();var values=new List<ProfitPrice>();while(r.Read())values.Add(JsonSerializer.Deserialize<ProfitPrice>(r.GetString(0),TradingAnalysis.JsonOptions)!);return values.ToArray();
 }
 public void SaveProfitExchangeQuote(ProfitExchangeQuote q) {
  if(!Recipes().Any(r=>r.Id==q.RecipeId&&r.OutputQuantity==q.OutputQuantity)||q.MaterialTotal<0||q.SupplementCash<0||q.At==default||string.IsNullOrWhiteSpace(q.EvidenceId))throw new InvalidDataException("兑换报价缺少唯一配方与来源");
  using var db=Open();using var c=Command(db,"INSERT INTO ProfitExchangeQuotes VALUES($id,$replay,$json) ON CONFLICT(RecipeId,Replay) DO UPDATE SET Payload=excluded.Payload",("$id",q.RecipeId),("$replay",q.Replay),("$json",JsonSerializer.Serialize(q,TradingAnalysis.JsonOptions)));c.ExecuteNonQuery();
 }
 public ProfitExchangeQuote[] ProfitExchangeQuotes(bool replay) {
  using var db=Open();using var c=Command(db,"SELECT Payload FROM ProfitExchangeQuotes WHERE Replay=$replay",("$replay",replay));using var r=c.ExecuteReader();var rows=new List<ProfitExchangeQuote>();while(r.Read())rows.Add(JsonSerializer.Deserialize<ProfitExchangeQuote>(r.GetString(0),TradingAnalysis.JsonOptions)!);return rows.ToArray();
 }
 public void SaveProfitSaleCost(ProfitSaleCostQuote quote) {
  if(ResolveCanonical(quote.ItemId) is not{Confirmed:true}||quote.Quantity<=0||quote.Gross<=0||quote.SaleFee<0||quote.ManagementFee<0||quote.SaleFee+quote.ManagementFee>=quote.Gross||string.IsNullOrWhiteSpace(quote.EvidenceId)||quote.At==default)throw new InvalidDataException("出售费用报价无效");
  using var db=Open();var payload=JsonSerializer.Serialize(quote,TradingAnalysis.JsonOptions);using var lookup=Command(db,"SELECT Payload FROM ProfitSaleCosts WHERE EvidenceId=$id",("$id",quote.EvidenceId+":"+quote.Replay));if(lookup.ExecuteScalar() is string old&&old!=payload)throw new InvalidDataException("同一费用凭证出现不同金额");
  using var c=Command(db,"INSERT OR IGNORE INTO ProfitSaleCosts VALUES($id,$replay,$payload)",("$id",quote.EvidenceId+":"+quote.Replay),("$replay",quote.Replay),("$payload",JsonSerializer.Serialize(quote,TradingAnalysis.JsonOptions)));c.ExecuteNonQuery();
 }
 public ProfitSaleCostQuote[] ProfitSaleCosts(bool replay) {using var db=Open();using var c=Command(db,"SELECT Payload FROM ProfitSaleCosts WHERE Replay=$replay",("$replay",replay));using var r=c.ExecuteReader();var rows=new List<ProfitSaleCostQuote>();while(r.Read())rows.Add(JsonSerializer.Deserialize<ProfitSaleCostQuote>(r.GetString(0),TradingAnalysis.JsonOptions)!);return rows.ToArray();}
 public ProfitAdviceContext ProfitContext() {using var db=Open();using var c=Command(db,"SELECT Payload FROM ProfitContext WHERE Id=1");return c.ExecuteScalar() is string json?JsonSerializer.Deserialize<ProfitAdviceContext>(json,TradingAnalysis.JsonOptions)??ProfitAdviceContext.Empty():ProfitAdviceContext.Empty();}
 public void SaveProfitContext(ProfitAdviceContext value) {
  if(value.Policy is null||value.Availability is null||value.Availability.Select(a=>a.RecipeId).Distinct().Count()!=value.Availability.Length||value.Inventory is{} inv&&inv.Lots is null)throw new InvalidDataException("建议配置缺少必要字段或状态重复");
  var known=Catalog().Select(i=>i.Id).ToHashSet();if(value.Inventory?.Lots.Any(l=>!known.Contains(l.ItemId))==true||value.Availability.Any(a=>!Recipes().Any(r=>r.Id==a.RecipeId)))throw new InvalidDataException("库存或兑换状态未匹配商品与配方ID");
  using var db=Open();using var c=Command(db,"INSERT INTO ProfitContext VALUES(1,$json) ON CONFLICT(Id) DO UPDATE SET Payload=excluded.Payload",("$json",JsonSerializer.Serialize(value,TradingAnalysis.JsonOptions)));c.ExecuteNonQuery();
 }
 public ProfitAdvice[] Recommend(DateTimeOffset now,bool replay=false) {
  var context=ProfitContext();var prices=ProfitPrices(replay,now.AddSeconds(-Math.Max(1,context.Policy.PriceMaxAgeSeconds)));var exchanges=ProfitExchangeQuotes(replay);var catalog=Catalog();var saleCosts=ProfitSaleCosts(replay);
  return UnifiedProfitEngine.Rank(Recipes().Select(r=>UnifiedProfitEngine.Evaluate(r,catalog.Single(i=>i.Id==r.ProductId).Name,prices,context,exchanges.SingleOrDefault(q=>q.RecipeId==r.Id),now,replay,saleCosts)));
 }
}
public sealed class ProfitRecommendationService(MarketDatabase db) {
 public string[] Issues {get;private set;}=[];
 public void Observe(PerceptionResult perception,bool replay) {
  if(perception.Paused)return;var issues=new List<string>();
  foreach(var q in perception.Quotes.Where(q=>q.Confidence>=OcrPolicy.MinimumConfidence&&q.DetectionConfidence>=.6)) {
   var item=db.ResolveCanonical(q.ItemName);if(item is not{Confirmed:true}){issues.Add("待关联商品:"+q.ItemName);continue;}
   QuotePurpose? purpose=q.Context switch{"purchase-unit"=>QuotePurpose.MaterialPurchaseUnit,"sell-displayed"=>QuotePurpose.ProductSaleUnit,"market-card"=>QuotePurpose.MarketCardReference,_=>null};if(purpose is null)continue;
   var id=MarketDatabase.IdentityKey(perception.Vision.FrameId+":"+perception.Vision.ContentHash+":"+q.RegionKey+":"+purpose+":"+replay);
   db.SaveProfitPrice(new(id,item.Id,purpose.Value,q.Price,perception.Vision.CapturedAt,PriceSource.OcrObservation,perception.Vision.FrameId,replay));
  }
  var sale=TradeScreenReader.Read(perception,db.VisionCatalog()).Sale;
  if(sale is{Quantity:>0,Gross:>0,SaleFee:>=0,ManagementFee:>=0}&&sale.ItemName is{} saleName&&db.ResolveCanonical(saleName) is{Confirmed:true} saleItem
   &&(sale.NetAfterSaleFee is null||sale.Gross-sale.SaleFee==sale.NetAfterSaleFee))db.SaveProfitSaleCost(new(saleItem.Id,sale.Quantity.Value,sale.Gross.Value,sale.SaleFee.Value,sale.ManagementFee.Value,perception.Vision.CapturedAt,perception.Vision.FrameId,replay));
  var exchange=ExchangeScreenReader.Read(perception);
  if(exchange?.ItemName is{} product&&db.ResolveCanonical(product) is{} canonical) {
   var possible=db.Recipes().Where(r=>r.ProductId==canonical.Id&&perception.Vision.Texts.Any(t=>t.Confidence >= OcrPolicy.MinimumConfidence&&t.Bounds.Y<.13&&MarketDatabase.IdentityKey(t.Label)==MarketDatabase.IdentityKey(r.Contact))).ToArray();
   // Materials must match the complete recipe, not merely the product title/contact.
   var matches=possible.Where(r=>exchange.Materials.Length==r.Materials.Length&&exchange.Materials.All(m=>m.Name is{} n&&db.ResolveCanonical(n) is{} i&&r.Materials.Any(x=>x.ItemId==i.Id&&x.Quantity==m.Required))).ToArray();
   if(matches.Length==1&&exchange.MaterialValuation is{} total) {
    var r=matches[0];db.SaveProfitExchangeQuote(new(r.Id,total,exchange.TopUpSpend,r.OutputQuantity,perception.Vision.CapturedAt,perception.Vision.FrameId,replay,exchange.ProductMarketPrice));
   }else issues.Add("兑换页未唯一绑定联络人、整套配方和产出量");
  }
  Issues=issues.Distinct().ToArray();
 }
}
