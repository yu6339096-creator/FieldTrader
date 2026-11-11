using System.IO;
using System.Text.Json;
using FieldTrader.Navigation;
namespace FieldTrader.Market;

internal sealed class GameContactCircuitInput(string directory):IContactCircuitInput {
    public bool IsNative=>true;
    public Task ClickAsync(AutoBuyAction action,CapturedScreen screen,CancellationToken token)=>new GameTradeInput(directory).ClickAsync(action,screen,token);
    public Task NavigateAsync(ContactNavigationAction action,CapturedScreen screen,CancellationToken token) {
        var target=new VisualTarget(action.Name,"contact_navigation",action.Bounds,CoordinateTransform.ToScreen(action.Bounds,screen),OcrPolicy.MinimumConfidence,screen.Frame.Id,screen.Frame.CapturedAt,screen.ContentHash,["ContactCircuit: fresh OCR navigation"]);
        var step=new NavigationStep("contact:"+screen.Frame.Id,"ContactCircuit",new(action.Name,"contact_navigation",new(0,0,1,1),action.Name,new(0,0,1,1)),action.Operation,"ObserveNext",TimeSpan.FromSeconds(5),ScrollDelta:action.ScrollDelta);
        void Trace(string stage,object details) {
            Directory.CreateDirectory(directory);
            File.AppendAllText(Path.Combine(directory,"native-input-events.jsonl"),JsonSerializer.Serialize(new{At=DateTimeOffset.UtcNow,Frame=screen.Frame.Id,Stage=stage,Details=details})+"\n");
        }
        return WindowsInputController.ForGameNavigation(new(screen.WindowHandle),Trace).ExecuteAsync(new(0,step,target),screen,token);
    }
}
