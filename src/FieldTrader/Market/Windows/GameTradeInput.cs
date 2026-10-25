using FieldTrader.Navigation;
namespace FieldTrader.Market;
internal sealed class GameTradeInput(string directory):IRealTradeInput {
 public bool IsNative=>true;
 public Task ClickAsync(AutoBuyAction action,CapturedScreen screen,CancellationToken token) {
  var target=new VisualTarget(action.Button,"trade_submit",action.Bounds,CoordinateTransform.ToScreen(action.Bounds,screen),action.Confidence,screen.Frame.Id,screen.Frame.CapturedAt,screen.ContentHash,["Fresh OCR quote and bounded automatic single transaction"]);
  var step=new NavigationStep("real-buy:"+screen.Frame.Id,action.Page,new(action.Button,"trade_submit",new(0,0,1,1),action.Button,new(0,0,1,1)),InputOperation.Click,"PendingResult",TimeSpan.FromSeconds(5));
  void Trace(string stage,object details){
   System.IO.Directory.CreateDirectory(directory);
   string line=System.Text.Json.JsonSerializer.Serialize(new{At=DateTimeOffset.UtcNow,Frame=screen.Frame.Id,Stage=stage,Details=details});
   using var file=new System.IO.FileStream(System.IO.Path.Combine(directory,"native-input-events.jsonl"),System.IO.FileMode.Append,System.IO.FileAccess.Write,System.IO.FileShare.Read);
   var bytes=System.Text.Encoding.UTF8.GetBytes(line+"\n");file.Write(bytes);file.Flush(true);
  }
  return WindowsInputController.ForGameTransaction(new(screen.WindowHandle),Trace).ExecuteAsync(new(0,step,target),screen,token);
 }
}
