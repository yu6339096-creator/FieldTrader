using System.IO;
using System.Text.Json;
using FieldTrader.Navigation;
namespace FieldTrader.Market;

public sealed record ContactNavigationAction(string Name,Region Bounds,InputOperation Operation=InputOperation.Click,int ScrollDelta=0);
public interface IContactCircuitInput:IRealTradeInput {
    Task NavigateAsync(ContactNavigationAction action,CapturedScreen screen,CancellationToken token);
}
public sealed record ContactCircuitEntry(string TargetId,string Name,string State,string Reason,decimal ReservedQuote,string? Evidence=null);
public sealed record ContactCircuitOutcome(string State,string Reason,int Completed,int NavigationActions,int TradeClicks,decimal ReservedQuote,string EvidenceDirectory,ContactCircuitEntry[] Entries);

// One finite pass. Owns the same mouse gate and execution.lock as the proven single-trade runner.
public sealed class ContactCircuitRunner(IScreenSource capture,IVisionPipeline vision,IContactCircuitInput input,string root,Action<string>? progress=null) {
    public async Task<ContactCircuitOutcome> Run(ContactCircuitSettings settings,Func<bool> enabled,CancellationToken token) {
        if(settings.TotalBudget<=0||settings.MaximumExchanges is <1 or >100||settings.SelectedIds.Length==0
            ||settings.SelectedIds.Distinct().Count()!=settings.SelectedIds.Length||settings.SelectedIds.Any(id=>!ContactExchangeCatalog.Items.Any(i=>i.Id==id))
            ||settings.MinimumProfit<0||settings.MinimumRoi<0||settings.SaleRate<0||settings.ManagementRate<0||settings.SaleRate+settings.ManagementRate>=1)
            throw new InvalidOperationException("请勾选商品并设置有效整轮预算、数量和利润条件");
        Directory.CreateDirectory(root);string session=Guid.NewGuid().ToString("N"),folder=Path.Combine(root,"contacts-"+session);Directory.CreateDirectory(folder);
        var entries=new List<ContactCircuitEntry>();int completed=0,navCount=0,clicks=0;decimal spent=0;CapturedScreen? last=null;int frame=0;
        var selected=settings.SelectedIds.Select(id=>ContactExchangeCatalog.Items.Single(i=>i.Id==id)).ToArray();
        var lastCompleted=new Dictionary<string,DateTimeOffset>();
        var ledger=Path.Combine(root,input.IsNative?"contact-exchanges.jsonl":"test-contact-exchanges.jsonl");
        if(File.Exists(ledger))foreach(var line in File.ReadLines(ledger)) {
            try {using var doc=JsonDocument.Parse(line);var e=doc.RootElement;var id=e.GetProperty("Target").GetString()!;var at=e.GetProperty("At").GetDateTimeOffset();if(!lastCompleted.TryGetValue(id,out var old)||at>old)lastCompleted[id]=at;}catch(JsonException){}catch(KeyNotFoundException){}
        }
        FileStream? lease=null;var notes=Path.Combine(folder,"events.jsonl");
        void Record(string state,object detail)=>File.AppendAllText(notes,JsonSerializer.Serialize(new{At=DateTimeOffset.UtcNow,Session=session,NativeGameInput=input.IsNative,State=state,Details=detail},TradingAnalysis.JsonOptions).Replace("\r","").Replace("\n","")+"\n");
        void Guard(){token.ThrowIfCancellationRequested();if(!enabled())throw new OperationCanceledException("自动交易已关闭",token);if(frame>400)throw new InvalidOperationException("单轮观察上限已到");}
        async Task<(CapturedScreen Screen,CircuitPage Page)> Observe() {
            Guard();var screen=await capture.CaptureAsync(token);var raw=await vision.AnalyzeAsync(screen,token);Guard();frame++;
            VisualTargetResolver.Validate(new(screen,raw,"Unknown","",[]));
            if(!screen.Live||screen.WindowHandle==0||DateTimeOffset.UtcNow<screen.Frame.CapturedAt
                ||last is {} old&&(screen.Frame.Id==old.Frame.Id||screen.Frame.CapturedAt<=old.Frame.CapturedAt||screen.WindowHandle!=old.WindowHandle||screen.Width!=old.Width||screen.Height!=old.Height||screen.ScreenX!=old.ScreenX||screen.ScreenY!=old.ScreenY))
                throw new InvalidOperationException("游戏窗口、截图时效或画面来源发生变化");
            last=screen;var page=ContactExchangeReader.Page(raw);
            File.WriteAllText(Path.Combine(folder,screen.Frame.Id+".json"),JsonSerializer.Serialize(new{Screen=screen,RawVision=raw,Page=page.Kind,Contact=page.Contact,Category=page.Category},TradingAnalysis.JsonOptions));
            if(page.Kind=="Unknown")throw new InvalidOperationException("当前页面文字无法识别，停止本轮");
            return(screen,page);
        }
        VisualToken Find(CircuitPage p,string name,Region area) {
            var words=ContactExchangeReader.Words(p.Vision).Where(t=>Geometry.Contains(area,t.Bounds)&&ContactExchangeCatalog.Matches(t.Label,name)).ToArray();
            return words.Length==1?words[0]:throw new InvalidOperationException("找不到唯一可点击文字："+name);
        }
        async Task Act(CircuitPage before,Func<CircuitPage,ContactNavigationAction> locate) {
            Guard();var fresh=await Observe();
            if(fresh.Page.Kind!=before.Kind||fresh.Page.Contact!=before.Contact||fresh.Page.Category!=before.Category)throw new InvalidOperationException("点击前页面已变化");
            var action=locate(fresh.Page);if(DateTimeOffset.UtcNow-fresh.Screen.Frame.CapturedAt>TimeSpan.FromSeconds(5))throw new InvalidOperationException("点击前画面已过期");if(!action.Bounds.Valid)throw new InvalidOperationException("导航点击区域无效");
            Record("NavigationAttempted",new{Action=action,Frame=fresh.Screen.Frame.Id,Position=CoordinateTransform.ToScreen(action.Bounds,fresh.Screen)});
            await input.NavigateAsync(action,fresh.Screen,token);navCount++;Guard();Record("NavigationSent",action);await Task.Delay(350,token);
        }
        Task Click(CircuitPage p,string text,Region area)=>Act(p,fresh=>new(text,Find(fresh,text,area).Bounds));
        Task Scroll(CircuitPage p,int delta)=>Act(p,fresh=> {
            if(fresh.Kind!="ContactShop"||fresh.Contact!=p.Contact||fresh.Category!=p.Category)throw new InvalidOperationException("列表滚动上下文不符");
            return new("商品列表",new(.06,.2,.40,.65),InputOperation.Scroll,delta);
        });
        async Task<CircuitPage> Shop(ContactExchangeTarget target) {
            for(int step=0;step<12;step++) {
                var (_,p)=await Observe();
                if(p.Kind=="Confirmation")throw new InvalidOperationException("存在上一次未处理确认框；本轮尚未提交，不能代为确认");
                if(p.Kind=="Exchange") {await Click(p,"取消",new(.45,.7,.25,.3));continue;}
                if(p.Kind=="ContactShop"&&p.Contact==target.Contact) {
                    if(p.Category==target.Category)return p;
                    await Click(p,target.Category,new(.04,.115,.44,.05));continue;
                }
                if(p.Kind=="ContactList") {
                    bool profile=ContactExchangeReader.Words(p.Vision).Any(t=>t.Bounds.X>.6&&t.Bounds.Y<.35&&ContactExchangeCatalog.Key(t.Label)==ContactExchangeCatalog.Key(target.Contact));
                    if(profile)await Click(p,"购买",new(.55,.66,.14,.08));
                    else await Click(p,target.Contact,new(.19,.21,.36,.52));
                    continue;
                }
                if(p.Kind=="Purchase")throw new InvalidOperationException("请关闭市场购买弹窗后再启动联络人串联");
                await Click(p,"联络人",new(.45,.94,.15,.06));
            }
            throw new InvalidOperationException("进入联络人目录超出步骤上限");
        }
        string Signature(CircuitPage p)=>string.Join("|",ContactExchangeReader.Words(p.Vision).Where(t=>Geometry.Contains(LarryRoutes.Goods,t.Bounds)).OrderBy(t=>t.Bounds.Y).ThenBy(t=>t.Bounds.X).Select(t=>$"{t.Label}:{t.Bounds.Y:F2}"));
        async Task<bool> Trade(ContactProductCard card,CircuitPage p) {
            Guard();if(completed>=settings.MaximumExchanges||spent>=settings.TotalBudget)return false;
            progress?.Invoke($"{card.Item.Contact} / {card.Item.Category}：读取 {card.Item.Name} 的兑换报价");
            await Click(p,card.Item.Name,card.Bounds);
            var (_,detail)=await Observe();
            if(detail.Kind=="Exchange"&&ContactExchangeReader.DetailSoldOut(detail.Vision)) {
                entries.Add(new(card.Item.Id,card.Item.Name,"Skipped","详情提示当前商品已售罄",0));await Click(detail,"取消",new(.45,.7,.25,.3));return true;
            }
            var quote=ContactExchangeReader.Quote(detail.Vision,card.Item);
            decimal? net=ContactExchangeReader.EstimatedNet(quote,settings);
            Record("ExchangeQuote",new{Target=card.Item,Quote=quote,EstimatedNet=net,FeeRatesVerified=settings.FeesVerified});
            string? skip=quote.CashRequired>settings.TotalBudget-spent?"本次补齐超过剩余整轮预算":
                !card.Item.Mandatory&&(net is null||net<settings.MinimumProfit||net/quote.MaterialTotal<settings.MinimumRoi)?"预计净利润/利润率不足，或尚未填写已核实费率":null;
            if(skip is not null){entries.Add(new(card.Item.Id,card.Item.Name,"Skipped",skip,0));await Click(detail,"取消",new(.45,.7,.25,.3));return true;}
            if(card.OutputQuantity<1)throw new InvalidOperationException("兑换成品数量未识别，不能提交");
            var request=new AutoBuyRequest(AutoBuyKind.ContactExchange,card.Item.Name,1,settings.TotalBudget-spent,TargetId:card.Item.Id,OutputQuantity:card.OutputQuantity,ExpectedMaterialTotal:quote.MaterialTotal,ExpectedProductReference:quote.ProductReference);
            var result=await new AutoBuyRunner(capture,vision,input,root,progress).RunOwned(request,enabled,token,p.Vision);
            spent+=result.ReservedQuote;clicks+=result.Clicks;entries.Add(new(card.Item.Id,card.Item.Name,result.State,result.Reason,result.ReservedQuote,result.EvidenceDirectory));
            if(result.State!="Completed")throw new InvalidOperationException($"{card.Item.Name}：{result.Reason}；本轮停止，不重复提交");
            completed++;lastCompleted[card.Item.Id]=DateTimeOffset.UtcNow;
            // Only independently verified completion enters this real ledger. Quotes are not actual cash.
            File.AppendAllText(Path.Combine(root,input.IsNative?"contact-exchanges.jsonl":"test-contact-exchanges.jsonl"),JsonSerializer.Serialize(new{Session=session,Target=card.Item.Id,Product=card.Item.Name,Quantity=card.OutputQuantity,At=DateTimeOffset.UtcNow,NativeGameInput=input.IsNative,ReservedQuote=result.ReservedQuote,ActualCash=result.ActualCash,MaterialValue=quote.MaterialTotal,ProductReference=quote.ProductReference,EstimatedNet=net,Evidence=result.EvidenceDirectory},TradingAnalysis.JsonOptions).Replace("\r","").Replace("\n","")+"\n");
            Guard();return true;
        }
        async Task Scan(ContactExchangeTarget[] targets,bool limited) {
            var first=targets[0];var p=await Shop(first);await Scroll(p,9600);p=(await Observe()).Page;
            var processed=new HashSet<string>();var seen=new HashSet<string>();int generation=0;bool refreshed=false;
            for(int n=0;n<48&&completed<settings.MaximumExchanges&&spent<settings.TotalBudget;n++) {
                Guard();p=await Shop(first);
                var cards=ContactExchangeReader.Cards(p).Where(c=>targets.Any(t=>t.Id==c.Item.Id)&&!processed.Contains(c.Item.Id)).ToArray();
                bool traded=false;
                foreach(var card in cards) {
                    processed.Add(card.Item.Id);
                    if(card.Item.CooldownSeconds is {} wait&&lastCompleted.TryGetValue(card.Item.Id,out var lastDone)&&DateTimeOffset.UtcNow<lastDone.AddSeconds(wait)){entries.Add(new(card.Item.Id,card.Item.Name,"Skipped","上次已核实兑换仍在独立冷却期",0));continue;}
                    if(card.Unavailable){entries.Add(new(card.Item.Id,card.Item.Name,"Skipped","冷却、限购用尽或售罄",0));continue;}
                    if(card.OutputQuantity==0){entries.Add(new(card.Item.Id,card.Item.Name,"Skipped","卡片兑换数量未识别",0));continue;}
                    traded=await Trade(card,p);break;
                }
                if(traded)continue;
                if(!seen.Add(Signature(p))||processed.Count==targets.Length) {
                    if(limited&&!refreshed&&settings.RefreshLimited) {
                        var refresh=ContactExchangeReader.Words(p.Vision).Where(t=>t.Bounds.X>.35&&t.Bounds.X<.49&&t.Bounds.Y>.115&&t.Bounds.Y<.16&&System.Text.RegularExpressions.Regex.IsMatch(ContactExchangeCatalog.Key(t.Label),@"^刷新商品[1-3]/3$")).ToArray();
                        var acquire=ContactExchangeReader.Words(p.Vision).Where(t=>t.Label=="获取限时商品"&&t.Bounds.X>.15&&t.Bounds.X<.4&&t.Bounds.Y>.45&&t.Bounds.Y<.65).ToArray();
                        if(refresh.Length==1||acquire.Length==1) {
                            var button=(refresh.Length==1?refresh:acquire)[0];await Click(p,button.Label,new(0,0,1,1));refreshed=true;generation++;
                            var refreshedPage=(await Observe()).Page;
                            if(refreshedPage.Contact!=first.Contact||refreshedPage.Category!=ContactExchangeCatalog.Limited||Signature(refreshedPage)==Signature(p))throw new InvalidOperationException("限时刷新结果未核实，不再次刷新");
                            Record("LimitedRefreshVerified",new{Generation=generation,ConsumesChance=true});processed.Clear();seen.Clear();await Scroll(refreshedPage,9600);continue;
                        }
                    }
                    break;
                }
                await Scroll(p,-600);
            }
            foreach(var missing in targets.Where(t=>!processed.Contains(t.Id)))entries.Add(new(missing.Id,missing.Name,"Skipped","本轮商品列表未找到，或达到本轮预算/次数上限",0));
        }
        ContactCircuitOutcome Finish(string state,string reason) {var result=new ContactCircuitOutcome(state,reason,completed,navCount,clicks,spent,folder,entries.ToArray());Record(state,result);File.WriteAllText(Path.Combine(folder,"summary.json"),JsonSerializer.Serialize(result,TradingAnalysis.JsonOptions));return result;}
        await NavigationInputGate.Shared.WaitAsync(token);NavigationInputGate.Enter();
        try {
            lease=new FileStream(Path.Combine(root,"execution.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
            Record("Started",settings);
            foreach(var contact in NavigationSceneAdapter.Contacts) {
                var items=selected.Where(i=>i.Contact==contact).ToArray();if(items.Length==0)continue;
                foreach(var group in items.GroupBy(t=>t.Category).OrderBy(g=>g.Key==ContactExchangeCatalog.Limited?1:0)) {
                    Guard();if(completed>=settings.MaximumExchanges||spent>=settings.TotalBudget)break;
                    await Scan(group.ToArray(),group.Key==ContactExchangeCatalog.Limited);
                }
            }
            return Finish("Completed","已完成一轮所选联络人兑换；冷却/售罄/未达利润条件的商品已跳过，不自动开始下一轮");
        } catch(Exception ex) when(ex is not OutOfMemoryException) {return Finish(token.IsCancellationRequested||!enabled()?"Stopped":"Paused",ex.Message);}
        finally{lease?.Dispose();NavigationInputGate.Exit();NavigationInputGate.Shared.Release();}
    }
}
