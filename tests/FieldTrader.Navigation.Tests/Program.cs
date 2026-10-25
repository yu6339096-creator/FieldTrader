using System.Text.Json;
using FieldTrader.Market;
using FieldTrader.Navigation;
using Planner = FieldTrader.Navigation.NavigationPlanner;

var results = new List<object>();
int failures = 0;
async Task Test(string name, Func<Task> body) {
    try { await body(); results.Add(new { name, passed = true }); Console.WriteLine("PASS " + name); }
    catch (Exception e) { failures++; results.Add(new { name, passed = false, error = e.ToString() }); Console.WriteLine("FAIL " + name + ": " + e.Message); }
}
void Check(bool condition, string reason = "assertion failed") { if (!condition) throw new Exception(reason); }
void Reject(Action action) { try { action(); } catch (NavigationEvidenceException) { return; } throw new Exception("Expected evidence rejection"); }
NavigationStep Step(string name = "A", string before = "Market", string after = "Market", bool stable = true, bool batch = true) =>
    new(name, before, Fixtures.Query(name), InputOperation.Click, after, TimeSpan.FromSeconds(3),
        FailurePolicy.RetryFresh, batch, stable ? LayoutEffect.Stable : LayoutEffect.MayChange, RequiredAfterText: [before == after ? "done:" + name : "page:" + after]);
NavigationTask Job(params NavigationStep[] steps) => new(Guid.NewGuid().ToString("N"), steps, ExecutionMode.Simulated,
    BatchMode.StablePage, ActionInterval: TimeSpan.Zero);
async Task<NavigationResult> Run(World w, NavigationTask task, NavigationControl? control = null, CancellationToken ct = default) {
    using var owned = control is null ? new NavigationControl() : null;
    return await new NavigationExecutor(w, w, new Classifier(), w, w).RunAsync(task, control ?? owned!, ct);
}

