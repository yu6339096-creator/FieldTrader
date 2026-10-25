using System.IO;
using System.Text.Json;
using FieldTrader.Navigation;
namespace FieldTrader.Market;
public interface IRealTradeInput { bool IsNative {get;} Task ClickAsync(AutoBuyAction action,CapturedScreen screen,CancellationToken token); }
public sealed record AutoBuyOutcome(string State,string Reason,int Clicks,decimal ReservedQuote,decimal? ActualCash,string EvidenceDirectory);
// One selected detail, one transaction. No sale, patrol loop, refresh or blind retry.
public sealed class AutoBuyRunner(IScreenSource capture,IVisionPipeline vision,IRealTradeInput input,string root,Action<string>? progress=null) {
 public async Task<AutoBuyOutcome> Run(AutoBuyRequest request,Func<bool> enabled,CancellationToken token) {
  Directory.CreateDirectory(root);string journal=Path.Combine(root,input.IsNative?"real-transactions.jsonl":"test-transactions.jsonl");
  string session=Guid.NewGuid().ToString("N"),folder=Path.Combine(root,session);Directory.CreateDirectory(folder);
  int clicks=0;decimal reserved=0;bool attempted=false,journalOwned=false;FileStream? sessionLock=null;var seen=new HashSet<string>();CapturedScreen? last=null;
  void Guard(){token.ThrowIfCancellationRequested();if(!enabled())throw new OperationCanceledException("自动交易开关关闭",token);}
  void Record(string state,object details) {
   if(!journalOwned)return;
   var bytes=System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new{Session=session,At=DateTimeOffset.UtcNow,State=state,NativeGameInput=input.IsNative,Request=request,Details=details},TradingAnalysis.JsonOptions).Replace("\r","").Replace("\n","")+"\n");
   using var file=new FileStream(journal,FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.Read);
   // Preserve a truncated older record without merging a new run into its unfinished line.
   if(file.Length>0){file.Seek(-1,SeekOrigin.End);if(file.ReadByte()!='\n'){file.Seek(0,SeekOrigin.End);file.WriteByte((byte)'\n');}}
   file.Seek(0,SeekOrigin.End);file.Write(bytes);file.Flush(true);
  }
  async Task<(CapturedScreen Screen,VisionResult Vision)> Observe() {
   progress?.Invoke("正在截取游戏画面并识别；OCR门槛75%…");
   Guard();var s=await capture.CaptureAsync(token);var v=await vision.AnalyzeAsync(s,token);Guard();
   VisualTargetResolver.Validate(new(s,v,"Unknown","",[]));
   if(!s.Live||s.WindowHandle==0||!seen.Add(s.Frame.Id)||last is {} old&&(s.Frame.CapturedAt<=old.Frame.CapturedAt||s.WindowHandle!=old.WindowHandle||s.Width!=old.Width||s.Height!=old.Height||s.ScreenX!=old.ScreenX||s.ScreenY!=old.ScreenY))throw new InvalidOperationException("截图重复、窗口改变或非实时游戏画面");
   last=s;File.WriteAllText(Path.Combine(folder,s.Frame.Id+".json"),JsonSerializer.Serialize(new{Screen=s,RawVision=v},TradingAnalysis.JsonOptions));
   AutoBuyRules.Guard(v);return(s,v);
  }
  AutoBuyOutcome Finish(string state,string reason,decimal? actual=null){Record(state,new{Reason=reason,Clicks=clicks,ReservedQuote=reserved,ActualCash=actual,Evidence=folder});return new(state,reason,clicks,reserved,actual,folder);}
  await NavigationInputGate.Shared.WaitAsync(token);NavigationInputGate.Enter();
  try {
   sessionLock=new FileStream(Path.Combine(root,"execution.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);journalOwned=true;
   // Journal entries are history, not persistent execution locks. Every explicit Run
   // starts with new frames, counters and budget, regardless of an older outcome.
   Record("Started",new{NativeGameInput=input.IsNative,MaximumTransactions=1,RuntimeReset=true});
   var baseline=await Observe();bool toppedUp=false,exchangeSubmitted=false,confirmationSubmitted=false;
   for(int step=0;step<3;step++) {
    var plan=AutoBuyRules.Read(baseline.Vision,request,toppedUp||exchangeSubmitted);
    progress?.Invoke($"已识别 {request.ItemName}；操作：{plan.Button}；本次报价 {plan.QuotedSpend}。正在重新截图核价…");
    if(plan.Button=="补齐道具"&&toppedUp||plan.Button=="兑换"&&exchangeSubmitted||plan.Button=="确定"&&confirmationSubmitted)throw new InvalidOperationException("同一步骤已提交，禁止重试");
    var fresh=await Observe();var current=AutoBuyRules.Read(fresh.Vision,request,toppedUp||exchangeSubmitted);
    if(plan.Button!=current.Button||plan.Page!=current.Page||plan.QuotedSpend!=current.QuotedSpend)throw new InvalidOperationException("提交前价格或操作变化，停止重新核价");
    if(DateTimeOffset.UtcNow<fresh.Screen.Frame.CapturedAt||DateTimeOffset.UtcNow-fresh.Screen.Frame.CapturedAt>TimeSpan.FromSeconds(5))throw new InvalidOperationException("识别画面已过期");
    if(reserved+current.QuotedSpend>request.MaximumSpend)throw new InvalidOperationException("累计支出报价超过上限");
    Guard();reserved+=current.QuotedSpend;attempted=true;
    progress?.Invoke($"核价通过：{current.QuotedSpend} / 上限 {request.MaximumSpend}。正在执行真实鼠标操作：{current.Button}…");
    Record("Attempted",new{Action=current,Frame=fresh.Screen.Frame.Id,Hash=fresh.Screen.ContentHash,Click=CoordinateTransform.ToScreen(current.Bounds,fresh.Screen),ReservedQuote=reserved});
    await input.ClickAsync(current,fresh.Screen,token);clicks++;Guard();Record("Sent",new{Button=current.Button,ResultVerified=false});
    progress?.Invoke($"鼠标点击已发送（{clicks}次），正在截图核对结果…");
    toppedUp|=current.Button=="补齐道具";exchangeSubmitted|=current.Button=="兑换";confirmationSubmitted|=current.Button=="确定";
    await Task.Delay(600,token);var after=await Observe();
    if(request.Kind==AutoBuyKind.MarketPurchase) {
     var p=MarketPerception.Fuse(after.Vision,[new("selected",request.ItemName,Product:true,Confirmed:true)]);
     var receipt=TradeScreenReader.Read(p,[new("selected",request.ItemName,Product:true,Confirmed:true)]).Receipt;
     if(receipt is {Complete:true}&&AutoBuyRules.NameMatches(receipt.ItemName,request.ItemName)&&receipt.Quantity==request.Quantity)
      return Finish(receipt.Total>request.MaximumSpend?"CompletedOverLimit":"Completed",receipt.Total>request.MaximumSpend?"购买结果已核实，但实付超过上限；停止并保留异常":"独立购买结果的商品、数量、均价和实付合计核对成功",receipt.Total);
     return Finish("Unknown","购买已提交，未读到完整结果；本轮结束，不自动重试");
    }
    if(AutoBuyRules.SameInventoryView(fresh.Vision,after.Vision,request.ItemName)
       &&AutoBuyRules.VisibleProductCount(after.Vision,request.ItemName)==AutoBuyRules.VisibleProductCount(fresh.Vision,request.ItemName)+1)
    {
     var verify=await Observe();
     if(AutoBuyRules.SameInventoryView(after.Vision,verify.Vision,request.ItemName)&&AutoBuyRules.VisibleProductCount(verify.Vision,request.ItemName)==AutoBuyRules.VisibleProductCount(after.Vision,request.ItemName))
      return Finish("Completed","连续新画面核实同一仓库视口新增100D医疗包×1；实际扣款未取得独立凭证，保留未知");
     return Finish("Unknown","新增物品未在后续画面中稳定复核，本轮结束");
    }
    // Optional material-completion dialog or owned-material exchange detail is the next step.
    AutoBuyRules.Read(after.Vision,request,toppedUp||exchangeSubmitted);baseline=after;
   }
   return Finish("Unknown","单笔步骤上限已到；结果未确认，不继续");
  }catch(InputNotSentException ex) when(clicks==0){
   Record("InputNotSent",new{Reason=ex.Message,ReleasedQuote=reserved,Clicks=0});reserved=0;
   return Finish("Blocked",ex.Message);
  }catch(Exception ex) when(ex is not OutOfMemoryException){return Finish(attempted?"Unknown":"Blocked",ex.Message);}
  finally{sessionLock?.Dispose();NavigationInputGate.Exit();NavigationInputGate.Shared.Release();}
 }
}
