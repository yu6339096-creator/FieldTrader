using System.IO;
using System.Text.Json;
namespace FieldTrader.Market;
public static class ExchangeConfirmationValidation {
 public static void Run(string recognition,string output) {
  using var doc=JsonDocument.Parse(File.ReadAllText(recognition));
  var v=doc.RootElement.GetProperty("RawVision").Deserialize<VisionResult>(TradingAnalysis.JsonOptions)!;
  CatalogItem[] catalog=[new("larry-100d","100D战地医疗包",Product:true,Confirmed:true)];
  var checks=new List<string>();
  void Check(bool ok,string name){if(!ok)throw new InvalidDataException(name);checks.Add(name);}
  Check(ExchangeConfirmationReader.Read(v,catalog)?.ItemId=="larry-100d","真实确认截图OCR关联100D，容忍该物品已观察到的D/0混淆");
  var ocrOnly=v with{Regions=[]};
  Check(MarketPerception.Fuse(ocrOnly,catalog).Page==MarketPage.ExchangeConfirm,"无YOLO弹窗框时可由固定文案、商品和按钮布局识别");
  Check(ExchangeConfirmationReader.Read(v,[new("other","TMK野战医疗包",Product:true,Confirmed:true)]) is null,"错误目标拒绝关联");
  Check(ExchangeConfirmationReader.Read(v,[catalog[0] with{Confirmed=false}]) is null,"未确认商品不关联");
  Check(ExchangeConfirmationReader.Read(v with{Texts=v.Texts.Where(t=>!t.Label.Contains("已集齐")).ToArray()},catalog) is null,"可选弹窗不存在时不生成确认动作候选");
  Check(ExchangeConfirmationReader.Read(v with{Texts=v.Texts.Select(t=>t.Label.Contains("已集齐")?t with{Confidence=.5}:t).ToArray()},catalog) is null,"低置信度文案拒绝");
  Check(ExchangeConfirmationReader.Read(v with{Texts=v.Texts.Where(t=>Geometry.Normalize(t.Label)!="确定").ToArray()},catalog) is null,"缺少确定按钮拒绝");
  var guarded=MarketPerception.Fuse(v with{Texts=v.Texts.Append(new("安全验证",.99,new(.4,.3,.1,.03))).ToArray()},catalog);
  Check(guarded.Paused&&TradeScreenReader.Read(guarded,catalog).Confirmation is null,"安全暂停不产生确认候选");
  Directory.CreateDirectory(output);
  File.WriteAllText(Path.Combine(output,"confirmation-checks.json"),JsonSerializer.Serialize(new{Mode="RecognitionRegressionOnly",NativeInput=false,Count=checks.Count,Checks=checks,Matched=ExchangeConfirmationReader.Read(v,catalog)},TradingAnalysis.JsonOptions));
  Console.WriteLine($"{checks.Count} confirmation recognition checks passed; no game input");
 }
}
