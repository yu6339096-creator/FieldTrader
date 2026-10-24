using System.IO;
using System.Text.Json;

namespace FieldTrader.Market;
public enum NavigationKind { Category, Product, NextPage, MarketEntry, CloseDialog, Search, SimulatedTrade }
public sealed record NavigationAction(NavigationKind Kind, string Text, Region Bounds, string FrameId,
    string ContentHash, MarketPage Before, MarketPage ExpectedAfter, string? SearchText = null);
public interface IMouseExecutor {
    bool IsSimulation { get; }
    Task ExecuteAsync(NavigationAction action, CapturedScreen screen, CancellationToken token);
}
public sealed class SimulatedMouseExecutor : IMouseExecutor {
    public bool IsSimulation => true;
    public List<NavigationAction> Actions { get; } = [];
    public Task ExecuteAsync(NavigationAction action, CapturedScreen screen, CancellationToken token) {
        token.ThrowIfCancellationRequested(); Actions.Add(action); return Task.CompletedTask;
    }
}
public static class NavigationPlanner {
    public static NavigationAction? Plan(PerceptionResult result, CatalogItem target, ISet<string>? attempted = null) {
        if(result.Paused)return null;
        var v=result.Vision;
        NavigationAction Make(NavigationKind k,VisualToken t,MarketPage after)=>new(k,t.Label,t.Bounds,v.FrameId,v.ContentHash,result.Page,after);
        VisualToken[] Exact(string value)=>v.Texts.Where(t=>t.Confidence>=.95 && Geometry.Normalize(t.Label)==Geometry.Normalize(value)).ToArray();
        if(result.Page is MarketPage.Purchase or MarketPage.Sell) {
            var close=Exact("关闭").Where(t=>v.Regions.Any(r=>r.Label=="secondary_button"&&r.Confidence>=.8&&Geometry.Contains(r.Bounds,t.Bounds))).ToArray();
            return close.Length==1?Make(NavigationKind.CloseDialog,close[0],result.Page==MarketPage.Sell?MarketPage.Listings:MarketPage.Market):null;
        }
        if(result.Page!=MarketPage.Market)return null;
        var products=Exact(target.Name).Where(t=>v.Regions.Any(r=>r.Label=="product_card"&&r.Confidence>=.8&&Geometry.Contains(r.Bounds,t.Bounds))).ToArray();
        if(products.Length==1)return Make(NavigationKind.Product,products[0],MarketPage.Purchase);
        var search=Exact("输入物品名称搜索").Where(t=>t.Bounds.X<.2&&t.Bounds.Y<.2).ToArray();
        if(search.Length==1 && attempted?.Contains("search")!=true)return Make(NavigationKind.Search,search[0],MarketPage.Market) with{SearchText=target.Name};
        var category=Exact(target.Category).Where(t=>t.Bounds.X<.2).ToArray();
        if(category.Length==1 && attempted?.Contains("category")!=true)return Make(NavigationKind.Category,category[0],MarketPage.Market);
        var next=Exact("下一页");
        return next.Length==1?Make(NavigationKind.NextPage,next[0],MarketPage.Market):null;
    }
}
public sealed record CollectionUpdate(PerceptionResult Perception,int Inserted,Coverage Coverage,ScheduledTarget? Next,string ResultPath,string? ScreenshotPath=null,TradeScreenFacts? TradeFacts=null,TradeDecision[]? Decisions=null);

