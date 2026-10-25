using System.IO;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using FieldTrader.Navigation;
namespace FieldTrader.Market;

public sealed record LarryLimit(string Type,int? Remaining,int? Maximum,string? Countdown,string SourceFrame);
public sealed record LarryMaterial(string Name,int? Required,int? Owned,decimal? UnitPrice,bool? NeedsTopUp,int VisibleRows,bool? IsOwned);
public sealed record LarryRawRow(string Name,string? Category,decimal? Price,bool Owned,Region Bounds,double Confidence,string FrameId);
public sealed record LarryDetail(string Contact,string ItemId,string ItemName,string Category,LarryLimit Limit,
    LarryMaterial[] Materials,LarryRawRow[] RawRows,decimal? MaterialTotal,decimal? TopUpPrice,decimal? ProductMarketPrice,
    decimal? ReferenceGrossProfit,decimal? EstimatedNetProfit,string NetAvailability,string RecipeStatus,string Status,
    DateTimeOffset At,string FrameId,string ContentHash,string Screenshot,double OcrConfidence,double YoloConfidence,
    string[] Issues,PageObservation Evidence,string[]? EvidenceScreens=null,bool MaterialsComplete=false);

public static class LarryDetailReader {
    private static string N(string s)=>Geometry.Normalize(s);
    public static LarryLimit Limit(PageObservation page,LarryTarget item) {
        var cards=page.Vision.Regions.Where(r=>r.Label=="product_card"&&r.Confidence>=.8&&Geometry.Contains(LarryRoutes.Goods,r.Bounds)
            &&page.Vision.Texts.Any(t=>t.Confidence >= OcrPolicy.MinimumConfidence&&N(t.Label)==N(item.Name)&&Geometry.Contains(r.Bounds,t.Bounds))).ToArray();
        var texts=cards.Length==1?LarryPageClassifier.Unique(page.Vision.Texts.Where(t=>t.Confidence >= OcrPolicy.MinimumConfidence&&Geometry.Contains(cards[0].Bounds,t.Bounds))):[];
        var counts=texts.Select(t=>Regex.Match(N(t.Label),@"^限购[:：]?(\d+)/(\d+)$")).Where(m=>m.Success).ToArray();
        var clocks=texts.Select(t=>Regex.Match(N(t.Label),@"(?:^|[^\d])(\d{2}:\d{2}:\d{2})$")).Where(m=>m.Success).ToArray();
        return new(item.Limit,counts.Length==1?int.Parse(counts[0].Groups[1].Value):null,counts.Length==1?int.Parse(counts[0].Groups[2].Value):null,
            clocks.Length==1?clocks[0].Groups[1].Value:null,page.Screen.Frame.Id);
    }
    private static decimal? Amount(string s) {
        s=N(s).TrimStart('-','−');
        return Regex.IsMatch(s,@"^(?:\d+|\d{1,3}(?:,\d{3})+)$")&&decimal.TryParse(s,NumberStyles.AllowThousands,CultureInfo.InvariantCulture,out var n)?n:null;
    }
    public static decimal? Money(IEnumerable<VisualToken> texts,string label) {
        var all=texts.Where(t=>t.Confidence >= OcrPolicy.MinimumConfidence).ToArray();var values=new List<decimal>();
        foreach(var t in all.Where(t=>N(t.Label).StartsWith(label))) {
            // The information icon is a glyph, not the NFKC numeric value of circled one.
            var inline=Regex.Match(N(t.Label.Replace("①","")),"^"+label+@"[:：](.+)$");
            if(inline.Success&&Amount(inline.Groups[1].Value) is {} value)values.Add(value);
            else {
                var right=all.Where(r=>r.Bounds.X>t.Bounds.X+t.Bounds.Width&&Math.Abs(Geometry.CenterY(r.Bounds)-Geometry.CenterY(t.Bounds))<Math.Max(t.Bounds.Height,r.Bounds.Height)*.7).OrderBy(r=>r.Bounds.X).ToArray();
                var column=right.TakeWhile(r=>!Regex.IsMatch(N(r.Label),@"^[\u4e00-\u9fff]")).ToArray();
                var numeric=column.Where(r=>Regex.IsMatch(N(r.Label),@"\d")).ToArray();
                if(numeric.Length>0&&Amount(numeric[^1].Label) is {} number)values.Add(number);
            }
        }
        var unique=values.Distinct().ToArray();return unique.Length==1?unique[0]:null;
    }
    public static LarryDetail Read(PageObservation p,LarryTarget target,LarryLimit? limit=null) {
        VisualTargetResolver.Validate(p);
        if(p.Page!="Exchange"||p.Errors.Length>0||new VisualTargetResolver().Resolve(p,new(target.Name,"larry_detail",LarryRoutes.All,"道具兑换",LarryRoutes.All),true).Length!=1)
            throw new NavigationEvidenceException("Exchange identity unverified; no collection or cancel guessing");
        var words=LarryPageClassifier.Unique(p.Vision.Texts.Where(t=>t.Confidence >= OcrPolicy.MinimumConfidence));
        var issues=new List<string>();var needs=new List<MaterialNeed>();
        var headers=words.Where(t=>N(t.Label)=="需要提交以下道具").ToArray();var lists=words.Where(t=>N(t.Label)=="兑换列表").ToArray();
        if(headers.Length==1&&lists.Length==1) {
            var band=words.Where(t=>t.Bounds.X>=headers[0].Bounds.X-.006&&t.Bounds.Y>headers[0].Bounds.Y+headers[0].Bounds.Height&&t.Bounds.Y<lists[0].Bounds.Y-.008).ToArray();
            var counts=band.Where(t=>Regex.IsMatch(N(t.Label),@"^\d{1,5}/\d{1,5}$")).ToArray();
            var names=band.Where(t=>Regex.IsMatch(N(t.Label),@"^[\u4e00-\u9fff]+$")&&t.Bounds.Y<lists[0].Bounds.Y-.025).ToArray();
            foreach(var name in names) {
                var nearby=counts.Where(c=>c.Bounds.X>=name.Bounds.X-.01&&c.Bounds.X<name.Bounds.X+.055&&c.Bounds.Y>name.Bounds.Y).ToArray();
                var tuples=nearby.Select(c=>N(c.Label).Split('/')).Select(v=>(owned:int.Parse(v[0]),required:int.Parse(v[1]))).Distinct().ToArray();
                needs.Add(new(LarryTargets.MaterialName(name.Label),tuples.Length==1?tuples[0].owned:null,tuples.Length==1?tuples[0].required:null,$"{name.Bounds.X:F4}:{name.Bounds.Y:F4}"));
            }
        }
        var rows=new List<LarryRawRow>();
        var view=p.LayoutRegions?.FirstOrDefault(r=>r.Token.Label=="larry_material_viewport")?.Token.Bounds;
        if(view is not null)foreach(var row in p.Vision.Regions.Where(r=>r.Label=="material_row"&&r.Confidence>=.6&&Geometry.Contains(view,r.Bounds))) {
            var tokens=words.Where(t=>Geometry.Contains(row.Bounds,t.Bounds)).ToArray();
            var names=tokens.Where(t=>t.Bounds.X<row.Bounds.X+row.Bounds.Width*.55&&t.Bounds.Y<row.Bounds.Y+row.Bounds.Height*.42&&Regex.IsMatch(N(t.Label),@"^[\u4e00-\u9fff]+$")).ToArray();
            var prices=tokens.Where(t=>t.Bounds.X>row.Bounds.X+row.Bounds.Width*.6).Select(t=>Amount(t.Label)).Where(n=>n is not null).Distinct().ToArray();
            if(names.Length!=1){issues.Add("材料行名称不唯一："+row.Bounds);continue;}
            var categories=tokens.Where(t=>t.Bounds.X<row.Bounds.X+row.Bounds.Width*.55&&t.Bounds.Y>names[0].Bounds.Y+names[0].Bounds.Height*.7&&Regex.IsMatch(N(t.Label),@"^[\u4e00-\u9fff]+$")).ToArray();
            rows.Add(new(LarryTargets.MaterialName(names[0].Label),categories.Length==1?categories[0].Label:null,prices.Length==1?prices[0]:null,tokens.Any(t=>N(t.Label)=="已拥有"),row.Bounds,Math.Min(row.Confidence,names[0].Confidence),p.Screen.Frame.Id));
        }
        var materials=needs.GroupBy(n=>N(n.Name!)).Select(g=>{
            var owned=g.Select(n=>n.Owned).Where(n=>n is not null).Distinct().ToArray();var required=g.Select(n=>n.Required).Where(n=>n is not null).Distinct().ToArray();
            var raw=rows.Where(r=>N(r.Name)==g.Key).ToArray();var prices=raw.Select(r=>r.Price).Where(n=>n is not null).Distinct().ToArray();
            int? o=owned.Length==1?owned[0]:null,q=required.Length==1?required[0]:null;
            return new LarryMaterial(g.First().Name!,q,o,prices.Length==1?prices[0]:null,o is not null&&q is not null?o<q:null,raw.Length,raw.Length>0?raw.All(r=>r.Owned):null);
        }).ToArray();
        bool recipeKnown=materials.Length>0&&materials.All(m=>m.Required>0);
        bool expected=target.ExpectedRecipe is null||target.ExpectedRecipe.Count==materials.Length&&materials.All(m=>target.ExpectedRecipe.TryGetValue(m.Name,out int count)&&count==m.Required);
        if(!expected)issues.Add("OCR配方与用户提供的参考配方不一致/缺失；不使用参考配方填充");
        decimal? total=Money(words,"合计价格"),cash=Money(words,"补齐价格"),market=Money(words,"产品市场价");
        bool complete=recipeKnown&&expected&&materials.All(m=>m.Owned is not null&&m.UnitPrice is not null&&m.VisibleRows>=m.Required);
        if(!complete)issues.Add("逐项材料明细仍缺证据；不标为明细完整。合计/产品市场参考价优先，默认不阻塞只读主线返回");
        if(complete&&materials.Sum(m=>m.UnitPrice*m.Required)!=total){complete=false;issues.Add("逐项经济成本与合计不一致");}
        if(complete&&materials.Sum(m=>m.UnitPrice*Math.Max(0,m.Required!.Value-m.Owned!.Value))!=cash){complete=false;issues.Add("缺失材料成本与补齐价不一致");}
        if(total is null||cash is null||market is null)issues.Add("三种金额中有缺失/冲突");
        return new(LarryPageClassifier.Contact,target.Id,target.Name,target.Category,limit??new(target.Limit,null,null,null,"Unavailable"),materials,rows.ToArray(),total,cash,market,market-total,null,"NotAvailable: no reliable sale price and actual sale fees",
            target.ExpectedRecipe is null&&!recipeKnown?"RecipeUnknown":recipeKnown&&expected?"Observed":"RecipeUnverified",
            total is not null&&cash is not null&&market is not null?complete?"Complete":"SummaryComplete":"Partial",p.Screen.Frame.CapturedAt,p.Screen.Frame.Id,p.Screen.ContentHash,p.Screen.Frame.ScreenshotPath,
            words.Length>0?words.Min(t=>t.Confidence):0,p.Vision.Regions.Length>0?p.Vision.Regions.Max(r=>r.Confidence):0,issues.ToArray(),p,[p.Screen.Frame.ScreenshotPath],complete);
    }
    public static LarryDetail Merge(LarryDetail previous,LarryDetail current) {
        if(previous.ItemId!=current.ItemId||previous.At>current.At||previous.MaterialTotal!=current.MaterialTotal||previous.TopUpPrice!=current.TopUpPrice||previous.ProductMarketPrice!=current.ProductMarketPrice)
            throw new NavigationPausedException("Item/price changed during material scroll; keep separate evidence and stop");
        var rows=previous.RawRows.Concat(current.RawRows).DistinctBy(r=>(r.FrameId,r.Bounds)).ToArray();
        var materials=previous.Materials.Concat(current.Materials).GroupBy(m=>N(m.Name)).Select(g=>{
            var qs=g.Select(m=>m.Required).Where(n=>n is not null).Distinct().ToArray();var os=g.Select(m=>m.Owned).Where(n=>n is not null).Distinct().ToArray();
            var rr=rows.Where(r=>N(r.Name)==g.Key).ToArray();var ps=rr.Select(r=>r.Price).Where(n=>n is not null).Distinct().ToArray();
            int? q=qs.Length==1?qs[0]:null,o=os.Length==1?os[0]:null;
            // Repeated identical rows on overlapping scroll frames must never be added twice.
            // Lower bound = largest simultaneous count. If insufficient, remain Partial.
            int visible=rr.GroupBy(r=>r.FrameId).Select(f=>f.Count()).DefaultIfEmpty(0).Max();
            return new LarryMaterial(g.First().Name,q,o,ps.Length==1?ps[0]:null,o is not null&&q is not null?o<q:null,visible,rr.Length>0?rr.All(r=>r.Owned):null);
        }).ToArray();
        bool complete=previous.RecipeStatus=="Observed"&&current.RecipeStatus=="Observed"&&materials.Length>0&&materials.All(m=>m.Required>0&&m.Owned is not null&&m.UnitPrice is not null&&m.VisibleRows>=m.Required)
            &&materials.Sum(m=>m.Required*m.UnitPrice)==current.MaterialTotal
            &&materials.Sum(m=>Math.Max(0,m.Required!.Value-m.Owned!.Value)*m.UnitPrice)==current.TopUpPrice;
        return current with{Materials=materials,RawRows=rows,MaterialsComplete=complete,Status=complete?"Complete":current.Status,EvidenceScreens=(previous.EvidenceScreens??[previous.Screenshot]).Concat(current.EvidenceScreens??[current.Screenshot]).Distinct().ToArray(),Issues=complete?["Multi-frame rows merged conservatively; no transaction performed"]:previous.Issues.Concat(current.Issues).Distinct().ToArray()};
    }
}