await Test("single target joins class, text, area, context", async () => {
    var w = new World(); var p = await w.Observe();
    var found = new VisualTargetResolver().Resolve(p, Fixtures.Query("A"));
    Check(found.Length == 1 && found[0].Category == "product_card" && found[0].FrameId == p.Screen.Frame.Id);
    Check(found[0].Click == new PixelPoint(300, 250));
});
await Test("one frame resolves multiple targets", async () => {
    var w = new World(); var p = await w.Observe(); var resolver = new VisualTargetResolver();
    Check(resolver.Resolve(p, Fixtures.Query("A")).Length == 1 && resolver.Resolve(p, Fixtures.Query("B")).Length == 1);
    Check(w.Captures == 1);
});
await Test("OCR alone, wrong class, context and area rejected", async () => {
    var w = new World(); var p = await w.Observe(); var r = new VisualTargetResolver();
    Check(r.Resolve(p with { Vision = p.Vision with { Regions = [] } }, Fixtures.Query("A")).Length == 0);
    Check(r.Resolve(p, Fixtures.Query("A") with { VisualClass = "secondary_button" }).Length == 0);
    Check(r.Resolve(p, Fixtures.Query("A") with { ContextText = "联络人" }).Length == 0);
    Check(r.Resolve(p, Fixtures.Query("A") with { Area = new(.7,.7,.2,.2) }).Length == 0);
});
await Test("ambiguous same text cannot select arbitrary target", async () => {
    var w = new World { Names = ["A", "A"] }; var p = await w.Observe();
    Check(new VisualTargetResolver().Resolve(p, Fixtures.Query("A")).Length == 2);
    Reject(() => new Planner(new()).Locate(p, Step(), 0));
});
await Test("overlapping boxes and duplicate OCR rejected", async () => {
    var p = await new World().Observe(); var r = new VisualTargetResolver();
    var v = p.Vision with { Regions = [p.Vision.Regions[0], p.Vision.Regions[0]] };
    Check(r.Resolve(p with { Vision = v }, Fixtures.Query("A")).Length == 0);
    v = p.Vision with { Texts = [..p.Vision.Texts, p.Vision.Texts[2]] };
    Check(r.Resolve(p with { Vision = v }, Fixtures.Query("A")).Length == 0);
});
await Test("provenance, NaN confidence and bounds fail closed", async () => {
    var p = await new World().Observe();
    Reject(() => VisualTargetResolver.Validate(p with { Vision = p.Vision with { FrameId = "wrong" } }));
    Reject(() => VisualTargetResolver.Validate(p with { Vision = p.Vision with { ContentHash = "wrong" } }));
    Reject(() => VisualTargetResolver.Validate(p with { Vision = p.Vision with { Regions = [new("product_card", double.NaN, Fixtures.Box(0))] } }));
    Reject(() => CoordinateTransform.ToScreen(new(-1,0,.1,.1), p.Screen));
});
await Test("coordinates: negative monitor origin and physical pixels", () => {
    var s = new CapturedScreen(new("x", DateTimeOffset.UtcNow, ""), "x", 1920,1080,1,-1920,100,true);
    Check(CoordinateTransform.ToScreen(new(.25,.25,.5,.5),s) == new PixelPoint(-960,640));
    var r = CoordinateTransform.FromLetterbox(160,230,320,180,1920,1080,1.0/3,0,140);
    Check(Math.Abs(r.X-.25)<.00001 && Math.Abs(r.Y-.25)<.00001 && Math.Abs(r.Width-.5)<.00001);
    return Task.CompletedTask;
});
await Test("single action independently verifies result", async () => {
    var w = new World(); var result = await Run(w, Job(Step(batch:false)));
    Check(result.State == NavigationState.SUCCESS && result.Completed == 1 && w.Executed.Count == 1);
    Check(w.Captures == 3 && w.Events.Any(e => e.Shape == ExecutionShape.SINGLE_ACTION));
    foreach (var state in new[]{NavigationState.IDLE,NavigationState.CAPTURE,NavigationState.RECOGNIZE,NavigationState.LOCATE,NavigationState.PLAN,NavigationState.EXECUTE,NavigationState.VERIFY,NavigationState.SUCCESS})
        Check(w.Events.Any(e=>e.State==state), "missing state " + state);
});
await Test("stable batch reuses original targets with fresh boundary guards", async () => {
    var w = new World(); var result = await Run(w, Job(Step(),Step("B")));
    Check(result.State == NavigationState.SUCCESS && result.Completed == 2);
    Check(w.Executed.Select(a=>a.Target.FrameId).Distinct().Count()==1);
    Check(w.Executed.Select(a=>a.Order).SequenceEqual(new[]{0,1}) && w.Captures==5);
    Check(w.Events.Any(e=>e.Shape==ExecutionShape.BATCH_ACTION));
});
await Test("dynamic page ends batch and relocates next step", async () => {
    var w = new World(); w.OnInput = a => { if(a.Step.Id=="A") {w.Page="Contact"; w.Shift=.05;} return Task.CompletedTask; };
    var result = await Run(w, Job(Step(after:"Contact",stable:false), Step("B","Contact","Contact")) with { BatchMode=BatchMode.DynamicPage });
    Check(result.State==NavigationState.SUCCESS && result.Completed==2);
    Check(w.Executed[0].Target.FrameId!=w.Executed[1].Target.FrameId && w.Executed[1].Target.Bounds.X==.55);
});
await Test("three-step state machine Market Contact Warehouse", async () => {
    var w = new World(); w.OnInput = a => {w.Page=a.Step.ExpectedAfter; return Task.CompletedTask;};
    var result=await Run(w,Job(Step(after:"Contact",stable:false),Step("B","Contact","Warehouse",false),Step("A","Warehouse","Warehouse") with {Id="A2",RequiredAfterText=["done:A2"]}));
    Check(result.State==NavigationState.SUCCESS && result.Completed==3);
});
await Test("unexpected layout change interrupts stable batch", async () => {
    var w=new World(); w.OnInput=a=>{w.Shift=.01;return Task.CompletedTask;};
    var result=await Run(w,Job(Step(),Step("B")));
    Check(result.State==NavigationState.PAUSED && w.Executed.Count==1 && result.Completed==0);
});
await Test("target disappears before next action", async () => {
    var w=new World(); w.BeforeCapture=n=>{if(n==4)w.Names=["A"];};
    var result=await Run(w,Job(Step(),Step("B")));
    Check(result.State==NavigationState.PAUSED && w.Executed.Count==1);
});
await Test("wrong page, result text and error prompt fail verification", async () => {
    foreach(var kind in new[]{"page","anchor","error"}) {
        var w=new World(); w.OnInput=a=>{if(kind=="page")w.Page="Contact";if(kind=="error")w.Error="failure";return Task.CompletedTask;};
        var step=Step() with {RequiredAfterText=kind=="anchor"?["missing"]:["page:Market"]};
        Check((await Run(w,Job(step))).State==NavigationState.PAUSED);
        Check(w.Executed.Count==1);
    }
});
await Test("critical action isolated before and after", async () => {
    var w=new World();var result=await Run(w,Job(Step() with {Critical=true},Step("B")));
    Check(result.State==NavigationState.SUCCESS && w.Captures==6);
    Check(w.Executed[0].Target.FrameId!=w.Executed[1].Target.FrameId);
});
await Test("max action count splits stable batches", async () => {
    var w=new World();var result=await Run(w,Job(Step(),Step("B")) with {MaxActions=1});
    Check(result.State==NavigationState.SUCCESS && w.Executed.Select(a=>a.Target.FrameId).Distinct().Count()==2);
});
await Test("cancel before run emits no input", async () => {
    var w=new World();using var c=new CancellationTokenSource();c.Cancel();
    Check((await Run(w,Job(Step()),ct:c.Token)).State==NavigationState.PAUSED && w.Executed.Count==0);
});
await Test("cancel between batch actions", async () => {
    var w=new World();using var c=new CancellationTokenSource();w.OnInput=a=>{c.Cancel();return Task.CompletedTask;};
    Check((await Run(w,Job(Step(),Step("B")),ct:c.Token)).State==NavigationState.PAUSED && w.Executed.Count==1);
});
await Test("pause and resume start with fresh evidence", async () => {
    var w=new World();using var control=new NavigationControl();control.Pause();
    Check((await Run(w,Job(Step()),control)).State==NavigationState.PAUSED && w.Executed.Count==0);
    control.Resume();Check((await Run(w,Job(Step()),control)).State==NavigationState.SUCCESS);
});
await Test("pause during batch stops subsequent actions", async () => {
    var w=new World();using var control=new NavigationControl();w.OnInput=a=>{control.Pause();return Task.CompletedTask;};
    Check((await Run(w,Job(Step(),Step("B")),control)).State==NavigationState.PAUSED && w.Executed.Count==1);
});
await Test("emergency stop is latched", async () => {
    var w=new World();using var control=new NavigationControl();w.OnInput=a=>{control.EmergencyStop();return Task.CompletedTask;};
    Check((await Run(w,Job(Step(),Step("B")),control)).State==NavigationState.PAUSED && w.Executed.Count==1);
    control.Resume();Check((await Run(w,Job(Step()),control)).State==NavigationState.PAUSED && w.Executed.Count==1);
});
await Test("step timeout cancels cooperative input without retry", async () => {
    var w=new World{InputDelay=TimeSpan.FromMilliseconds(200)};
    var result=await Run(w,Job(Step() with {Timeout=TimeSpan.FromMilliseconds(30)}));
    Check(result.State==NavigationState.PAUSED && w.Executed.Count==0 && w.Events.All(e=>e.State!=NavigationState.RETRY));
});
await Test("batch deadline interrupts delay", async () => {
    var w=new World{InputDelay=TimeSpan.FromMilliseconds(60)};var result=await Run(w,Job(Step(),Step("B")) with {MaxBatchTime=TimeSpan.FromMilliseconds(100),ActionInterval=TimeSpan.FromMilliseconds(60)});
    Check(result.State==NavigationState.PAUSED && w.Executed.Count<=1);
});
await Test("fresh retry after missing target succeeds", async () => {
    var w=new World{Names=["B"]};w.BeforeCapture=n=>{if(n==2)w.Names=["A","B"];};
    var result=await Run(w,Job(Step()));
    Check(result.State==NavigationState.SUCCESS && w.Events.Count(e=>e.State==NavigationState.RETRY)==1);
    Check(w.Executed[0].Target.FrameId.EndsWith("-2"));
});
await Test("retry exhaustion reports FAILED and reason", async () => {
    var w=new World{Names=["B"]};var result=await Run(w,Job(Step()));
    Check(result.State==NavigationState.FAILED && w.Executed.Count==0 && w.Captures==2 && result.Reason.Contains("absent"));
});
await Test("input exception never retries uncertain action", async () => {
    var w=new World();w.OnInput=_=>throw new IOException("injected input failure");
    var result=await Run(w,Job(Step(),Step("B")));
    Check(result.State==NavigationState.PAUSED && w.Executed.Count==1 && w.Events.All(e=>e.State!=NavigationState.RETRY));
});
await Test("recognize-only never emits or claims completion", async () => {
    var w=new World();var result=await Run(w,Job(Step()) with {Mode=ExecutionMode.RecognizeOnly});
    Check(result.State==NavigationState.SUCCESS && result.Completed==0 && w.Executed.Count==0);
});
await Test("real mode rejects simulated controller", async () => {
    var w=new World();var result=await Run(w,Job(Step()) with {Mode=ExecutionMode.Real});
    Check(result.State!=NavigationState.SUCCESS && w.Executed.Count==0);
});
await Test("transaction labels and primary buttons forbidden", async () => {
    foreach(var name in new[]{"购买","确认购买","兑换","上架","出售","confirm"}) {
        var w=new World{Names=[name]};Check((await Run(w,Job(Step(name)))).State==NavigationState.FAILED && w.Executed.Count==0);
    }
    var p=await new World().Observe();var target=new VisualTarget("A","primary_button",Fixtures.Box(0),new(300,250),1,p.Screen.Frame.Id,p.Screen.Frame.CapturedAt,p.Screen.ContentHash);
    Reject(()=>NavigationSafety.Check(Step() with {Target=Fixtures.Query("A") with {VisualClass="primary_button"}},target));
});
await Test("old/future frame cannot drive input", async () => {
    foreach(var delta in new[]{TimeSpan.FromSeconds(-20),TimeSpan.FromSeconds(20)}) {
        var w=new World{TimeOffset=delta};Check((await Run(w,Job(Step()))).State==NavigationState.PAUSED && w.Executed.Count==0);
    }
});
await Test("reused frame rejected", async () => {
    var w=new World{ReuseId=true};var r=await Run(w,Job(Step()) with {MaxRetries=0});
    Check(r.State==NavigationState.FAILED && w.Executed.Count==0);
});
await Test("screen origin changes stop before input", async () => {
    var w=new World();w.BeforeCapture=n=>{if(n==2)w.OriginX=20;};
    Check((await Run(w,Job(Step()))).State==NavigationState.PAUSED && w.Executed.Count==0);
});
await Test("simulated controller records actions without native input", async () => {
    var w=new World();var p=await w.Observe();var action=new Planner(new()).Locate(p,Step(),0);var mouse=new SimulatedInputController();
    await mouse.ExecuteAsync(action,p.Screen,CancellationToken.None);Check(mouse.Actions.Length==1 && mouse.IsSimulation);
});
await Test("two tasks serialize entire multi-action sessions", async () => {
    var trace=new List<string>();var w1=new World{InputDelay=TimeSpan.FromMilliseconds(10)};var w2=new World{InputDelay=TimeSpan.FromMilliseconds(10)};
    w1.OnInput=a=>{lock(trace)trace.Add("one");return Task.CompletedTask;};w2.OnInput=a=>{lock(trace)trace.Add("two");return Task.CompletedTask;};
    var runs=await Task.WhenAll(Run(w1,Job(Step(),Step("B"))),Run(w2,Job(Step(),Step("B"))));
    Check(runs.All(r=>r.State==NavigationState.SUCCESS));
    Check(trace.SequenceEqual(new[]{"one","one","two","two"}) || trace.SequenceEqual(new[]{"two","two","one","one"}));
});
await Test("cancel queued task does not affect current owner", async () => {
    var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var w1=new World();w1.OnInput=async _=>{entered.SetResult();await release.Task;};var w2=new World();
    var first=Run(w1,Job(Step()));await entered.Task;
    using var cancel=new CancellationTokenSource();var second=Run(w2,Job(Step()),ct:cancel.Token);cancel.Cancel();
    var r2=await second;release.SetResult();Check((await first).State==NavigationState.SUCCESS && r2.State==NavigationState.PAUSED && w2.Captures==0);
});
await Test("existing MarketPerception integration recognizes real schema", async () => {
    var p=await new World().Observe();var v=p.Vision with {Texts=[..p.Vision.Texts,new("购买",.99,new(.01,.01,.06,.03)),new("我的交易",.99,new(.1,.01,.08,.03))]};
    var classified=new MarketPageClassifier().Classify(p.Screen,v);Check(classified.Page=="Market" && classified.Errors.Length==0);
    v=v with {Regions=[..v.Regions,new("notification",.99,new(.2,.8,.3,.1))]};
    Check(new MarketPageClassifier().Classify(p.Screen,v).Errors.Length>0);
});
await Test("durable JSONL stores failure reason and frame", () => {
    var path=Path.Combine(Path.GetTempPath(),"navigation-"+Guid.NewGuid()+".jsonl");
    try {new JsonNavigationLog(path).Append(new(DateTimeOffset.UtcNow,"test",NavigationState.FAILED,"reason","frame"));
        var entry=JsonSerializer.Deserialize<NavigationEvent>(File.ReadAllText(path));Check(entry?.Reason=="reason" && entry.FrameId=="frame");}
    finally{File.Delete(path);}return Task.CompletedTask;
});
await Test("no-op same-page click cannot claim success", async () => {
    var w=new World();w.OnInput=_=>{w.Done.Clear();return Task.CompletedTask;};
    Check((await Run(w,Job(Step()))).State==NavigationState.PAUSED);
});
await Test("modal blocks background target and ambiguous secondary confirmation", async () => {
    var p=await new World().Observe();
    var modal=p with {Vision=p.Vision with {Regions=[..p.Vision.Regions,new("confirm_dialog",1,new(.3,.3,.3,.3))]}};
    Check(new VisualTargetResolver().Resolve(modal,Fixtures.Query("A")).Length==0);
    var t=new VisualTarget("OK","secondary_button",Fixtures.Box(0),new(300,250),1,p.Screen.Frame.Id,p.Screen.Frame.CapturedAt,p.Screen.ContentHash);
    Reject(()=>NavigationSafety.Check(Step("OK") with {Target=Fixtures.Query("OK") with {VisualClass="secondary_button"}},t));
});
await Test("scroll is supported only as a dynamic boundary", async () => {
    var w=new World();var scroll=Step() with {Operation=InputOperation.Scroll,ScrollDelta=-120,Effect=LayoutEffect.MayChange};
    var result=await Run(w,Job(scroll,Step("B")) with {BatchMode=BatchMode.DynamicPage});
    Check(result.State==NavigationState.SUCCESS && w.Executed[0].Target.FrameId!=w.Executed[1].Target.FrameId);
    var p=await new World().Observe();Reject(()=>new Planner(new()).Locate(p,scroll with {Effect=LayoutEffect.Stable},0));
});
await Test("move action still uses explicit resolved target", async () => {
    var w=new World();var result=await Run(w,Job(Step() with {Operation=InputOperation.Move,RequiredAfterText=[]}));
    Check(result.State==NavigationState.SUCCESS && w.Executed[0].Step.Operation==InputOperation.Move);
});
await Test("post-action future frame fails verification", async () => {
    var w=new World();w.BeforeCapture=n=>{if(n==3)w.TimeOffset=TimeSpan.FromMinutes(1);};
    Check((await Run(w,Job(Step()))).State==NavigationState.PAUSED && w.Executed.Count==1);
});
await Test("capture timeout stops before input", async () => {
    var w=new World{CaptureDelay=TimeSpan.FromMilliseconds(100)};
    Check((await Run(w,Job(Step() with {Timeout=TimeSpan.FromMilliseconds(10)}))).State==NavigationState.PAUSED && w.Executed.Count==0);
});
await Test("malformed plans cannot emit input", async () => {
    foreach(var job in new[]{Job(Step()) with {MaxActions=0},Job(Step()) with {MaxBatchTime=TimeSpan.Zero},Job(Step()) with {ActionInterval=TimeSpan.FromSeconds(-1)}}) {
        var w=new World();Check((await Run(w,job)).State!=NavigationState.SUCCESS && w.Executed.Count==0);
    }
});
await Test("input ownership exists only inside executor session", async () => {
    Check(!NavigationInputGate.IsOwnedByCurrentFlow);
    var w=new World();bool owned=false;w.OnInput=_=>{owned=NavigationInputGate.IsOwnedByCurrentFlow;return Task.CompletedTask;};
    Check((await Run(w,Job(Step()))).State==NavigationState.SUCCESS && owned);
    Check(!NavigationInputGate.IsOwnedByCurrentFlow);
});
await Test("market/contact recipes stop at browse pages", () => {
    var item=new CatalogItem("a","A",Confirmed:true);
    var task=NavigationRecipes.OpenMarketProduct(item,Fixtures.Query("A"));
    Check(task.Mode==ExecutionMode.RecognizeOnly && task.Steps.Single().ExpectedAfter=="Purchase");
    var contact=NavigationRecipes.BrowseContact("c",[Step(after:"Contact",stable:false)]);
    Check(contact.Steps.Last().ExpectedAfter=="Contact" && contact.BatchMode==BatchMode.DynamicPage);
    return Task.CompletedTask;
});
await Test("postcondition associates title with detail region, not background", async () => {
    var w=new World();var before=await w.Observe();var after=await w.Observe();
    var step=Step(after:"Purchase",stable:false) with {RequiredAfterText=["A"],ExpectedResultTarget=Fixtures.Query("A") with {VisualClass="detail_panel"}};
    after=after with {Page="Purchase",Vision=after.Vision with {Regions=[..after.Vision.Regions,new("detail_panel",1,new(.7,.2,.2,.3))]}};
    Check(!new NavigationVerifier(new()).After(before,after,step).Passed);
    after=after with {Vision=after.Vision with {Regions=[..after.Vision.Regions.Where(r=>r.Label!="detail_panel"),new("detail_panel",1,Fixtures.Box(0))]}};
    Check(new NavigationVerifier(new()).After(before,after,step).Passed);
});
await Test("stable batch splits before a dynamic action", async () => {
    var p=await new World().Observe();var planner=new BatchActionPlanner(new Planner(new()));
    var batch=planner.Plan(p,Job(Step(),Step("B",after:"Contact",stable:false)),0);
    Check(batch.Mode==BatchMode.StablePage && batch.Actions.Length==1);
    batch=planner.Plan(p,Job(Step(),Step("B",after:"Contact",stable:false)) with {BatchMode=BatchMode.DynamicPage},0);
    Check(batch.Mode==BatchMode.DynamicPage && batch.Actions.Length==2 && batch.Actions[^1].Step.Effect==LayoutEffect.MayChange);
});

