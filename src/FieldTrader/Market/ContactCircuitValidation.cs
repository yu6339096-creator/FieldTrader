using System.IO;
using System.Text.Json;
using FieldTrader.Navigation;
namespace FieldTrader.Market;

public static class ContactCircuitValidation {
    public static void Stored(string source,string output) {
        Directory.CreateDirectory(output);var results=new List<object>();int failed=0;
        var files=new[]{"dick-route/replay-v2","randall-route/replay-final","larry/replay-final"}
            .SelectMany(dir=>Directory.GetFiles(Path.Combine(source,dir),"*.json"));
        foreach(var file in files) {
            using var doc=JsonDocument.Parse(File.ReadAllText(file));var root=doc.RootElement;
            JsonElement raw;
            if(!root.TryGetProperty("RawVision",out raw)&&!(root.TryGetProperty("Observation",out var observation)&&observation.TryGetProperty("Vision",out raw)))continue;
            var v=raw.Deserialize<VisionResult>(TradingAnalysis.JsonOptions)!;
            try {
                var page=ContactExchangeReader.Page(v);ContactExchangeQuote? quote=null;
                if(page.Kind=="Exchange"&&!ContactExchangeReader.DetailSoldOut(v)) {
                    var targets=ContactExchangeCatalog.Items.Where(t=>t.Category!=ContactExchangeCatalog.Limited&&ContactExchangeReader.Words(v).Any(w=>w.Bounds.X<.5&&w.Bounds.Y<.16&&ContactExchangeCatalog.Matches(w.Label,t.Name))).ToArray();
                    if(targets.Length==1)quote=ContactExchangeReader.Quote(v,targets[0]);
                }
                if(Path.GetFileName(file) is "D07_helmets.json" or "D08_helmets_scrolled.json"&&page.Category!="头盔")throw new InvalidDataException("头盔选中状态未识别");
                if(Path.GetFileName(file)=="R04_headsets.json"&&page.Category!="耳机")throw new InvalidDataException("耳机选中状态未识别");
                results.Add(new{File=file,Page=page.Kind,page.Contact,page.Category,SoldOut=ContactExchangeReader.DetailSoldOut(v),Quote=quote,Cards=ContactExchangeReader.Cards(page).Select(c=>new{c.Item.Id,c.Unavailable,c.OutputQuantity})});
            }catch(Exception ex){failed++;results.Add(new{File=file,Error=ex.Message});Console.WriteLine(Path.GetFileName(file)+": "+ex.Message);}
        }
        File.WriteAllText(Path.Combine(output,"stored-screenshot-results.json"),JsonSerializer.Serialize(new{Mode="StoredRealYoloOcrOutputs",NativeGameInput=false,Failed=failed,Results=results},TradingAnalysis.JsonOptions));
        if(failed>0)throw new InvalidDataException($"{failed} stored screenshot checks failed");
        Console.WriteLine($"{results.Count} stored real YOLO/OCR outputs read; no native game input");
    }
    public static async Task Run(string output) {
        Directory.CreateDirectory(output);var checks=new List<string>();
        void Check(bool ok,string name){if(!ok)throw new InvalidDataException(name);checks.Add(name);}
        void Reject(Action action,string name){try{action();}catch(InvalidOperationException){checks.Add(name);return;}throw new InvalidDataException(name);}
        var world=new World();
        foreach(var item in ContactExchangeCatalog.Items.Where(i=>i.Category!=ContactExchangeCatalog.Limited)) {
            world.Target=item;world.Stage="Exchange";world.Owned=false;
            var q=ContactExchangeReader.Quote(world.View(),item);
            Check(q.CashRequired==100&&q.MaterialTotal==200&&q.Button=="补齐道具",item.Name+"读取实时配方与补齐金额");
            world.Owned=true;q=ContactExchangeReader.Quote(world.View(),item);
            Check(q.CashRequired==0&&q.MaterialTotal==200&&q.Button=="兑换",item.Name+"库存充足时支出为0，材料价值仍为200");
        }
        var mask=ContactExchangeCatalog.Items.Single(i=>i.Id=="randall-dk8");world.Target=mask;world.Owned=false;
        var raw=world.View();var request=new AutoBuyRequest(AutoBuyKind.ContactExchange,mask.Name,1,200,TargetId:mask.Id);
        Check(AutoBuyRules.Read(raw,request).Button=="补齐道具","通用兑换请求已接入原单笔核价执行器");
        Reject(()=>AutoBuyRules.Read(raw,request with{MaximumSpend=99}),"超剩余整轮预算不提交");
        Reject(()=>AutoBuyRules.Read(raw,request with{ExpectedMaterialTotal=201}),"进详情后材料总价值变化重新决策，零提交");
        Reject(()=>AutoBuyRules.Read(raw,request with{ItemName="GS2战术耳机"}),"错商品立即停止");
        Reject(()=>ContactExchangeReader.Quote(raw with{Texts=raw.Texts.Select(t=>t.Label=="0/1"?t with{Label="0/2"}:t).ToArray()},mask),"配方数量变化不沿用旧截图");
        Reject(()=>ContactExchangeReader.Quote(raw with{Texts=raw.Texts.Where(t=>!t.Label.StartsWith("补齐价格")).ToArray()},mask),"补齐价缺失不拿合计价值替代");
        var settings=new ContactCircuitSettings(["larry-100d","dick-o3","randall-dk8"],1000,3,false);
        var q0=ContactExchangeReader.Quote(raw,mask);
        Check(ContactExchangeReader.EstimatedNet(q0,settings)==null,"未核实费用时不假称净利润");
        Check(ContactExchangeReader.EstimatedNet(q0,settings with{FeesVerified=true,SaleRate=.1m,ManagementRate=.05m})==225,"净利润扣除整套材料价值与两类费用");
        world.Stage="Confirmation";var confirm=world.View();
        Check(AutoBuyRules.Read(confirm,request,true).Button=="确定","任意登记商品都可自动确认匹配文案");
        Reject(()=>AutoBuyRules.Read(confirm,request),"未提交前出现的旧确认框不代为确认");
        var prompt=confirm.Texts.Single(t=>t.Label.StartsWith(ExchangeConfirmationReader.Prefix));
        var split=confirm with{Texts=confirm.Texts.Where(t=>t!=prompt).Concat(new[]{prompt with{Label=ExchangeConfirmationReader.Prefix},T(mask.Name,.37,.49,.22)}).ToArray()};
        Check(AutoBuyRules.Read(split,request,true).Button=="确定","确认文案和商品换行时仍可匹配");

        world=new World();var result=await new ContactCircuitRunner(world,world,world,Path.Combine(output,"multi-contact")).Run(settings,()=>true,default);
        Check(result.State=="Completed"&&result.Completed==3&&result.TradeClicks==6&&result.ReservedQuote==300,"大厅到三位联络人，补齐、自动确认、库存核实完整串联");
        Check(world.TradeButtons.SequenceEqual(Enumerable.Range(0,3).SelectMany(_=>new[]{"补齐道具","确定"})),"每目标仅一份配方，没有隐式重复兑换");
        Check(File.ReadAllLines(Path.Combine(output,"multi-contact","test-contact-exchanges.jsonl")).Length==3,"仅成功三笔入测试账本，与真实账本隔离");
        world=new World{MaterialOnlyTopup=true};result=await new ContactCircuitRunner(world,world,world,Path.Combine(output,"without-popup")).Run(settings with{SelectedIds=["larry-100d"]},()=>true,default);
        Check(result.Completed==1&&world.TradeButtons.SequenceEqual(new[]{"补齐道具","兑换"}),"补齐后没有确认弹窗时，重新读取材料已齐详情并直接兑换");
        world=new World{MaterialOnlyTopup=true,ConfirmOwned=true};result=await new ContactCircuitRunner(world,world,world,Path.Combine(output,"three-step")).Run(settings with{SelectedIds=["larry-100d"]},()=>true,default);
        Check(result.Completed==1&&world.TradeButtons.SequenceEqual(new[]{"补齐道具","兑换","确定"})&&result.ReservedQuote==100,"补齐、兑换、可选确认三步完整执行，支出不重复累计");
        world=new World{Owned=true};result=await new ContactCircuitRunner(world,world,world,Path.Combine(output,"owned")).Run(settings,()=>true,default);
        Check(result.Completed==3&&result.TradeClicks==3&&result.ReservedQuote==0&&world.TradeButtons.All(b=>b=="兑换"),"三联络人材料已齐直接兑换不补齐");
        world=new World{Owned=true};result=await new ContactCircuitRunner(world,world,world,Path.Combine(output,"owned")).Run(settings,()=>true,default);
        Check(result.Completed==2&&result.Entries.Any(e=>e.TargetId=="dick-o3"&&e.Reason.Contains("冷却")),"已核实03兑换按独立12小时冷却跳过，其他联络人继续");
        var limitedSettings=new ContactCircuitSettings(["limited-gho2"],1000,5,true);
        world=new World{LimitedReady=false};result=await new ContactCircuitRunner(world,world,world,Path.Combine(output,"limited-refresh")).Run(limitedSettings,()=>true,default);
        Check(result.Completed==1&&world.NavigationButtons.Count(s=>s.StartsWith("刷新商品"))==1,"限时旧批售罄后只刷新一次，新批购买一份，不循环刷新");
        Check(result.Entries.Any(e=>e.TargetId=="limited-gho2"&&e.State=="Completed"),"限时手雷按卡片x5核实新增5枚，不套用单件数量");
        world=new World{LimitedReady=false,RefreshAvailable=false};result=await new ContactCircuitRunner(world,world,world,Path.Combine(output,"limited-cooling")).Run(limitedSettings,()=>true,default);
        Check(result.State=="Completed"&&result.Completed==0&&!world.NavigationButtons.Any(s=>s.StartsWith("刷新商品")),"限时冷却/次数用尽不盲点刷新，不等待一小时");
        world=new World{Uncertain=true};result=await new ContactCircuitRunner(world,world,world,Path.Combine(output,"unknown")).Run(settings,()=>true,default);
        Check(result.State=="Paused"&&world.TradeButtons.Count==2&&result.Completed==0,"结果未知停止整轮，不继续下一联络人且不重试");
        Check(!File.Exists(Path.Combine(output,"unknown","test-contact-exchanges.jsonl")),"结果未知不记成功账本");
        world=new World();result=await new ContactCircuitRunner(world,world,world,Path.Combine(output,"off")).Run(settings,()=>false,default);
        Check(result.State=="Stopped"&&world.TradeButtons.Count==0&&world.NavigationButtons.Count==0,"开关关闭时导航与交易均为零输入");
        world=new World{SoldOut=true};result=await new ContactCircuitRunner(world,world,world,Path.Combine(output,"soldout")).Run(settings,()=>true,default);
        Check(result.Completed==0&&result.Entries.All(e=>e.State=="Skipped")&&world.TradeButtons.Count==0,"售罄逐项跳过，不把背景售罄误判全局异常");
        world=new World{Target=mask,Stage="Exchange",Owned=false};raw=world.View();
        Check(AutoBuyRules.Read(raw with{Texts=raw.Texts.Select(t=>t with{Confidence=.75}).ToArray()},request).QuotedSpend==100,"通用流程全链路保持75% OCR门槛");
        File.WriteAllText(Path.Combine(output,"checks.json"),JsonSerializer.Serialize(new{Mode="SyntheticContactCircuitTests",NativeGameInput=false,Passed=true,Count=checks.Count,Checks=checks},TradingAnalysis.JsonOptions));
        Console.WriteLine($"{checks.Count} contact-circuit checks passed; no native game input");
    }
    static VisualToken T(string s,double x,double y,double width=.1)=>new(s,.99,new(x,y,width,.018));
    sealed class World:IScreenSource,IVisionPipeline,IContactCircuitInput {
        public string Stage="Home",Contact="",Category="";public bool Owned,SoldOut,Uncertain,LimitedReady=true,RefreshAvailable=true,MaterialOnlyTopup,ConfirmOwned;
        public ContactExchangeTarget? Target;
        public bool IsNative=>false;
        public List<string> TradeButtons=[],NavigationButtons=[];
        readonly Dictionary<string,int> inventory=[];
        VisionResult? current;
        public VisionResult View() {
            var words=new List<VisualToken>{T("工具箱",.7,.2),T("水壶",.8,.3),T("望远镜",.7,.4)};
            foreach(var pair in inventory)for(int i=0;i<pair.Value;i++)words.Add(T(pair.Key,.65,.60+i*.025,.2));
            var pixels=new Dictionary<string,double>();var regions=new List<VisualToken>();
            if(Stage=="Home")words.Add(T("进入暗区",.04,.86));
            if(Stage is "Home" or "ContactShop")words.Add(T("联络人",.51,.965,.06));
            if(Stage=="ContactList") {
                int i=0;foreach(var c in NavigationSceneAdapter.Contacts)words.Add(T(c,.25,.25+i++*.07,.20));
                words.Add(T(Contact.Length>0?Contact:NavigationSceneAdapter.Contacts[0],.66,.25,.14));words.Add(T("购买",.6,.70,.04));words.Add(T("出售",.72,.70,.04));
            }
            if(Stage=="ContactShop") {
                words.Add(T(Contact,.05,.06,.2));int n=0;
                foreach(var category in ContactExchangeCatalog.Items.Where(t=>t.Contact==Contact).Select(t=>t.Category).Distinct())words.Add(T(category,.05+n++*.1,.125,.07));
                pixels["CircuitTabOrange:"+Category]=.5;
                if(Category==ContactExchangeCatalog.Limited)words.Add(T(RefreshAvailable?"刷新商品2/3":"59:59后可刷新",.38,.125,.09));
                n=0;foreach(var item in ContactExchangeCatalog.Items.Where(t=>t.Contact==Contact&&t.Category==Category&&(Category!=ContactExchangeCatalog.Limited||t.Id=="limited-gho2"))) {
                    double x=.05+n++*.108;
                    if(x>.40)break;
                    words.Add(T(item.Name,x,.175,.10));regions.Add(new("product_card",.99,new(x-.003,.16,.105,.235)));
                    if(SoldOut||Category==ContactExchangeCatalog.Limited&&!LimitedReady)words.Add(T("已售罄",x,.35,.05));
                    if(item.ItemClass?.StartsWith("grenade_")==true)words.Add(T("x5",x+.07,.35,.03));
                }
            }
            if(Stage=="Exchange") {
                var t=Target!;words.AddRange([T(t.Name,.14,.1,.3),T(t.Contact,.05,.06,.2),T("道具兑换",.5,.09),T("需要提交以下道具",.5,.15,.15),T("兑换列表",.5,.30)]);
                int n=0;foreach(var material in t.Recipe??new(){["实时材料"]=1}) {
                    double x=.5+n++*.055;words.Add(T(material.Key,x,.20,.045));words.Add(T($"{(Owned?material.Value+1:0)}/{material.Value}",x+.022,.24,.028));
                }
                words.Add(T("合计价格：200",.5,.80,.15));if(!Owned)words.Add(T("补齐价格：100",.72,.80,.15));words.Add(T("产品市场价：500",.5,.85,.18));words.Add(T(Owned?"兑换":"补齐道具",.7,.90,.1));words.Add(T("取消",.53,.90,.06));
            }
            if(Stage=="Confirmation")words.AddRange([T(ExchangeConfirmationReader.Prefix+Target!.Name,.35,.45,.3),T("确定",.56,.54,.04),T("取消",.38,.54,.04)]);
            if(Stage=="FailedResult")words.Add(T("未知画面",.45,.5));
            return new("fixture",DateTimeOffset.UtcNow,"fixture",2560,1440,regions.ToArray(),words.ToArray(),0,0,0,PixelMeasurements:pixels);
        }
        public Task<CapturedScreen> CaptureAsync(CancellationToken token) {
            token.ThrowIfCancellationRequested();var id=Guid.NewGuid().ToString("N");var at=DateTimeOffset.UtcNow;
            current=View() with{FrameId=id,ContentHash=id,CapturedAt=at};return Task.FromResult(new CapturedScreen(new(id,at,"synthetic-contact.png"),id,2560,1440,1,0,0,true));
        }
        public Task<VisionResult> AnalyzeAsync(CapturedScreen screen,CancellationToken token)=>Task.FromResult(current!);
        public Task NavigateAsync(ContactNavigationAction action,CapturedScreen screen,CancellationToken token) {
            token.ThrowIfCancellationRequested();NavigationButtons.Add(action.Name);
            if(action.Operation==InputOperation.Scroll)return Task.CompletedTask;
            if(action.Name.StartsWith("刷新商品")){LimitedReady=true;RefreshAvailable=false;}
            else if(action.Name=="联络人")Stage="ContactList";
            else if(NavigationSceneAdapter.Contacts.Contains(action.Name))Contact=action.Name;
            else if(action.Name=="购买"){Stage="ContactShop";Category=ContactExchangeCatalog.Items.First(t=>t.Contact==Contact).Category;}
            else if(action.Name=="取消")Stage="ContactShop";
            else if(ContactExchangeCatalog.Items.Any(t=>t.Contact==Contact&&t.Category==action.Name))Category=action.Name;
            else {Target=ContactExchangeCatalog.Items.Single(t=>t.Contact==Contact&&t.Category==Category&&t.Name==action.Name);Stage="Exchange";}
            return Task.CompletedTask;
        }
        public Task ClickAsync(AutoBuyAction action,CapturedScreen screen,CancellationToken token) {
            token.ThrowIfCancellationRequested();TradeButtons.Add(action.Button);
            if(action.Button=="补齐道具"){if(MaterialOnlyTopup){Owned=true;Stage="Exchange";}else Stage="Confirmation";}
            else if(action.Button=="兑换"&&ConfirmOwned)Stage="Confirmation";
            else {if(Uncertain)Stage="FailedResult";else {inventory[Target!.Name]=inventory.GetValueOrDefault(Target.Name)+(Target.ItemClass?.StartsWith("grenade_")==true?5:1);Stage="ContactShop";}}
            return Task.CompletedTask;
        }
    }
}