// Single owner for capture, recognition, collection and navigation. Audio has no dependency on this service.
public sealed class MarketWorkflow(IVisionPipeline vision,MarketDatabase database,string evidenceDirectory,TradingAnalysis? analysis=null) {
    private readonly SemaphoreSlim gate=MarketOperationGate.Shared;
    private readonly CancellationTokenSource stop=new();
    private int paused;
    public bool Paused=>Volatile.Read(ref paused)!=0;
    public void Stop(){Interlocked.Exchange(ref paused,1);stop.Cancel();}
    public async Task<CollectionUpdate> CollectAsync(IScreenSource capture,CancellationToken token) {
        using var linked=CancellationTokenSource.CreateLinkedTokenSource(token,stop.Token);
        await gate.WaitAsync(linked.Token);
        try{return (await Observe(capture,linked.Token)).Update;}
        catch(OperationCanceledException){throw;}
        catch(Exception ex){Interlocked.Exchange(ref paused,1);database.Log(null,null,ex.Message);throw;}
        finally{gate.Release();}
    }
    private async Task<(CapturedScreen Screen,CollectionUpdate Update)> Observe(IScreenSource capture,CancellationToken token) {
        if(Paused)throw new InvalidOperationException("市场采集已暂停；请检查日志后创建新会话");
        var screen=await capture.CaptureAsync(token);
        var raw=await vision.AnalyzeAsync(screen,token);
        if(raw.FrameId!=screen.Frame.Id||raw.ContentHash!=screen.ContentHash||raw.CapturedAt!=screen.Frame.CapturedAt)
            throw new InvalidDataException("推理结果不属于当前截图");
        var catalog=database.Catalog();var fused=MarketPerception.Fuse(raw,catalog);
        if(fused.Paused){Interlocked.Exchange(ref paused,1);database.Log(raw.FrameId,null,fused.Reason);}
        var count=fused.Paused?0:database.Store(fused,!screen.Live);
        var facts=TradeScreenReader.Read(fused,catalog);
        var decisions=fused.Paused?[]:analysis?.Observe(fused,!screen.Live,DateTimeOffset.UtcNow)??[];
        var boost=decisions.Where(d=>d.Kind==DecisionKind.BUY).SelectMany(d=>d.Purchases.Select(p=>(p.ItemId,Priority:Math.Clamp((double)d.Roi,0,10)))).GroupBy(x=>x.ItemId).ToDictionary(g=>g.Key,g=>g.Max(x=>x.Priority));
        Directory.CreateDirectory(evidenceDirectory);
        var path=Path.GetFullPath(Path.Combine(evidenceDirectory,screen.Frame.Id+".json"));
        await File.WriteAllTextAsync(path,JsonSerializer.Serialize(new {Screen=screen,Result=fused,TradeFacts=facts,Decisions=decisions,Reference=analysis?.Reference},new JsonSerializerOptions{WriteIndented=true}),token);
        return (screen,new(fused,count,database.Coverage(DateTimeOffset.UtcNow.AddHours(-24)),new MarketScheduler(database).Next(DateTimeOffset.UtcNow,boost),path,screen.Frame.ScreenshotPath,facts,decisions));
    }
    public async Task<string> NavigateAsync(CatalogItem target,IScreenSource capture,IMouseExecutor mouse,CancellationToken token) {
        using var linked=CancellationTokenSource.CreateLinkedTokenSource(token,stop.Token);
        await MarketOperationGate.TradeSession.WaitAsync(linked.Token);
        bool locked=false;
        try {
            await gate.WaitAsync(linked.Token);locked=true;
            var current=await Observe(capture,linked.Token);
            var seen=new HashSet<string>();var attempted=new HashSet<string>();
            for(int step=0;step<5;step++) {
                if(current.Update.Perception.Paused)throw new InvalidOperationException(current.Update.Perception.Reason);
                if(current.Update.Perception.Page==MarketPage.Purchase&&current.Update.Perception.Quotes.Any(q=>Geometry.Normalize(q.ItemName)==Geometry.Normalize(target.Name)))return "已验证目标商品页面并采集";
                var plan=NavigationPlanner.Plan(current.Update.Perception,target,attempted);
                if(plan is null) {database.FailedAttempt(target.Id,DateTimeOffset.UtcNow,"OCR导航锚点不足，需要人工导航");return "未找到唯一导航目标；需要人工导航";}
                var key=$"{plan.Kind}:{plan.Text}:{current.Screen.ContentHash}";
                if(!seen.Add(key)){database.FailedAttempt(target.Id,DateTimeOffset.UtcNow,"页面未变化，停止导航重试");return "页面未变化，停止导航";}
                await mouse.ExecuteAsync(plan,current.Screen,linked.Token);
                if(plan.Kind==NavigationKind.Search)attempted.Add("search");
                if(plan.Kind==NavigationKind.Category)attempted.Add("category");
                database.Log(current.Screen.Frame.Id,target.Id,$"{(mouse.IsSimulation?"模拟导航":"导航")}: {plan.Kind} {plan.Text}");
                if(mouse.IsSimulation)return $"模拟导航：{plan.Kind} → {plan.Text}，未发送输入";
                await Task.Delay(500,linked.Token);
                var next=await Observe(capture,linked.Token);
                if(next.Update.Perception.Page!=plan.ExpectedAfter || next.Screen.ContentHash==current.Screen.ContentHash)
                    throw new InvalidOperationException("UNKNOWN：导航后页面未验证，暂停操作");
                current=next;
            }
            database.FailedAttempt(target.Id,DateTimeOffset.UtcNow,"达到单次导航步数上限");return "达到导航步数上限";
        }catch(OperationCanceledException){Interlocked.Exchange(ref paused,1);database.Log(null,target.Id,"导航取消；恢复前需要重新检查当前页面");throw;}
        catch(Exception ex){Interlocked.Exchange(ref paused,1);database.Log(null,target.Id,ex.Message);throw;}
        finally{if(locked)gate.Release();MarketOperationGate.TradeSession.Release();}
    }
    public async Task RunCollectionAsync(IScreenSource capture,Action<CollectionUpdate> report,CancellationToken token) {
        // Capture only when the prior frame finishes. There is no unbounded screenshot queue.
        using var linked=CancellationTokenSource.CreateLinkedTokenSource(token,stop.Token);
        while(!linked.IsCancellationRequested&&!Paused){report(await CollectAsync(capture,linked.Token));await Task.Delay(2000,linked.Token);}
    }
    public async Task RunPatrolAsync(IScreenSource capture,IMouseExecutor mouse,Action<string> report,CancellationToken token) {
        using var linked=CancellationTokenSource.CreateLinkedTokenSource(token,stop.Token);
        var scheduler=new MarketScheduler(database);
        while(!linked.IsCancellationRequested&&!Paused) {
            var target=scheduler.Next(DateTimeOffset.UtcNow);
            if(target is null){report("巡查队列暂无到期商品；等待目录确认或退避结束");return;}
            report(await NavigateAsync(target.Item,capture,mouse,linked.Token));
            if(mouse.IsSimulation)return; // A dry run must not advance coverage or spin on unchanged screens.
            await Task.Delay(2000,linked.Token);
        }
    }
}