await Test("detailed resolution reports AMBIGUOUS and UNKNOWN without guessing",async()=>{
 var w=new World{Names=["A","A"]};var p=await w.Observe();var resolver=new VisualTargetResolver();
 Check(resolver.ResolveDetailed(p,Fixtures.Query("A")).Status==LocationStatus.AMBIGUOUS);
 Check(resolver.ResolveDetailed(p with{Page="Unknown"},Fixtures.Query("A")).Status==LocationStatus.UNKNOWN);
 Check(resolver.ResolveDetailed(p,Fixtures.Query("missing")).Status==LocationStatus.NOT_FOUND);
});
await Test("OCR menu uses text center with auxiliary layout evidence",async()=>{
 var p=await new World().Observe();
 var text=new[]{new VisualToken("仓库",.99,new(.35,.965,.03,.02)),new("联络人",.99,new(.53,.965,.03,.02)),new("市场",.99,new(.63,.965,.03,.02)),new("进入暗区",.99,new(.02,.85,.12,.05)),new("每日任务",.99,new(.03,.25,.1,.03)),new("赛季手册",.99,new(.32,.02,.1,.03))};
 p=p with{Page="Unknown",Vision=p.Vision with{Regions=[],Texts=text}};
 var classified=NavigationSceneAdapter.Enrich(p);Check(classified.Page=="Home"&&classified.LayoutRegions!.Length==3);
 var market=new VisualTargetResolver().ResolveDetailed(classified,VisualNavigationRoutes.Menu("市场"));
 Check(market.Status==LocationStatus.RESOLVED&&market.Candidates[0].Sources!.Any(s=>s.StartsWith("LayoutRule:")));
 var click=market.Candidates[0].Click;Check(click==CoordinateTransform.ToScreen(text[2].Bounds,p.Screen));
 Check(click!=CoordinateTransform.ToScreen(new(.59375,.95,.09375,.05),p.Screen),"OCR primary uses the visible text center");
 var task=VisualNavigationRoutes.Build(classified,"Market",item:new("a","A",Confirmed:true));
 Check(task.Steps.Length==2&&task.Steps[0].ExpectedBefore=="Home"&&task.Steps[1].ExpectedBefore=="Market");
});
await Test("missing menu companion anchors and modal forbid derived background targets",async()=>{
 var p=await new World().Observe();var token=new VisualToken("市场",.99,new(.63,.96,.03,.02));
 p=p with{Page="Unknown",Vision=p.Vision with{Regions=[],Texts=[token]}};
 Check(NavigationSceneAdapter.Enrich(p).LayoutRegions!.Length==0);
 p=p with{Vision=p.Vision with{Regions=[new("confirm_dialog",.99,new(.1,.1,.8,.7))]}};
 Check(new VisualTargetResolver().ResolveDetailed(NavigationSceneAdapter.Enrich(p),VisualNavigationRoutes.Menu("市场")).Status!=LocationStatus.RESOLVED);
});
await Test("observer receives independent before/after frames and a plan",async()=>{
 var w=new World();Check((await Run(w,Job(Step()))).State==NavigationState.SUCCESS);
 Check(w.Observed.Count==3&&w.Planned==1&&w.VerifiedPairs.Count==1);
 Check(w.VerifiedPairs[0].Item1!=w.VerifiedPairs[0].Item2);
});
await Test("raw OCR detections remain untouched by scene layout adaptation",async()=>{
 var p=await new World().Observe();var raw=p.Vision;var adapted=NavigationSceneAdapter.Enrich(p);
 Check(ReferenceEquals(raw,adapted.Vision)&&raw.Regions.Length==adapted.Vision.Regions.Length);
});
await Test("unknown page cannot create route or execute simulated input",async()=>{
 var p=await new World().Observe();Reject(()=>VisualNavigationRoutes.Build(p with{Page="Unknown"},"Market"));
 var w=new World{Page="Unknown"};Check((await Run(w,Job(Step()))).Completed==0&&w.Executed.Count==0);
});
await Test("transaction text cannot enter a calibrated layout navigation queue",async()=>{
 var p=await new World().Observe();var token=new VisualTarget("确认","layout_menu",Fixtures.Box(0),new(300,250),1,p.Screen.Frame.Id,p.Screen.Frame.CapturedAt,p.Screen.ContentHash);
 Reject(()=>NavigationSafety.Check(Step() with{Target=Fixtures.Query("确认") with{VisualClass="layout_menu"}},token));
});
await Test("legacy vision payload remains compatible without performance metadata",async()=>{
 var p=await new World().Observe();var json=JsonSerializer.Serialize(p.Vision);var decoded=JsonSerializer.Deserialize<VisionResult>(json)!;
 Check(decoded.Performance is null&&decoded.FrameId==p.Screen.Frame.Id);
});
await Test("scroll verification rejects unrelated screenshot changes at viewport boundary",async()=>{
 var w=new World();var before=await w.Observe();var after=await w.Observe();
 var step=Step() with{Operation=InputOperation.Scroll,Effect=LayoutEffect.MayChange,RequiredAfterText=[],ScrollDelta=-120};
 Check(before.Screen.ContentHash!=after.Screen.ContentHash);
 Check(!new NavigationVerifier(new()).After(before,after,step).Passed);
 var key=VisualRegionKey.For("product_card",Fixtures.Box(0));
 before=before with{Vision=before.Vision with{RegionContent=new(){{key,"viewport-old"}}}};
 after=after with{Vision=after.Vision with{RegionContent=new(){{key,"viewport-new"}}}};
 Check(new NavigationVerifier(new()).After(before,after,step).Passed);
});
await Test("stepwise input uses newly resolved boundary frame with original bounds verified",async()=>{
 var w=new World();var result=await Run(w,Job(Step()) with{UseCurrentFrameForInput=true});
 Check(result.State==NavigationState.SUCCESS&&w.Executed.Count==1);
 Check(w.Executed[0].Target.FrameId==w.Observed[1].Screen.Frame.Id);
 Check(w.Executed[0].Target.FrameId!=w.Observed[0].Screen.Frame.Id&&w.Executed[0].Target.Bounds==Fixtures.Box(0));
});
await Test("fresh boundary mode still refuses moved targets before input",async()=>{
 var w=new World();w.BeforeCapture=n=>{if(n==2)w.Shift=.01;};
 var result=await Run(w,Job(Step()) with{UseCurrentFrameForInput=true});
 Check(result.State==NavigationState.PAUSED&&w.Executed.Count==0);
});
await Test("75% OCR locates with UI card; optional item evidence needs approval and canonical text",async()=>{
 var p=await new World().Observe();var box=new Region(.15,.25,.05,.1);var q=Fixtures.Query("A") with{ItemClass="grenade_mkii"};
 p=p with{Page="ContactShop",Vision=p.Vision with{Texts=p.Vision.Texts.Select(t=>t.Label=="A"?t with{Confidence=.75}:t).ToArray(),Items=[new("grenade_mkii",.99,box)],ItemModel=new("item-hash",[])}};
 var resolver=new VisualTargetResolver();var ocr=resolver.ResolveDetailed(p,q);
 Check(ocr.Status==LocationStatus.RESOLVED&&!ocr.Candidates[0].Sources!.Any(s=>s.StartsWith("Item-YOLO:")));
 p=p with{Vision=p.Vision with{ItemModel=new("item-hash",["grenade_mkii"])}};
 var r=resolver.ResolveDetailed(p,q);Check(r.Status==LocationStatus.RESOLVED&&r.Candidates[0].Click==new PixelPoint(300,250));
 Check(r.Candidates[0].Sources!.Any(s=>s.StartsWith("Item-YOLO:")));
 Check(resolver.ResolveDetailed(p with{Vision=p.Vision with{Texts=p.Vision.Texts.Select(t=>t.Label=="A"?t with{Confidence=.7499}:t).ToArray()}},q).Status==LocationStatus.NOT_FOUND);
 var wrong=p with{Vision=p.Vision with{Texts=p.Vision.Texts.Select(t=>t.Label=="A"?t with{Label="MKI"}:t).ToArray()}};
 Check(resolver.ResolveDetailed(wrong,q).Status==LocationStatus.NOT_FOUND);
 var conflict=p with{Vision=p.Vision with{Items=[new("grenade_mkii",.99,box),new("grenade_gho2",.9,box)]}};
 Check(resolver.ResolveDetailed(conflict,q).Status==LocationStatus.AMBIGUOUS);
 Reject(()=>VisualTargetResolver.Validate(p with{Vision=p.Vision with{Items=[new("grenade_mkii",double.NaN,box)]}}));
});
await Test("Dick refresh state is category-bound and does not invent remaining quota",async()=>{
 var p=await new World().Observe();
 p=p with{Page="ContactShop",Vision=p.Vision with{Texts=[new("59:59 后可刷新",.99,new(.39,.125,.065,.02))],PixelMeasurements=new(){{"DickTabOrange:限时物品",.4}}}};
 var state=DickScreenReader.Limited(p);Check(state.State==DickRefreshState.Cooling&&state.WaitSeconds==3599&&state.Remaining is null);
 Check(DickScreenReader.Limited(p with{Vision=p.Vision with{PixelMeasurements=new(){{"DickTabOrange:头盔",.4}}}}).State==DickRefreshState.Unknown);
 Check(DickScreenReader.Limited(p with{Vision=p.Vision with{Texts=[..p.Vision.Texts,new("已用尽",.99,new(.4,.125,.04,.02))]}}).State==DickRefreshState.Unknown);
 Check(DickScreenReader.Limited(p with{Vision=p.Vision with{Texts=[new("已用尽",.99,new(.4,.125,.04,.02))]}}).Remaining==0);
});
await Test("Dick initial goods acquisition is distinct from empty or failed loading",async()=>{
 var p=await new World().Observe();p=p with{Page="ContactShop",Vision=p.Vision with{Texts=[new("限时商品未刷新",.99,new(.26,.50,.07,.02)),new("获取限时商品",.99,new(.26,.54,.07,.02))],PixelMeasurements=new(){{"DickTabOrange:限时物品",.4}}}};
 Check(DickScreenReader.Limited(p).State==DickRefreshState.NotAcquired);
 Check(DickScreenReader.Limited(p with{Vision=p.Vision with{Texts=[]}}).State==DickRefreshState.Unknown);
});
await Test("Dick cooling affects refresh only and GHO output quantity belongs to its own card",async()=>{
 var p=await new World().Observe();var item=DickTargets.Items.Single(i=>i.Id=="gho2");
 p=p with{Page="ContactShop",Vision=p.Vision with{Regions=[new("product_card",.99,new(.05,.18,.1,.22))],Texts=[new(item.Name,.99,new(.055,.19,.09,.02)),new("x5",.99,new(.12,.35,.02,.02)),new("59:59 后可刷新",.99,new(.39,.125,.065,.02))],PixelMeasurements=new(){{"DickTabOrange:限时物品",.4}},Items=[new(item.ItemClass,.99,new(.07,.23,.05,.1))],ItemModel=new("test-only",[item.ItemClass])}};
 var card=DickScreenReader.Cards(p).Single();Check(card.OutputQuantity==5&&card.ItemModelMatched&&!card.SoldOut);
 Check(DickRoutes.Next(p,item,"限时物品")?.Id=="dick:detail:gho2");
 Check(DickRoutes.Next(p with{Vision=p.Vision with{Items=[]}},item,"限时物品") is null);
 Check(DickRoutes.Next(p with{Vision=p.Vision with{Texts=[..p.Vision.Texts,new("已售罄",.99,new(.055,.36,.05,.02))]}},item,"限时物品") is null);
 Check(DickRoutes.Next(p with{Vision=p.Vision with{Texts=[..p.Vision.Texts,new("00:12:00",.99,new(.055,.36,.065,.02))]}},item,"限时物品") is null);
});
await Test("Dick regular rules preserve separate cooldowns and four selected ammo types",()=>{
 Check(DickTargets.Items[0].CooldownSeconds==43200&&DickTargets.Items[1].CooldownSeconds is null);
 Check(DickTargets.Items.Count(i=>i.Category=="弹药")==4);
 Check(DickTargets.ItemClass("5.45×39毫米 BS子弹") is null);
 Check(!DickPageClassifier.Categories.Contains("手雷"));
 return Task.CompletedTask;
});
await Test("Dick queue cannot execute refresh or exchange submission",async()=>{
 var p=await new World().Observe();foreach(var name in new[]{"获取限时商品","刷新","补齐道具","兑换","确定"}) {
  Reject(()=>NavigationSafety.Check(new("dick:bad","ContactShop",new(name,"primary_button",DickRoutes.All,"迪克·文森",DickRoutes.All),InputOperation.Click,"ContactShop",TimeSpan.FromSeconds(10)),new(name,"primary_button",Fixtures.Box(0),new(1,1),1,p.Screen.Frame.Id,p.Screen.Frame.CapturedAt,p.Screen.ContentHash)));
 }
});

