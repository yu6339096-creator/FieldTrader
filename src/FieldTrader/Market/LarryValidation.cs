using System.IO;
using System.Text.Json;
using FieldTrader.Navigation;
namespace FieldTrader.Market;
public static class LarryValidation {
    public static void CheckEvidence(string replay,string output) {
        Directory.CreateDirectory(output);using var json=JsonDocument.Parse(File.ReadAllText(replay));
        var pages=json.RootElement.GetProperty("Rows").EnumerateArray().ToDictionary(r=>r.GetProperty("Image").GetString()!,r=>r.Clone());
        var checks=new List<object>();int failures=0;
        void Check(string name,bool passed){checks.Add(new{Name=name,Passed=passed});if(!passed)failures++;}
        PageObservation Page(string name)=>pages[name+".png"].GetProperty("Observation").Deserialize<PageObservation>(NavigationEvidenceLog.Json)!;
        LarryDetail Detail(string name)=>pages[name+".png"].GetProperty("Detail").Deserialize<LarryDetail>(NavigationEvidenceLog.Json)!;
        foreach(var (name,expected) in new[]{("S01","Home"),("S02","ContactList"),("S03","ContactProfile"),("S04","ContactShop"),("S06","Exchange"),("S08","Exchange"),("S10","Exchange"),("S11","ContactShop"),("S13","Exchange"),("S15","Exchange"),("S16","ContactShop")})Check(name+" page",Page(name).Page==expected);
        foreach(var (name,total,cash,market) in new[]{("S06",30937m,30937m,38745m),("S08",71014m,71014m,82325m),("S10",33148m,9705m,53946m),("S13",33900m,11704m,65664m),("S15",26308m,14927m,54468m)}) {
            var d=Detail(name);Check(name+" material total",d.MaterialTotal==total);Check(name+" extra cash",d.TopUpPrice==cash);Check(name+" market reference",d.ProductMarketPrice==market);Check(name+" gross reference",d.ReferenceGrossProfit==market-total);Check(name+" net unavailable",d.EstimatedNetProfit is null&&d.NetAvailability.StartsWith("NotAvailable"));
            Check(name+" independent cancel plan",pages[name+".png"].GetProperty("Failure").ValueKind==JsonValueKind.Null);
        }
        Check("TMK 2/1 owned vs required",Detail("S10").Materials.Any(m=>m.Name=="宠物蛋"&&m.Owned==2&&m.Required==1&&m.NeedsTopUp==false));
        Check("TMK five notes",Detail("S10").Materials.Any(m=>m.Name=="一叠便签"&&m.Required==5));
        Check("surgery owned SSD retains economic value",Detail("S13").Materials.Any(m=>m.Name=="固态硬盘"&&m.Owned==1&&m.Required==1)&&Detail("S13").MaterialTotal>Detail("S13").TopUpPrice);
        Check("grenades unknown recipes and refresh limits",LarryTargets.Items.Skip(5).All(i=>i.ExpectedRecipe is null&&i.Limit=="RefreshWindow"));
        var resolver=new VisualTargetResolver();var planner=new FieldTrader.Navigation.NavigationPlanner(resolver);
        foreach(var item in LarryTargets.Items) {
            var p=Page(item.Category=="医疗包"?"S04":item.Category=="伤害救治"?"S11":"S16");
            var location=resolver.ResolveDetailed(p,LarryRoutes.Query(item.Name,"product_card",LarryRoutes.Goods));
            // Painkiller is below the supplied initial damage viewport. Absence must plan a scan.
            Check(item.Id+" visible target or safe search",item.Id is "larry-pain" or "larry-mkii"?location.Status==LocationStatus.NOT_FOUND:location.Status==LocationStatus.RESOLVED);
        }
        int rejections=0;
        void Reject(Action operation){try{operation();}catch(NavigationEvidenceException){rejections++;}}
        var detailPage=Page("S10");
        foreach(string word in new[]{"补齐道具","购买","兑换","确认","出售","上架"})Reject(()=>NavigationSafety.Check(new("bad","Exchange",new(word,"primary_button",LarryRoutes.All,"道具兑换",LarryRoutes.All),InputOperation.Click,"ContactShop",TimeSpan.FromSeconds(10)),new(word,"primary_button",new(.6,.8,.1,.1),new(1,1),1,"x",DateTimeOffset.UtcNow,"x")));
        Check("all six transaction targets blocked",rejections==6);
        var shop=Page("S03");var entry=LarryRoutes.Next(shop,LarryTargets.Items[0],null)!;var safe=planner.Locate(shop,entry,0);
        Reject(()=>NavigationSafety.Check(entry with{Id="other-purchase"},safe.Target));Check("buy exception requires exact profile role",rejections==7);
        Reject(()=>resolver.ResolveDetailed(detailPage with{Vision=detailPage.Vision with{FrameId="other-frame"}},LarryRoutes.Cancel(detailPage).Target));Check("cross-frame evidence blocked",rejections==8);
        Check("unknown pauses",resolver.ResolveDetailed(detailPage with{Page="Unknown"},LarryRoutes.Cancel(detailPage).Target).Status==LocationStatus.UNKNOWN);
        var db=new MarketDatabase(Path.Combine(output,"dedup.sqlite"));var record=Detail("S13");var session=Guid.NewGuid().ToString("N");
        Check("first record inserts",db.StoreLarry(session,record,true));Check("unchanged image same session deduplicated",!db.StoreLarry(session,record with{At=record.At.AddMinutes(1)},true));
        Check("new priced screenshot persists",db.StoreLarry(session,record with{ContentHash=Detail("S15").ContentHash,At=record.At.AddMinutes(2)},true));
        File.WriteAllText(Path.Combine(output,"acceptance.json"),JsonSerializer.Serialize(new{Passed=failures==0,Failures=failures,Checks=checks,Recognition="Checks on actual saved YOLO/OCR outputs; destructive/cross-frame evidence perturbations only for rejection tests; no input"},NavigationEvidenceLog.Json));
        if(failures>0)throw new InvalidOperationException($"Larry evidence checks failed: {failures}");
    }
    public static async Task Replay(string config,string images,string output) {
        Directory.CreateDirectory(output);await using var pipeline=new PythonVisionPipeline(config);
        var rows=new List<object>();var classifier=new LarryPageClassifier();var resolver=new VisualTargetResolver();
        var db=new MarketDatabase(Path.Combine(output,"larry.sqlite"));var log=new NavigationEvidenceLog(output);
        foreach(var file in Directory.GetFiles(images,"*.png").Order()) {
            var s=await new ImageFileCapture(file).CaptureAsync(default);var raw=await pipeline.AnalyzeAsync(s,default);s=s with{Width=raw.Width,Height=raw.Height};
            var p=classifier.Classify(s,raw);log.Observation(p);var results=new List<object>();
            foreach(var item in LarryTargets.Items) {
                var next=LarryRoutes.Next(p,item,LarryPageClassifier.ActiveCategory(raw));
                if(next is not null) {
                    var found=resolver.ResolveDetailed(p,next.Target);PlannedAction? action=null;string? error=null;
                    if(found.Status==LocationStatus.RESOLVED)try {action=new FieldTrader.Navigation.NavigationPlanner(resolver).Locate(p,next,0);log.Plan(new(p,[action],TimeSpan.FromSeconds(40),TimeSpan.Zero,BatchMode.DynamicPage));}catch(Exception e){error=e.Message;}
                    results.Add(new{Item=item.Id,Step=next,Location=found,Plan=action,Error=error,Limit=LarryDetailReader.Limit(p,item)});
                }
            }
            LarryDetail? detail=null;string? failure=null;
            if(p.Page=="Exchange") {
                var match=LarryTargets.Items.Where(i=>resolver.Resolve(p,new(i.Name,"larry_detail",LarryRoutes.All,"道具兑换",LarryRoutes.All),true).Length==1).ToArray();
                if(match.Length==1)try {
                    detail=LarryDetailReader.Read(p,match[0]);db.StoreLarry("supplied-20261024",detail,true);
                    var cancel=LarryRoutes.Cancel(p);var plan=new FieldTrader.Navigation.NavigationPlanner(resolver).Locate(p,cancel,0);log.Plan(new(p,[plan],TimeSpan.FromSeconds(40),TimeSpan.Zero,BatchMode.DynamicPage));results.Add(new{CancelPlan=plan});
                }catch(Exception e){failure=e.Message;}
            }
            var row=new{Image=Path.GetFileName(file),RawVision=raw,Observation=p,ActiveCategory=LarryPageClassifier.ActiveCategory(raw),Targets=results,Detail=detail,Failure=failure,NativeInput=false};rows.Add(row);
            File.WriteAllText(Path.Combine(output,Path.GetFileNameWithoutExtension(file)+".json"),JsonSerializer.Serialize(row,NavigationEvidenceLog.Json));
        }
        File.WriteAllText(Path.Combine(output,"replay.json"),JsonSerializer.Serialize(new{NativeInput=false,pipeline.WorkerStarts,pipeline.StartupStages,Rows=rows},NavigationEvidenceLog.Json));
    }
}
