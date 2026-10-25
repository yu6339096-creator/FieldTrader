using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using FieldTrader.Navigation;
namespace FieldTrader.Market;
public sealed record LiveCaptureReceipt(string FrameId,DateTimeOffset CaptureRequestedAt,DateTimeOffset CaptureReceivedAt,string Source,string App,long WindowId,string Title,int Width,int Height,string Sha256,bool LiveGameCapture,bool InputEligible,int AutomationMouseClicks);
public static class LiveManualExchange {
 public static async Task Inspect(string config,string image,string receiptFile,string output) {
  Directory.CreateDirectory(output);var meta=JsonSerializer.Deserialize<LiveCaptureReceipt>(File.ReadAllText(receiptFile),TradingAnalysis.JsonOptions)??throw new InvalidDataException("Missing capture receipt");
  var hash=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(image))).ToLowerInvariant();
  if(!meta.LiveGameCapture||meta.Source!="sky.get_window_state"||!meta.App.EndsWith("UAGame.exe",StringComparison.OrdinalIgnoreCase)||hash!=meta.Sha256||meta.CaptureReceivedAt<meta.CaptureRequestedAt||meta.InputEligible||meta.AutomationMouseClicks!=0)throw new InvalidDataException("Read-only live capture provenance mismatch");
  var screen=new CapturedScreen(new(meta.FrameId,meta.CaptureReceivedAt,Path.GetFullPath(image)),hash,meta.Width,meta.Height,Live:true);
  await using var pipeline=new PythonVisionPipeline(config);var raw=await pipeline.AnalyzeAsync(screen,default);screen=screen with{Width=raw.Width,Height=raw.Height};
  var page=new LarryPageClassifier().Classify(screen,raw);var target=LarryTargets.Items.Single(i=>i.Id=="larry-100d");LarryDetail? detail=null;string? error=null;
  try{detail=LarryDetailReader.Read(page,target);}catch(NavigationEvidenceException e){error=e.Message;}
  var wallet=raw.Texts.Where(t=>t.Confidence >= OcrPolicy.MinimumConfidence&&t.Bounds.X>.83&&t.Bounds.Y<.08&&System.Text.RegularExpressions.Regex.IsMatch(Geometry.Normalize(t.Label),@"^\d[\d,]*k$")).Select(t=>t.Label).Distinct().ToArray();
  var catalog=new[]{new CatalogItem("larry-100d",target.Name,Product:true,Confirmed:true),new CatalogItem("radio","无线对讲机",Material:true,Confirmed:true)};var perception=MarketPerception.Fuse(raw,catalog);var facts=TradeScreenReader.Read(perception,catalog);
  var result=new{Perception=perception,TradeFacts=facts,Events=ExchangeScreenReader.Events(raw.Texts.Where(t=>t.Confidence >= OcrPolicy.MinimumConfidence).ToArray()),Mode="LiveGameCaptureReadOnly",Capture=meta,Screen=screen,RawVision=raw,Observation=page,Detail=detail,Error=error,Review=new{Product=target.Name,Quantity=1,Material="无线对讲机",MaterialQuantity=1,SpendCap=40000,QuotedCash=detail?.TopUpPrice,MaterialValue=detail?.MaterialTotal,ProductReference=detail?.ProductMarketPrice,GrossReferenceDifference=detail?.ReferenceGrossProfit,ExpectedNetProfit=(decimal?)null,NetProfitReason="No current sale quote or verified sale/management fees",WithinCap=detail?.TopUpPrice is >=0 and <=40000,WalletDisplay=wallet,ExactWalletBalance=(decimal?)null,RequiresExplicitUserConfirmation=true,AutomaticSubmission=false,HistoricalReplay=false,ProofOfTrade=false},AutomationMouseClicks=0};
  File.WriteAllText(Path.Combine(output,Path.GetFileNameWithoutExtension(image)+".recognition.json"),JsonSerializer.Serialize(result,TradingAnalysis.JsonOptions));
  Console.WriteLine(JsonSerializer.Serialize(new{page.Page,detail?.ItemName,detail?.MaterialTotal,detail?.TopUpPrice,detail?.ProductMarketPrice,detail?.ReferenceGrossProfit,detail?.RecipeStatus,detail?.MaterialsComplete,Error=error},TradingAnalysis.JsonOptions));
 }
}
