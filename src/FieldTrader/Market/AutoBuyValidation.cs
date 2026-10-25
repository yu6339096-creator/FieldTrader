using System.IO;
using System.Text.Json;
namespace FieldTrader.Market;
public static class AutoBuyValidation {
 public static void ReadStoredQuote(string file) {
  using var json=JsonDocument.Parse(File.ReadAllText(file));
  var raw=json.RootElement.GetProperty("RawVision").Deserialize<VisionResult>(TradingAnalysis.JsonOptions)!;
  var action=AutoBuyRules.Read(raw,new(AutoBuyKind.Larry100DExchange,"100D战地医疗包",1,40000),true);
  Console.WriteLine(JsonSerializer.Serialize(new{Mode="StoredRecognitionRegression",NativeGameInput=false,Action=action},TradingAnalysis.JsonOptions));
 }
 public static async Task Run(string output) {
  Directory.CreateDirectory(output);var checks=new List<string>();
  void Check(bool ok,string name){if(!ok)throw new InvalidDataException(name);checks.Add(name);}
  VisualToken T(string s,double x,double y,double w=.12)=>new(s,.99,new(x,y,w,.025));
  VisualToken[] stock=[T("工具箱",.7,.2),T("水壶",.8,.3),T("望远镜",.7,.4)];
  VisionResult V(params VisualToken[] texts)=>new("fixture",DateTimeOffset.UtcNow,"fixture",2560,1440,[],texts,0,0,0);
  VisionResult Exchange(decimal money)=>V([T("100D战地医疗包",.1,.1),T("道具兑换",.6,.08),T("需要提交以下道具",.5,.15),T("无线对讲机",.5,.2),T("0/1",.53,.24,.04),T("兑换列表",.5,.3),T("补齐价格："+money,.72,.81,.2),T("合计价格："+money,.5,.81,.2),T("补齐道具",.7,.88),..stock]);
  var request=new AutoBuyRequest(AutoBuyKind.Larry100DExchange,"100D战地医疗包",1,40000);
  var before=Exchange(35456);var confirm=V([T("已集齐全部兑换道具，是否兑换1000战地医疗包",.37,.45,.28),T("确定",.56,.54,.04),T("取消",.38,.54,.04),..stock]);
  var finished=V([..stock,T("100D战地包",.65,.6)]);
  Check(AutoBuyRules.Read(before,request).QuotedSpend==35456,"补齐支出读取并核对无线对讲机1件配方");
  var merged=before with{Texts=before.Texts.Where(t=>!t.Label.Contains("补齐价格")&&!t.Label.Contains("合计价格")).Concat(new[]{T("合计价格：",.51,.81,.05),new VisualToken("31284|补齐价格：",.9406,new(.5766,.81,.0993,.0232)),T("31284",.693,.81,.0469)}).ToArray()};
  Check(AutoBuyRules.Read(merged,request).QuotedSpend==31284,"实测合并标签94%仍按独立补齐列读取31284");
  var threshold=before with{Texts=before.Texts.Select(t=>t with{Confidence=.75}).ToArray()};
  Check(AutoBuyRules.Read(threshold,request).QuotedSpend==35456,"标题、材料、数量、报价和按钮均在75%时可识别");
  var under=threshold with{Texts=threshold.Texts.Select(t=>t.Label.StartsWith("补齐价格")?t with{Confidence=.7499}:t).ToArray()};
  try{AutoBuyRules.Read(under,request);throw new InvalidDataException("低于75%的补齐价不得采用");}catch(InvalidOperationException){checks.Add("低于75%的补齐标签拒绝且不借用合计价");}
  Check(AutoBuyRules.Read(confirm with{Texts=confirm.Texts.Select(t=>t with{Confidence=.75}).ToArray()},request,true).Button=="确定","75%固定确认文案与完整商品名核实");
  var observedMerge=merged with{Texts=merged.Texts.Select(t=>t.Label.Contains("补齐价格")?t with{Label="2|补齐价格：",Confidence=.8685756623744965}:t.Label=="31284"?t with{Label="33022"}:t).ToArray()};
  Check(AutoBuyRules.Read(observedMerge,request).QuotedSpend==33022,"实测86.9%标签2|补齐价格可读取33022");
  var absolute=AbsoluteMouseCoordinates.Normalize(1927,1276,0,0,2560,1440);
  Check(absolute.X*2560L/65536==1927&&absolute.Y*1440L/65536==1276,"真实鼠标绝对坐标映射回正确物理像素");
  absolute=AbsoluteMouseCoordinates.Normalize(-1919,1,-1920,0,4480,1440);
  Check(absolute.X*4480L/65536-1920==-1919,"负坐标多显示器映射正确");
  var missing=merged with{Texts=merged.Texts.Where(t=>!t.Label.Contains("补齐价格")).ToArray()};
  void MissingQuote(){try{AutoBuyRules.Read(missing,request);}catch(InvalidOperationException){return;}throw new InvalidDataException("不能用合计价代替补齐价");}MissingQuote();checks.Add("缺少补齐标签时不借用合计价格");
  Check(AutoBuyRules.Read(confirm,request,true).Button=="确定","可选确认文案与目标全名关联");
  void Reject(Action action,string name){try{action();}catch(InvalidOperationException){checks.Add(name);return;}throw new InvalidDataException(name);}
  Reject(()=>AutoBuyRules.Read(before,request with{MaximumSpend=30000}),"超预算禁止提交");
  Reject(()=>AutoBuyRules.Read(before,request with{ItemName="TMK野战医疗包"}),"错误兑换商品拒绝");
  Reject(()=>AutoBuyRules.Read(confirm,request),"未经过本流程的确认框拒绝");
  Reject(()=>AutoBuyRules.Read(before with{Texts=before.Texts.Append(T("交易失败",.4,.02)).ToArray()},request),"失败不追价重试");
  var frames=new Sequence([before,before,confirm,confirm,finished]);var mouse=new FakeMouse();
  var result=await new AutoBuyRunner(frames,frames,mouse,Path.Combine(output,"exchange")).Run(request,()=>true,default);
  Check(result.State=="Completed"&&result.Clicks==2&&result.ReservedQuote==35456&&result.ActualCash is null,"补齐到可选确认到库存新增完整状态流；未猜实付金额");
  Check(mouse.Buttons.SequenceEqual(new[]{"补齐道具","确定"}),"只提交两个预期按钮，无第二件");
  var change=new Sequence([before,Exchange(35457)]);var changedMouse=new FakeMouse();
  result=await new AutoBuyRunner(change,change,changedMouse,Path.Combine(output,"changed")).Run(request,()=>true,default);
  Check(result.State=="Blocked"&&changedMouse.Buttons.Count==0,"提交边界报价变化零点击停止");
  var unknown=new Sequence([before,before,V(T("未知画面",.4,.4))]);var unknownMouse=new FakeMouse();var unknownRoot=Path.Combine(output,"unknown");
  result=await new AutoBuyRunner(unknown,unknown,unknownMouse,unknownRoot).Run(request,()=>true,default);
  Check(result.State=="Unknown"&&unknownMouse.Buttons.Count==1,"提交后未知结果不自动重试");
  var restarted=new Sequence([Exchange(33022),Exchange(33022),confirm,confirm,finished]);var restartMouse=new FakeMouse();
  result=await new AutoBuyRunner(restarted,restarted,restartMouse,unknownRoot).Run(request,()=>true,default);
  Check(result.State=="Completed"&&restartMouse.Buttons.Count==2&&result.ReservedQuote==33022,"未知结果结束后新轮重新截图核价，不携带旧报价或次数");
  var partial=new Sequence([before,before]);var broken=new FakeMouse{Fail=true};
  result=await new AutoBuyRunner(partial,partial,broken,Path.Combine(output,"partial")).Run(request,()=>true,default);
  Check(result.State=="Unknown"&&result.Clicks==0,"输入异常保留未决状态，不冒充点击成功");
  var noInputFrames=new Sequence([before,before]);var noInputMouse=new FakeMouse{NotSent=true};var noInputRoot=Path.Combine(output,"not-sent");
  result=await new AutoBuyRunner(noInputFrames,noInputFrames,noInputMouse,noInputRoot).Run(request,()=>true,default);
  Check(result.State=="Blocked"&&result.Clicks==0&&result.ReservedQuote==0,"明确在点击发送前失败，释放报价并记录阻止状态");
  noInputMouse.NotSent=false;
  result=await new AutoBuyRunner(noInputFrames,noInputFrames,noInputMouse,noInputRoot).Run(request,()=>false,default);
  Check(result.State=="Blocked"&&result.Reason!="上一笔提交结果未核对，禁止再次提交","明确未发送不会造成永久未决锁；不会自动重试");
  var off=new Sequence([before]);result=await new AutoBuyRunner(off,off,new FakeMouse(),Path.Combine(output,"off")).Run(request,()=>false,default);
  Check(result.State=="Blocked"&&result.Clicks==0,"关闭开关不提交");
  Check(!AutoBuyRules.SameInventoryView(confirm,finished with{Texts=[T("100D战地包",.65,.6)]},request.ItemName),"仓库视口不一致不能证明新增");
  var purchase=V(T("无线对讲机",.2,.2),T("购买数量",.52,.62),T("5",.7,.62,.025),T("平均单价：100",.55,.72,.2),T("合计：500",.55,.77,.2),T("购买",.7,.88)) with{Regions=[new("trade_panel",.99,new(.5,.2,.45,.74)),new("quantity_field",.99,new(.68,.61,.06,.05))]};
  var buyRequest=new AutoBuyRequest(AutoBuyKind.MarketPurchase,"无线对讲机",5,600,110);
  Check(AutoBuyRules.Read(purchase,buyRequest).QuotedSpend==500,"市场独立数量、均价与合计一致后可提交");
  Reject(()=>AutoBuyRules.Read(purchase,buyRequest with{Quantity=1}),"数量与请求不符不买");
  Reject(()=>AutoBuyRules.Read(purchase,buyRequest with{MaximumUnitPrice=90}),"超过单价上限不买");
  var receipt=V(T("购买结果",.4,.2),T("已购买",.4,.25),T("无线对讲机",.4,.4),T("×5",.55,.45,.04),T("均价：100",.5,.5),T("总价：500",.5,.55)) with{Regions=[new("result_region",.99,new(.3,.15,.5,.5))]};
  var buying=new Sequence([purchase,purchase,receipt]);var buyMouse=new FakeMouse();
  result=await new AutoBuyRunner(buying,buying,buyMouse,Path.Combine(output,"purchase")).Run(buyRequest,()=>true,default);
  Check(result.State=="Completed"&&result.Clicks==1&&result.ActualCash==500,"市场一次购买，独立结果核实实付金额");
  foreach(string state in new[]{"Unknown","Attempted","Sent","CompletedOverLimit"}) {
   var priorRoot=Path.Combine(output,"prior-"+state);Directory.CreateDirectory(priorRoot);string journal=Path.Combine(priorRoot,"test-transactions.jsonl");
   var original=JsonSerializer.Serialize(new{Session="old-run",State=state,Details=new{ActualCash=(decimal?)null}});File.WriteAllText(journal,original+"\n");
   var nextFrames=new Sequence([before,before,confirm,confirm,finished]);var nextMouse=new FakeMouse();
   result=await new AutoBuyRunner(nextFrames,nextFrames,nextMouse,priorRoot).Run(request,()=>true,default);
   Check(result.State=="Completed"&&nextMouse.Buttons.Count==2&&File.ReadLines(journal).First()==original,$"旧{state}记录仅作为历史，不拦截新轮且原文保留");
  }
  var truncatedRoot=Path.Combine(output,"truncated");Directory.CreateDirectory(truncatedRoot);var truncatedJournal=Path.Combine(truncatedRoot,"test-transactions.jsonl");File.WriteAllText(truncatedJournal,"{unfinished");
  var truncatedFrames=new Sequence([before,before,confirm,confirm,finished]);
  result=await new AutoBuyRunner(truncatedFrames,truncatedFrames,new FakeMouse(),truncatedRoot).Run(request,()=>true,default);
  var preserved=File.ReadAllLines(truncatedJournal);
  Check(result.State=="Completed"&&preserved[0]=="{unfinished"&&JsonDocument.Parse(preserved[1]).RootElement.GetProperty("State").GetString()=="Started","旧残缺日志不再锁定，保留旧行并独立写入新轮");
  var cancelledRoot=Path.Combine(output,"cancelled");var cancelledFrames=new Sequence([before,before]);using var cancelled=new CancellationTokenSource();
  result=await new AutoBuyRunner(cancelledFrames,cancelledFrames,new FakeMouse{AfterClick=cancelled.Cancel},cancelledRoot).Run(request,()=>true,cancelled.Token);
  Check(result.State=="Unknown"&&result.Clicks==1,"点击返回后急停仍准确记录已发送一次");
  var resumedFrames=new Sequence([before,before,confirm,confirm,finished]);
  result=await new AutoBuyRunner(resumedFrames,resumedFrames,new FakeMouse(),cancelledRoot).Run(request,()=>true,default);
  Check(result.State=="Completed"&&result.Clicks==2,"急停结束后再次开始为全新一轮");
  File.WriteAllText(Path.Combine(output,"checks.json"),JsonSerializer.Serialize(new{Mode="SyntheticStateMachineTests",NativeGameInput=false,Count=checks.Count,Checks=checks},TradingAnalysis.JsonOptions));
  Console.WriteLine($"{checks.Count} automatic single-trade checks passed; no native game input");
 }
 private sealed class FakeMouse:IRealTradeInput {
  public bool IsNative=>false;public bool Fail,NotSent;public Action? AfterClick;public List<string> Buttons=[];
  public Task ClickAsync(AutoBuyAction action,CapturedScreen screen,CancellationToken token){token.ThrowIfCancellationRequested();if(NotSent)throw new InputNotSentException("fixture failed before click");if(Fail)throw new IOException("fixture partial input");Buttons.Add(action.Button);AfterClick?.Invoke();return Task.CompletedTask;}
 }
 private sealed class Sequence(VisionResult[] frames):IScreenSource,IVisionPipeline {
  private int index;private VisionResult? current;
  public Task<CapturedScreen> CaptureAsync(CancellationToken token){token.ThrowIfCancellationRequested();string id=Guid.NewGuid().ToString("N");var at=DateTimeOffset.UtcNow;current=frames[Math.Min(index++,frames.Length-1)] with{FrameId=id,ContentHash=id,CapturedAt=at};return Task.FromResult(new CapturedScreen(new(id,at,"synthetic-fixture.png"),id,2560,1440,1,0,0,true));}
  public Task<VisionResult> AnalyzeAsync(CapturedScreen screen,CancellationToken token)=>Task.FromResult(current!);
 }
}