await Test("Randall GS2 uses five-hour cooldown instead of daily limit",()=>{
 var now=DateTimeOffset.UtcNow;var item=RandallTargets.Items.Single(i=>i.Id=="gs2");var available=new RandallAvailability(true,false,null,1,1);
 Check(item.CooldownSeconds==18000&&!item.Repeatable);
 Check(!RandallRoutes.Eligible(item,available,now,now.AddHours(-4.999)));
 Check(RandallRoutes.Eligible(item,available,now,now.AddHours(-5)));
 Check(!RandallRoutes.Eligible(item,available with{Countdown="00:00:01"},now,now.AddHours(-8)));
 return Task.CompletedTask;
});
await Test("Randall repeatable DK8 does not bypass visible unavailable states",()=>{
 var item=RandallTargets.Items[0];var now=DateTimeOffset.UtcNow;var a=new RandallAvailability(true,false,null,null,null);
 Check(item.Repeatable&&item.CooldownSeconds is null&&RandallRoutes.Eligible(item,a,now,now));
 Check(!RandallRoutes.Eligible(item,a with{SoldOut=true},now));
 Check(!RandallRoutes.Eligible(item,a with{Remaining=0},now));
 return Task.CompletedTask;
});
await Test("Randall locates contact by exact name and restricts to two requested items",async()=>{
 var p=(await new World().Observe()) with{Page="ContactList"};
 Check(RandallTargets.Items.Length==2);
 Check(RandallRoutes.Next(p,RandallTargets.Items[0])!.Target.Name=="兰德尔·费舍");
 try{RandallRoutes.Next(p,new("dk7","DK7高级防弹面罩","头套面罩",true,null));throw new Exception("unexpected item accepted");}catch(ArgumentException){}
});
await Test("Randall category changes require fresh orange evidence from correct contact",async()=>{
 var w=new World();var before=(await w.Observe()) with{Page="ContactShop"};var after=(await w.Observe()) with{Page="ContactShop"};
 before=before with{Vision=before.Vision with{PixelMeasurements=new(){{"RandallTabOrange:头套面罩",.4}}}};
 after=after with{Vision=after.Vision with{PixelMeasurements=new(){{"RandallTabOrange:耳机",.4}}}};
 var step=Step() with{Id="randall:category:耳机",ExpectedBefore="ContactShop",ExpectedAfter="ContactShop",Effect=LayoutEffect.MayChange,RequiredAfterText=[],RequiredActiveCategory="耳机"};
 Check(new NavigationVerifier(new()).After(before,after,step).Passed);
 after=after with{Vision=after.Vision with{PixelMeasurements=new(){{"DickTabOrange:耳机",.4}}}};
 Check(!new NavigationVerifier(new()).After(before,after,step).Passed);
});
await Test("Randall exchange submission stays outside browse queue",async()=>{
 var p=await new World().Observe();
 foreach(var name in new[]{"兑换","补齐道具","确定"})Reject(()=>NavigationSafety.Check(new("randall:bad","Exchange",new(name,"primary_button",RandallRoutes.All,"道具兑换",RandallRoutes.All),InputOperation.Click,"ContactShop",TimeSpan.FromSeconds(10)),new(name,"primary_button",Fixtures.Box(0),new(1,1),1,p.Screen.Frame.Id,p.Screen.Frame.CapturedAt,p.Screen.ContentHash)));
});

