using System.Diagnostics;
using System.IO;
using System.Text.Json;
using FieldTrader.Navigation;
namespace FieldTrader.Market;
public static class VisualNavigationValidation {
    public static async Task Replay(string config,string images,string output) {
        Directory.CreateDirectory(output);await using var vision=new PythonVisionPipeline(config);var classifier=new MarketPageClassifier();var resolver=new VisualTargetResolver();
        var rows=new List<object>();var log=new NavigationEvidenceLog(output);
        foreach(var file in Directory.GetFiles(images,"*.png").Order()) {
            var timer=Stopwatch.StartNew();var s=await new ImageFileCapture(file).CaptureAsync(default);double capture=timer.Elapsed.TotalMilliseconds;
            var v=await vision.AnalyzeAsync(s,default);s=s with{Width=v.Width,Height=v.Height};timer.Restart();var p=classifier.Classify(s,v);double classification=timer.Elapsed.TotalMilliseconds;
            log.Observation(p);var queries=Queries(p);var found=new List<object>();
            foreach(var query in queries) {
                timer.Restart();var target=resolver.ResolveDetailed(p,query);double fusion=timer.Elapsed.TotalMilliseconds;string? rejection=null;PlannedAction? action=null;
                if(target.Status==LocationStatus.RESOLVED)try {
                    var after=query.VisualClass=="layout_menu"?query.Name=="市场"?"Market":query.Name=="仓库"?"Warehouse":"ContactList":p.Page;
                    var step=new NavigationStep("inspect:"+query.Name,p.Page,query,InputOperation.Click,after,TimeSpan.FromSeconds(20));
                    timer.Restart();action=new FieldTrader.Navigation.NavigationPlanner(resolver).Locate(p,step,0);
                    log.Plan(new(p,[action],TimeSpan.FromSeconds(20),TimeSpan.Zero,BatchMode.DynamicPage));
                }catch(Exception ex){rejection=ex.Message;}
                found.Add(new{Query=query,Location=target,Plan=action,Rejection=rejection,FusionMs=fusion,PlanningMs=timer.Elapsed.TotalMilliseconds});
            }
            var row=new{Image=Path.GetFileName(file),Observation=p,Targets=found,CaptureMs=capture,PageClassificationMs=classification,ModelInitializationMs=v.Performance?.StartupMs,CoordinateSystem="Boxes normalized original image; click client origin + physical pixels; no OCR-center shortcut",NativeInput=false};
            rows.Add(row);File.WriteAllText(Path.Combine(output,Path.GetFileNameWithoutExtension(file)+"-readonly.json"),JsonSerializer.Serialize(row,NavigationEvidenceLog.Json));
        }
        File.WriteAllText(Path.Combine(output,"readonly-results.json"),JsonSerializer.Serialize(new{NativeInput=false,vision.WorkerStarts,vision.StartupMs,vision.StartupStages,Rows=rows},NavigationEvidenceLog.Json));
    }
    public static TargetQuery[] Queries(PageObservation p) {
        var queries=new List<TargetQuery>();var full=new Region(0,0,1,1);
        if(p.LayoutRegions?.Any(r=>r.Token.Label=="layout_menu")==true)foreach(var name in new[]{"仓库","联络人","市场"})queries.Add(VisualNavigationRoutes.Menu(name));
        if(p.Page=="ContactList")foreach(var name in NavigationSceneAdapter.Contacts)queries.Add(new(name,"contact_entry",new(.19,.21,.36,.52),NavigationSceneAdapter.Contacts[0],new(.19,.21,.36,.52)));
        if(p.Page is "Market" or "Contact") {
            var fused=MarketPerception.Fuse(p.Vision,[]);
            foreach(var quote in fused.Quotes.Where(q=>q.Context=="market-card").Take(12))queries.Add(new(quote.ItemName,"product_card",new(.18,.12,.8,.8),"我的交易",new(0,0,.4,.1)));
            if(p.Page=="Contact")foreach(var region in p.Vision.Regions.Where(t=>t.Label=="product_card"&&t.Confidence>=.95)) {
                var names=p.Vision.Texts.Where(t=>t.Confidence>=.95&&Geometry.Contains(region.Bounds,t.Bounds)&&t.Bounds.Y<region.Bounds.Y+region.Bounds.Height*.25&&t.Label.Any(char.IsLetter)).ToArray();
                if(names.Length==1)queries.Add(new(names[0].Label,"product_card",full,"联络人",new(0,.94,1,.06)));
            }
        }
        foreach(var name in new[]{"关闭","取消","返回"})if(p.Vision.Texts.Any(t=>Geometry.Normalize(t.Label)==Geometry.Normalize(name)))queries.Add(new(name,"secondary_button",full,name,full));
        // Primary submission is deliberately queried to document safety rejection, never added to an executable plan.
        foreach(var name in new[]{"购买","兑换","上架","确认"})if(p.Vision.Texts.Any(t=>Geometry.Normalize(t.Label)==Geometry.Normalize(name)))queries.Add(new(name,"primary_button",full,name,full));
        return queries.Distinct().ToArray();
    }
}