// Collection records are isolated from executable recipes and price statistics.
public sealed partial class MarketDatabase {
    public bool StoreLarry(string session,LarryDetail detail,bool replay) {
        if(string.IsNullOrWhiteSpace(session))throw new ArgumentException("Collection session required");
        using var db=Open();using var schema=Command(db,"""
            CREATE TABLE IF NOT EXISTS ContactCollections(SessionId TEXT NOT NULL,ItemId TEXT NOT NULL,FrameId TEXT NOT NULL,
              ContentHash TEXT NOT NULL,ObservedAt TEXT NOT NULL,Replay INTEGER NOT NULL,Status TEXT NOT NULL,Json TEXT NOT NULL,
              PRIMARY KEY(SessionId,ItemId,ContentHash,Replay));
            """);schema.ExecuteNonQuery();
        using var insert=Command(db,"INSERT OR IGNORE INTO ContactCollections VALUES($session,$item,$frame,$hash,$at,$replay,$status,$json)",
            ("$session",session),("$item",detail.ItemId),("$frame",detail.FrameId),("$hash",detail.ContentHash),("$at",detail.At.ToUniversalTime().ToString("O")),("$replay",replay),("$status",detail.Status),("$json",JsonSerializer.Serialize(detail,NavigationEvidenceLog.Json)));
        return insert.ExecuteNonQuery()==1;
    }
}