await Test("material route restricts three materials and maps their exchange products",()=>{
 Check(MaterialMarketTargets.Items.Length==3);
 Check(MaterialMarketTargets.Items.Single(i=>i.Id=="radio").Name=="无线对讲机");
 Check(MaterialMarketTargets.Items.Single(i=>i.Id=="radio").ExchangeProduct=="100D战地医疗包");
 Check(MaterialMarketTargets.Items.Single(i=>i.Id=="chip").ExchangeProduct=="DK8军用防弹面罩");
 Check(MaterialMarketTargets.Items.Single(i=>i.Id=="rope").ExchangeProduct=="SH12军用头盔");
 return Task.CompletedTask;
});
await Test("material market category requires its own selected leaf evidence",async()=>{
 var p=await new World().Observe();var v=p.Vision with{PixelMeasurements=new(){{"MaterialCategoryOrange:电脑配件",.3}}};
 Check(MaterialMarketClassifier.ActiveCategory(v)=="电脑配件");
 Check(MaterialMarketClassifier.ActiveCategory(v with{PixelMeasurements=new(){{"MaterialCategoryOrange:电脑配件",.3},{"MaterialCategoryOrange:军用杂物",.3}}}) is null);
});
await Test("quantity preparation verifies one increment in a newer frame",async()=>{
 PageObservation Q(PageObservation p,int number)=>p with{Page="Purchase",Vision=p.Vision with{
  Regions=[new("trade_panel",.99,new(.5,.2,.4,.6)),new("quantity_field",.7,new(.6,.62,.14,.04))],
  Texts=[new("购买数量",.99,new(.52,.625,.07,.02)),new(number.ToString(),.99,new(.66,.625,.02,.02))]},
  LayoutRegions=[new(new("materials_quantity_plus",.95,new(.74,.62,.024,.04)),"LayoutRule:materials:quantity-right-square; NOT YOLO",["购买数量"])]};
 var w=new World();var before=Q(await w.Observe(),1);var after=Q(await w.Observe(),2);
 var step=new NavigationStep("materials:quantity:chip","Purchase",new("增加数量","materials_quantity_plus",new(0,0,1,1),"购买数量",new(0,0,1,1)),InputOperation.Click,"Purchase",TimeSpan.FromSeconds(10),RequiredPurchaseQuantity:2);
 var plan=new Planner(new()).Locate(before,step,0);
 Check(new NavigationVerifier(new()).After(before,after,step).Passed);
 Check(!new NavigationVerifier(new()).After(before,Q(after,1),step).Passed);
 Check(!new NavigationVerifier(new()).After(before,Q(after,3),step).Passed);
 Reject(()=>new Planner(new()).Locate(before,step with{RequiredPurchaseQuantity=5},0));
 Check(!new NavigationVerifier(new()).Before(before,after,plan,DateTimeOffset.UtcNow,TimeSpan.FromMinutes(1)).Passed);
 for(int current=1;current<5;current++) {
  var a=Q(await w.Observe(),current);var b=Q(await w.Observe(),current+1);var increment=step with{RequiredPurchaseQuantity=current+1};
  new Planner(new()).Locate(a,increment,0);Check(new NavigationVerifier(new()).After(a,b,increment).Passed);
 }

});
await Test("quantity evidence is not inferred from unit and total prices",async()=>{
 var p=await new World().Observe();var v=p.Vision with{Regions=[new("trade_panel",.99,new(.5,.2,.4,.6)),new("quantity_field",.7,new(.6,.62,.14,.04))],Texts=[new("购买数量",.99,new(.52,.625,.07,.02)),new("平均单价",.99,new(.52,.7,.07,.02)),new("100",.99,new(.75,.7,.05,.02)),new("合计",.99,new(.52,.75,.07,.02)),new("500",.99,new(.75,.75,.05,.02))]};
 Check(MaterialMarketQuantity.Read(v) is null);
});
await Test("material quantity navigation cannot submit purchases",async()=>{
 var p=await new World().Observe();
 Reject(()=>NavigationSafety.Check(new("materials:buy","Purchase",new("购买","primary_button",new(0,0,1,1),"购买数量",new(0,0,1,1)),InputOperation.Click,"MaterialMarket",TimeSpan.FromSeconds(10)),new("购买","primary_button",Fixtures.Box(0),new(1,1),1,p.Screen.Frame.Id,p.Screen.Frame.CapturedAt,p.Screen.ContentHash)));
});

