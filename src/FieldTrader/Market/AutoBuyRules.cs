using FieldTrader.Navigation;
namespace FieldTrader.Market;
public enum AutoBuyKind { MarketPurchase, Larry100DExchange }
public sealed record AutoBuyRequest(AutoBuyKind Kind,string ItemName,int Quantity,decimal MaximumSpend,decimal? MaximumUnitPrice=null);
public sealed record AutoBuyAction(string Button,Region Bounds,decimal QuotedSpend,string Page,double Confidence=1);
public static class AutoBuyRules {
 public static string Key(string text)=>Geometry.Normalize(text);
 public static bool NameMatches(string raw,string name)=>Key(raw)==Key(name)
  ||Key(name)==Key("100D战地医疗包")&&new[]{"1000战地医疗包","100D战地包","1000战地包"}.Any(n=>Key(n)==Key(raw));
 public static VisualToken[] Words(VisionResult v)=>LarryPageClassifier.Unique(v.Texts.Where(t=>t.Confidence >= OcrPolicy.MinimumConfidence&&t.Bounds.Valid));
 public static void Guard(VisionResult v) {
  if(Words(v).Any(t=>new[]{"验证码","安全验证","反作弊","封禁","交易失败","购买失败","兑换失败","部分购买失败","价格发生变化","仓库已满","空间不足","已售罄"}.Any(t.Label.Contains)))throw new InvalidOperationException("出现验证、失败、涨价或库存异常；停止且不重试");
 }
 public static AutoBuyAction Read(VisionResult v,AutoBuyRequest request,bool allowConfirmation=false) {
  if(!Enum.IsDefined(request.Kind)||string.IsNullOrWhiteSpace(request.ItemName)||request.ItemName.Length>150||request.Quantity is <1 or >5||request.MaximumSpend<=0||request.MaximumUnitPrice is <=0)throw new InvalidOperationException("请设置有效操作、商品、1至5数量和明确支出上限");
  Guard(v);var words=Words(v);
  if(request.Kind==AutoBuyKind.Larry100DExchange&&(request.Quantity!=1||request.ItemName!="100D战地医疗包"))throw new InvalidOperationException("首轮兑换入口仅支持100D战地医疗包×1");
  var confirmation=ExchangeConfirmationReader.Read(v,[new("selected",request.ItemName,Product:true,Confirmed:true)]);
  if(confirmation is not null) {
   if(!allowConfirmation||request.Kind!=AutoBuyKind.Larry100DExchange)throw new InvalidOperationException("非本流程预期确认框");
   return new("确定",confirmation.ConfirmTextBounds,0,"ExchangeConfirm",confirmation.Confidence);
  }
  if(words.Any(t=>t.Label.Contains("已集齐全部兑换道具"))||v.Regions.Any(t=>t.Label=="confirm_dialog"&&t.Confidence>=.6))throw new InvalidOperationException("确认框的固定文案或商品未匹配");
  if(words.Count(t=>t.Bounds.X<.5&&t.Bounds.Y<.3&&NameMatches(t.Label,request.ItemName))!=1)throw new InvalidOperationException("详情标题商品不唯一或与请求不符");
  bool Has(string name)=>words.Any(t=>Key(t.Label)==Key(name)||Key(t.Label).StartsWith(Key(name)+":",StringComparison.Ordinal));
  string button;decimal cost;string page;
  if(request.Kind==AutoBuyKind.MarketPurchase) {
   var right=v.Texts.Where(t=>t.Confidence >= OcrPolicy.MinimumConfidence&&t.Bounds.Valid&&t.Bounds.X>.5).ToArray();
   if(!Has("购买数量")||!right.Any(t=>Key(t.Label)=="平均单价"||Key(t.Label).StartsWith("平均单价:",StringComparison.Ordinal))||Has("道具兑换"))throw new InvalidOperationException("不是市场购买详情");
   var unit=TradeScreenReader.RowNumber(right,"平均单价");var total=TradeScreenReader.RowNumber(right,"合计");
   var quantity=MaterialMarketQuantity.Read(v);
   if(quantity!=request.Quantity||unit is not >0||total!=unit*quantity||request.MaximumUnitPrice is {} limit&&unit>limit)throw new InvalidOperationException("数量或单价/合计核对失败，或超过单价上限");
   cost=total!.Value;button="购买";page="Purchase";
  } else {
   if(!Has("道具兑换")||!Has("兑换列表")||!Has("需要提交以下道具"))throw new InvalidOperationException("不是兑换详情");
   var radio=words.Count(t=>t.Bounds.X>.45&&t.Bounds.Y<.3&&Key(t.Label)==Key("无线对讲机"));
   var counts=words.Where(t=>t.Bounds.X>.45&&t.Bounds.Y<.3&&System.Text.RegularExpressions.Regex.IsMatch(Key(t.Label),@"^[01]/1$")).ToArray();
   if(radio!=1||counts.Length!=1)throw new InvalidOperationException("无线对讲机×1配方数量未独立核实");
   bool owned=Key(counts[0].Label)=="1/1";
   // Financial labels can merge with the preceding amount. Use the shared 75% OCR policy.
   var topup=ExchangePanelMoney.Read(v.Texts.Where(t=>t.Bounds.X>.45&&t.Bounds.Y>.7),"补齐价格");
   if(!owned&&topup is not >0)throw new InvalidOperationException("缺少本次补齐支出报价");
   button=owned?"兑换":"补齐道具";cost=owned?0:topup!.Value;page="Exchange";
  }
  if(cost>request.MaximumSpend)throw new InvalidOperationException("超过本轮支出上限");
  var buttons=words.Where(t=>t.Bounds.X>.5&&t.Bounds.Y>.5&&Key(t.Label)==Key(button)).ToArray();
  if(buttons.Length!=1)throw new InvalidOperationException("当前详情提交按钮不唯一");
  return new(button,buttons[0].Bounds,cost,page,buttons[0].Confidence);
 }
 public static int VisibleProductCount(VisionResult v,string name)=>Words(v).Count(t=>t.Bounds.X>.58&&NameMatches(t.Label,name));
 public static bool SameInventoryView(VisionResult before,VisionResult after,string name) {
  HashSet<string> Anchors(VisionResult v)=>Words(v).Where(t=>t.Bounds.X>.58&&t.Bounds.Y>.12&&t.Bounds.Y<.9&&!NameMatches(t.Label,name)&&!t.Label.Any(char.IsDigit)&&t.Label.Length>1).Select(t=>Key(t.Label)+":"+Math.Round(t.Bounds.X,2)+":"+Math.Round(t.Bounds.Y,2)).ToHashSet();
  var a=Anchors(before);var b=Anchors(after);return a.Count>=3&&b.Count>=3&&a.Intersect(b).Count()>=Math.Max(a.Count,b.Count)*.8;
 }
}