// Adaptive coordinator reuses the existing executor. Every input remains a single verified task.
// The input adapter controls authorization; production WindowsInputController still only accepts
// registered same-process test windows. A file replay cannot arm real game input.
public sealed class LarryCollector(IScreenSource capture,IVisionPipeline vision,IInputController input,NavigationEvidenceLog log,MarketDatabase database) {
    public async Task<LarryDetail[]> Run(string session,LarryTarget[] targets,ExecutionMode mode,NavigationControl control,CancellationToken token=default,bool collectFullMaterials=false) {
        using var linked=CancellationTokenSource.CreateLinkedTokenSource(token,control.EmergencyToken);var ct=linked.Token;
        var classifier=new LarryPageClassifier();var collected=new List<LarryDetail>();var resolver=new VisualTargetResolver();
        async Task<PageObservation> Observe(){control.Check(ct);var s=await capture.CaptureAsync(ct);var v=await vision.AnalyzeAsync(s,ct);if(!s.Live)s=s with{Width=v.Width,Height=v.Height};log.Raw(s,v);var p=classifier.Classify(s,v);log.Observation(p);if(p.Page=="Unknown"||p.Errors.Length>0)throw new NavigationPausedException("Unknown page/prompt; collection paused");return p;}
        async Task Act(NavigationStep step) {
            var result=await new NavigationExecutor(capture,vision,classifier,input,log).RunAsync(new(session+":"+Guid.NewGuid().ToString("N"),[step],mode,MaxBatchTime:TimeSpan.FromSeconds(45),MaximumFrameAge:TimeSpan.FromSeconds(8),UseCurrentFrameForInput:true),control,ct);
            if(result.State!=NavigationState.SUCCESS||mode!=ExecutionMode.RecognizeOnly&&result.Completed!=1)throw new NavigationPausedException(result.Reason);
        }
        foreach(var target in targets) {
            var visited=new HashSet<string>();LarryLimit? limit=null;bool finished=false;
            for(int step=0;step<32;step++) {
                var p=await Observe();
                if(p.Page=="Exchange") {
                    var detail=LarryDetailReader.Read(p,target,limit);database.StoreLarry(session,detail,!p.Screen.Live);collected.Add(detail);
                    if(mode==ExecutionMode.RecognizeOnly)return collected.ToArray();
                    // Missing rows are never inferred from the price total. Scan until full or boundary.
                    var seen=new HashSet<string>();
                    for(int scan=0;collectFullMaterials&&!detail.MaterialsComplete&&scan<12;scan++) {
                        var view=p.LayoutRegions?.FirstOrDefault(r=>r.Token.Label=="larry_material_viewport")?.Token.Bounds;
                        if(view is null||!seen.Add(LarryRoutes.ViewSignature(p,view)))throw new NavigationPausedException("Material list boundary/repeated viewport; partial evidence saved");
                        await Act(LarryRoutes.Scroll(p));p=await Observe();detail=LarryDetailReader.Merge(detail,LarryDetailReader.Read(p,target,limit));database.StoreLarry(session,detail,!p.Screen.Live);collected.Add(detail);
                    }
                    if(collectFullMaterials&&!detail.MaterialsComplete)throw new NavigationPausedException("Material scan limit; partial evidence saved");
                    await Act(LarryRoutes.Cancel(p));finished=true;break;
                }
                string? category=LarryPageClassifier.ActiveCategory(p.Vision);
                var next=LarryRoutes.Next(p,target,category)??throw new NavigationPausedException("No supported Larry route");
                if(p.Page=="ContactShop"&&category==target.Category) {
                    limit=LarryDetailReader.Limit(p,target);
                    var located=resolver.ResolveDetailed(p,next.Target);
                    if(located.Status==LocationStatus.AMBIGUOUS)throw new NavigationPausedException("AMBIGUOUS product; no guessed click");
                    if(located.Status!=LocationStatus.RESOLVED) {
                        if(!visited.Add(LarryRoutes.ViewSignature(p,LarryRoutes.Goods))||visited.Count>12)throw new NavigationPausedException("Goods scan boundary/repeated viewport or limit");
                        next=LarryRoutes.Scroll(p);
                    }
                }
                await Act(next);if(mode==ExecutionMode.RecognizeOnly)return collected.ToArray();
            }
            if(!finished)throw new NavigationPausedException("Navigation step limit");
        }
        return collected.ToArray();
    }
}