await Test("OCR primary locates text without YOLO card and clicks text center",async()=>{
 var p=await new World().Observe();p=p with{Vision=p.Vision with{Regions=[]}};
 var q=Fixtures.Query("A") with{PreferOcr=true};var t=new VisualTargetResolver().ResolveDetailed(p,q);
 Check(t.Status==LocationStatus.RESOLVED&&t.Candidates[0].Bounds==p.Vision.Texts.Single(t=>t.Label=="A").Bounds);
 Check(t.Candidates[0].Click==CoordinateTransform.ToScreen(t.Candidates[0].Bounds,p.Screen));
 Check(new VisualTargetResolver().Resolve(p,q with{PreferOcr=false}).Length==0);
});
await Test("OCR primary duplicate text remains ambiguous",async()=>{
 var p=await new World().Observe();p=p with{Vision=p.Vision with{Regions=[],Texts=p.Vision.Texts.Append(new("A",.99,new(.6,.4,.1,.04))).ToArray()}};
 Check(new VisualTargetResolver().ResolveDetailed(p,Fixtures.Query("A") with{PreferOcr=true}).Status==LocationStatus.AMBIGUOUS);
});
await Test("OCR primary ignores detector jitter but rejects moved text",async()=>{
 var w=new World();var a=await w.Observe();var b=await w.Observe();var step=Step() with{Target=Fixtures.Query("A") with{PreferOcr=true}};
 var plan=new Planner(new()).Locate(a,step,0);b=b with{Layout="detector jitter",Vision=b.Vision with{Regions=[]}};
 Check(new NavigationVerifier(new()).Before(a,b,plan,DateTimeOffset.UtcNow,TimeSpan.FromMinutes(1)).Passed);
 b=b with{Vision=b.Vision with{Texts=b.Vision.Texts.Select(t=>t.Label=="A"?t with{Bounds=new(.3,.3,.1,.04)}:t).ToArray()}};
 Check(!new NavigationVerifier(new()).Before(a,b,plan,DateTimeOffset.UtcNow,TimeSpan.FromMinutes(1)).Passed);
});
await Test("click context needs new positive OCR page evidence",async()=>{
 var w=new World();var before=await w.Observe();var after=(await w.Observe()) with{Page="Unknown"};
 var step=Step(after:"Purchase",stable:false) with{Target=Fixtures.Query("A") with{PreferOcr=true}};
 var context=new OcrNavigationContext();context.Issued(new Planner(new()).Locate(before,step,0),before.Screen);
 Check(context.Reconcile(after).Page=="Unknown");
 after=after with{Vision=after.Vision with{Regions=[],Texts=[new("购买数量",.99,new(.5,.5,.1,.03)),new("平均单价",.99,new(.5,.6,.1,.03)),new("合计",.99,new(.5,.7,.1,.03))]}};
 Check(context.Reconcile(after).Page=="Purchase");
 Check(context.Reconcile(after with{Errors=["错误"]}).Page=="Unknown");
 Check(context.Reconcile(after with{Screen=after.Screen with{WindowHandle=2}}).Page=="Unknown");
 Check(context.Reconcile(after with{Screen=after.Screen with{Frame=after.Screen.Frame with{CapturedAt=before.Screen.Frame.CapturedAt.AddMinutes(1)}}}).Page=="Unknown");
 Check(context.Reconcile(after with{Vision=after.Vision with{Texts=after.Vision.Texts.Append(new("已集齐全部兑换道具",.99,new(.4,.4,.2,.04))).ToArray()}}).Page=="Unknown");
});
await Test("executor recovers missed page using issued click and fresh OCR",async()=>{
 var w=new World();w.OnInput=a=>{w.Page="Unknown";return Task.CompletedTask;};
 w.TransformVision=v=>w.Page!="Unknown"?v:v with{Regions=[],Texts=[new("page:Unknown",1,new(0,0,.1,.03)),new("购买数量",1,new(.5,.5,.1,.03)),new("平均单价",1,new(.5,.6,.1,.03)),new("合计",1,new(.5,.7,.1,.03))]};
 var step=Step(after:"Purchase",stable:false,batch:false) with{Target=Fixtures.Query("A") with{PreferOcr=true},RequiredAfterText=["购买数量"]};
 var result=await Run(w,Job(step));
 Check(result.State==NavigationState.SUCCESS&&result.Completed==1&&w.Executed.Count==1);
 Check(w.Observed.Last().Sources!.Any(s=>s.StartsWith("ClickContext:")));
});
await Test("OCR primary cannot bypass transaction queue or error page",async()=>{
 var p=await new World().Observe();var q=Fixtures.Query("A") with{PreferOcr=true};
 Check(new VisualTargetResolver().Resolve(p with{Errors=["异常"]},q).Length==0);
 var t=new VisualTargetResolver().Resolve(p,q).Single();
 Reject(()=>NavigationSafety.Check(Step() with{Target=q with{Name="确定"}},t with{Name="确定"}));
});

