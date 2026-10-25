using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;

namespace FieldTrader.Market;
public sealed class TradingPanel : DockPanel {
    private readonly MarketDatabase db;
    private readonly string root;
    private readonly TextBlock summary=new(){TextWrapping=TextWrapping.Wrap,Margin=new Thickness(8)};
    private readonly TextBox report=new(){IsReadOnly=true,AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};
    private readonly ComboBox recipes=new(){MinWidth=250,DisplayMemberPath="Id",Margin=new Thickness(4)};
    private readonly List<Button> buttons=[];
    private CancellationTokenSource? cancel;
    private string? session;
    private TradingSettings? settings;
    private TradeScreenFacts? facts;
    public TradingAnalysis Analysis {get;private set;}
    public bool Busy=>cancel is not null;
    public TradingPanel(MarketDatabase database,string directory) {
        db=database;root=directory;
        try{settings=TradingAnalysis.Load(Path.Combine(root,"trading-settings.json"));}catch(Exception ex){summary.Text="配置未载入："+ex.Message;}
        Analysis=new(db,settings);
        var controls=new WrapPanel();SetDock(controls,Dock.Top);Children.Add(controls);
        SetDock(summary,Dock.Top);Children.Add(summary);Children.Add(report);
        Add(controls,"导入兑换配方",async()=>{
            var file=Select("配方 JSON|*.json");if(file is null)return;
            var rows=JsonSerializer.Deserialize<ExchangeRecipe[]>(await File.ReadAllTextAsync(file),TradingAnalysis.JsonOptions)??[];
            await Task.Run(()=>{foreach(var row in rows)db.SaveUnifiedRecipe(row);});await RefreshRecipes();
            summary.Text=$"已保存 {rows.Length} 个配方；只有附核验来源且剩余次数明确的配方参与评估。";
        });
        Add(controls,"导入费率与预算",async()=>{
            var file=Select("交易配置 JSON|*.json");if(file is null)return;
            var candidate=TradingAnalysis.Load(file)??throw new InvalidDataException("配置为空");
            if(candidate.Fees is null||candidate.Policy is null||candidate.Inventory is null||candidate.SpecialRules is null)throw new InvalidDataException("配置缺少费用、预算、库存或特殊规则");
            await File.WriteAllTextAsync(Path.Combine(root,"trading-settings.json"),JsonSerializer.Serialize(candidate,TradingAnalysis.JsonOptions));
            settings=candidate;Analysis=new(db,settings);await RefreshRecipes();summary.Text="配置已保存。请重新采集各材料和成品页面；不使用旧配置的缓存决策。";
        });
        Add(controls,"导出录入模板",async()=>{
            var dialog=new SaveFileDialog{Filter="配置 JSON|*.json",FileName="trading-settings.json"};if(dialog.ShowDialog()!=true)return;
            await File.WriteAllTextAsync(dialog.FileName,JsonSerializer.Serialize(new TradingSettings(new(0,0,0,false,""),new(0),[],[]),TradingAnalysis.JsonOptions));
            var folder=Path.GetDirectoryName(dialog.FileName)!;
            await File.WriteAllTextAsync(Path.Combine(folder,"catalog-for-recipes.json"),JsonSerializer.Serialize(db.Catalog().Select(i=>new{i.Id,i.Name,i.Category}),TradingAnalysis.JsonOptions));
            await File.WriteAllTextAsync(Path.Combine(folder,"recipes-unverified.json"),JsonSerializer.Serialize(new[]{new ExchangeRecipe("填写唯一配方ID","填写联络人","从商品目录选成品ID",1,[new("从商品目录选材料ID",1)],0,VerificationStatus.Unverified,"",null)},TradingAnalysis.JsonOptions));
            summary.Text="已导出配置、商品ID目录和未验证配方模板。请按游戏核验填写；默认预算为零，费率待核验；未知费用不作为零费用计算利润。";
        });
        controls.Children.Add(recipes);
        Add(controls,"预演所选方案",async()=>{
            var unresolved=await Task.Run(()=>db.SimulationSessions().Any(s=>s.Id.StartsWith("preview-",StringComparison.Ordinal)&&(s.Pending is not null||s.Phase is SimulationPhase.Unknown or SimulationPhase.Buying or SimulationPhase.Exchanging or SimulationPhase.Selling or SimulationPhase.Listed)));
            if(unresolved)throw new InvalidOperationException("存在未完成或 UNKNOWN 预演，禁止换会话重复提交。请先查看模拟记录并核对原操作。");
            if(recipes.SelectedItem is not ExchangeRecipe recipe||settings is null)throw new InvalidOperationException("先导入配方与预算费率，选择配方并完成相关页面采集");
            var decision=Analysis.LastDecisions.SingleOrDefault(d=>d.RecipeId==recipe.Id);
            if(decision is null||decision.Kind==DecisionKind.SKIP)throw new InvalidOperationException("尚无满足整套利润条件的决策；网站参考价和这批历史截图不能替代实时历史样本");
            if(DateTimeOffset.UtcNow-decision.At>TimeSpan.FromSeconds(settings.Policy.PriceMaxAgeSeconds))throw new InvalidOperationException("决策已过期，请重新采集");
            if(facts?.Quota is not {} quota)throw new InvalidOperationException("先采集上架页的本周实际额度");
            session="preview-"+Guid.NewGuid().ToString("N");cancel=new();
            try {
                var rule=settings.SpecialRules.SingleOrDefault(r=>"special:"+r.ItemId==recipe.Id);
                var state=await Task.Run(()=>SimulationRunner.Run(db,session,recipe,decision,settings,quota,decision.ConservativeUnitSale,cancel.Token,rule));
                var ledger=await Task.Run(()=>db.SimulationLedger(session));
                report.Text=$"隔离预演完成：{state.Phase}\n模拟利润：{ledger.Sum(e=>e.RealizedProfit):N0}\n模拟现金变动：{ledger.Sum(e=>e.Cash):N0}\n模拟额度剩余：{state.Quota?.Remaining}\n\n所有买入、兑换、成交结果均由模拟器生成；没有点击游戏。\n\n"+string.Join("\n",ledger.Select(e=>$"{e.Kind} {e.ItemId} × {e.Quantity} | 现金 {e.Cash} | 盈亏 {e.RealizedProfit}"));
            }finally{cancel.Dispose();cancel=null;session=null;}
        });
        Add(controls,"运行模拟回归",async()=>{
            string output=Path.Combine(root,"simulation-checks",DateTime.Now.ToString("yyyyMMdd-HHmmss-fff"));
            await Task.Run(()=>MarketCdValidation.Run(output));
            using var json=JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(output,"stage-cd-tests.json")));
            report.Text="模拟回归通过（测试配方是虚构夹具，不写入行情数据库）\n"+string.Join("\n",json.RootElement.GetProperty("Checks").EnumerateArray().Select(x=>x.ToString()));
        });
        Add(controls,"查看模拟记录",async()=>{
            report.Text=await Task.Run(()=>{
                var history=db.SimulationSessions();
                return history.Length==0?"暂无模拟交易记录。真实截图采集不产生交易账本。":string.Join("\n\n",history.Take(20).Select(s=>$"会话 {s.Id}\n状态 {s.Phase} · 预算 {s.Budget} · 支出 {s.Spent} · 待核对 {s.Pending?.Id??"无"}\n{s.Reason}\n"+string.Join("\n",db.SimulationLedger(s.Id).Select(e=>$"{e.Kind} {e.ItemId} × {e.Quantity} | 现金 {e.Cash} | 盈亏 {e.RealizedProfit}"))));
            });
        });
        report.Text="使用顺序：导入核验配方 → 配置预算与费用 → 只读采集材料、成品和上架页 → 查看整套利润决策 → 预演。\n\nE 阶段请人工切换游戏页面，使用上方‘连续只读采集’。自动导航与交易提交保持关闭。\n\n真实截图只产生识别证据，不能直接修改模拟账本。购买结果数量不清楚、挂单身份不明确或缺少配方都会阻止执行。\n\n子弹/钥匙按单品资格配置；未核验商品没有直接转售资格。";
        Loaded+=async(_,_)=>await RefreshRecipes();
    }
    private async Task RefreshRecipes(){var stored=await Task.Run(()=>db.Recipes());recipes.ItemsSource=stored.Concat((settings?.SpecialRules??[]).Select(r=>new ExchangeRecipe("special:"+r.ItemId,"特殊商品直接转售",r.ItemId,1,[],0,VerificationStatus.Unverified,r.EvidenceId,1))).ToArray();}
    private static string? Select(string filter){var dialog=new OpenFileDialog{Filter=filter};return dialog.ShowDialog()==true?dialog.FileName:null;}
    private void Add(Panel panel,string title,Func<Task> action){var button=new Button{Content=title,Margin=new Thickness(3)};buttons.Add(button);panel.Children.Add(button);button.Click+=async(_,_)=>{
        foreach(var b in buttons)b.IsEnabled=false;
        try{await action();}catch(Exception ex){summary.Text="已暂停："+ex.Message;}finally{foreach(var b in buttons)b.IsEnabled=true;}
    };}
    public void SetCollecting(bool collecting){foreach(var b in buttons)b.IsEnabled=!collecting;}
    public void Stop(){cancel?.Cancel();if(session is {} id)new TradingSimulation(db,id).Stop();}
    public void Observe(CollectionUpdate update) {
        if(update.TradeFacts is {} f){if(f.Quota is not null)facts=f;
            summary.Text=$"当前页面：{f.Page} · 价格变化拒绝：{(f.PriceChanged?"是（正常事件）":"否")}"
                +(f.Quota is {} q?$" · 本周已用 {q.Used}/{q.Limit}，剩余 {q.Remaining}":"")
                +(f.Sale is {} s?$"\n{s.ItemName} × {s.Quantity} · 售价 {s.DisplayedUnitPrice} · 管理费 {s.ManagementFee} · 成交费 {s.SaleFee} · 两项费用后 {s.NetAfterAllFees}":"")
                +(f.Purchase is {} buy?$"\n购买报价：{buy.ItemName} × {buy.Quantity} · 单价 {buy.UnitPrice} · 合计 {buy.Total}（未成交）":"")
                +(f.Receipt is {} r?$"\n购买结果：{r.ItemName} × {r.Quantity?.ToString()??"未知"} · 金额 {r.Total} · {(r.Complete?"字段核对完整（仍需关联待确认操作）":"证据不完整，不入账")}":"");
            var reference=Analysis.Reference;
            string referenceText=reference is null?"":$"参考差价：{reference.ItemName} × {reference.Quantity} · 对应成本 {reference.Cost} · 预计到账 {reference.Net} · 管理费 {reference.ManagementFee} · 差价 {reference.Spread}\n"+string.Join("\n",reference.Sources.Concat(reference.Missing))+"\n\n";
            string exchangeText=f.Exchange is {} e?$"兑换：{e.ItemName} · {e.State}\n材料合计估值 {e.MaterialValuation} · 补齐支出报价 {e.TopUpSpend} · 产品市场参考价 {e.ProductMarketPrice}\n"+string.Join("\n",e.Materials.Select(m=>$"{m.Name}：拥有 {m.Owned?.ToString()??"未知"}/需求 {m.Required?.ToString()??"未知"}"))+"\n":"";
            report.Text=referenceText+exchangeText+string.Join("、",f.Events??[])+"\n"+string.Join("\n",f.Issues)+"\n\n"+(update.Decisions?.Length>0?string.Join("\n",update.Decisions.Select(d=>$"{d.Kind} · {d.RecipeId}\n预计净利润 {d.ExpectedProfit:N0} · 收益率 {d.Roi:P1}\n{d.Reason}\n")):"当前帧没有可评估的已导入配方。不会为便宜的单个材料自行下单。");
        }
    }
}