var report = args.Length>0?args[0]:"navigation-results.json";
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(report))!);
File.WriteAllText(report,JsonSerializer.Serialize(new { total=results.Count,failed=failures,platform=Environment.OSVersion.ToString(),nativeInput=false,tests=results },new JsonSerializerOptions{WriteIndented=true}));
Console.WriteLine($"{results.Count-failures}/{results.Count} passed");return failures==0?0:1;

static class Fixtures {
    public static Region Box(int i,double shift=0)=>new(.1+i*.4+shift,.2,.2,.3);
    public static TargetQuery Query(string name)=>new(name,"product_card",new(0,0,1,1),"导航",new(0,0,1,.15),PreferOcr:false);
}
sealed class Classifier : IPageClassifier {
    public PageObservation Classify(CapturedScreen s,VisionResult v)=>new(s,v,v.Texts[0].Label[5..],
        string.Join("|",v.Regions.Select(r=>r.Bounds.ToString())),v.Texts.Where(t=>t.Label.StartsWith("error:")).Select(t=>t.Label).ToArray());
}
sealed class World : IScreenSource, IVisionPipeline, IInputController, INavigationLog,INavigationObserver {
    public List<PageObservation> Observed=[];public int Planned;public List<(string,string)> VerifiedPairs=[];
    public void Observation(PageObservation page)=>Observed.Add(page);
    public void Plan(ActionBatch batch)=>Planned++;
    public void Verified(PageObservation before,PageObservation after,NavigationStep step,Verification result)=>VerifiedPairs.Add((before.Screen.Frame.Id,after.Screen.Frame.Id));
    private readonly string prefix=Guid.NewGuid().ToString("N");
    private readonly Dictionary<string,VisionResult> frames=[];
    public int Captures;public string Page="Market";public string[] Names=["A","B"];public double Shift;public string? Error;
    public int OriginX=100;public TimeSpan TimeOffset;public bool ReuseId;public TimeSpan InputDelay;public TimeSpan CaptureDelay;
    public Action<int>? BeforeCapture;public Func<PlannedAction,Task>? OnInput;public Func<VisionResult,VisionResult>? TransformVision;
    public List<string> Done=[];public List<PlannedAction> Executed=[];public List<NavigationEvent> Events=[];
    public bool IsSimulation=>true;
    public async Task<CapturedScreen> CaptureAsync(CancellationToken token) {
        token.ThrowIfCancellationRequested();if(CaptureDelay>TimeSpan.Zero)await Task.Delay(CaptureDelay,token);await Task.Yield();Captures++;BeforeCapture?.Invoke(Captures);
        var id=prefix+"-"+(ReuseId?1:Captures);var at=DateTimeOffset.UtcNow+TimeOffset;
        var s=new CapturedScreen(new(id,at,"fixture.png"),"hash-"+id,1000,600,1,OriginX,40);
        var words=new List<VisualToken>{new("page:"+Page,1,new(0,0,.1,.03)),new("导航",1,new(.2,0,.1,.03))};
        for(int i=0;i<Names.Length;i++)words.Add(new(Names[i],.99,new(.12+i*.4+Shift,.22,.1,.04)));
        foreach(var done in Done)words.Add(new(done,1,new(.7,.8,.2,.03)));
        if(Error!=null)words.Add(new("error:"+Error,1,new(.3,.8,.2,.1)));
        frames[id]=new(id,at,s.ContentHash,1000,600,Names.Select((_,i)=>new VisualToken("product_card",.99,Fixtures.Box(i,Shift))).ToArray(),words.ToArray(),0,0,0);
        if(TransformVision is not null)frames[id]=TransformVision(frames[id]);
        return s;
    }
    public Task<VisionResult> AnalyzeAsync(CapturedScreen s,CancellationToken token){token.ThrowIfCancellationRequested();return Task.FromResult(frames[s.Frame.Id]);}
    public async Task<PageObservation> Observe(){var s=await CaptureAsync(default);return new Classifier().Classify(s,await AnalyzeAsync(s,default));}
    public async Task ExecuteAsync(PlannedAction action,CapturedScreen screen,CancellationToken token){
        if(InputDelay>TimeSpan.Zero)await Task.Delay(InputDelay,token);token.ThrowIfCancellationRequested();Executed.Add(action);Done.Add("done:"+action.Step.Id);if(OnInput!=null)await OnInput(action);
    }
    public void Append(NavigationEvent entry)=>Events.Add(entry);
}
