using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
namespace FieldTrader.Market;
public sealed class ProfitRecommendationPanel : DockPanel {
 readonly MarketDatabase db;readonly TextBlock status=new(){TextWrapping=TextWrapping.Wrap,Margin=new Thickness(5)};
 readonly TextBox report=new(){IsReadOnly=true,AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};
 readonly CheckBox history=new(){Content="查看历史回放建议",Margin=new Thickness(10,5,0,5)};
 public ProfitRecommendationPanel(MarketDatabase database) {
  db=database;var header=new StackPanel{Margin=new Thickness(8)};SetDock(header,Dock.Top);Children.Add(header);
  header.Children.Add(new TextBlock{Text="统一利润建议 · 仅分析",FontSize=22});
  header.Children.Add(new TextBlock{Text="材料总价值 ≠ 补齐支出。费用未知时不计算净利润；各方案独立估算，不代表可以同时占用同一批库存与预算。",TextWrapping=TextWrapping.Wrap});
  var buttons=new WrapPanel();header.Children.Add(buttons);
  void Button(string title,Action action){var b=new Button{Content=title,Margin=new Thickness(3)};buttons.Children.Add(b);b.Click+=(_,_)=>{try{action();}catch(Exception e){status.Text=e.Message;}};}
  Button("更新建议",Refresh);
  Button("导入库存、费用与可用次数",()=>{var dialog=new OpenFileDialog{Filter="建议配置|*.json"};if(dialog.ShowDialog()!=true)return;var context=JsonSerializer.Deserialize<ProfitAdviceContext>(File.ReadAllText(dialog.FileName),TradingAnalysis.JsonOptions)??throw new InvalidDataException("配置为空");db.SaveProfitContext(context);Refresh();});
  Button("导入已确认名称别名",()=>{var dialog=new OpenFileDialog{Filter="名称别名|*.json"};if(dialog.ShowDialog()!=true)return;var rows=JsonSerializer.Deserialize<ProfitAliasInput[]>(File.ReadAllText(dialog.FileName),TradingAnalysis.JsonOptions)??[];foreach(var row in rows)db.SaveAlias(row.Alias,row.ItemId,row.EvidenceId);Refresh();});
  Button("导出建议与录入模板",()=>{var dialog=new SaveFileDialog{Filter="建议报告|*.json",FileName="profit-recommendations.json"};if(dialog.ShowDialog()!=true)return;File.WriteAllText(dialog.FileName,JsonSerializer.Serialize(db.Recommend(DateTimeOffset.UtcNow,history.IsChecked==true),TradingAnalysis.JsonOptions));var dir=Path.GetDirectoryName(dialog.FileName)!;File.WriteAllText(Path.Combine(dir,"profit-context-template.json"),JsonSerializer.Serialize(ProfitAdviceContext.Empty(),TradingAnalysis.JsonOptions));File.WriteAllText(Path.Combine(dir,"canonical-items.json"),JsonSerializer.Serialize(db.Catalog(),TradingAnalysis.JsonOptions));File.WriteAllText(Path.Combine(dir,"recipe-ids.json"),JsonSerializer.Serialize(db.Recipes(),TradingAnalysis.JsonOptions));status.Text="已导出建议与模板。库存、费用和次数须附时间与来源；空字段保持未知。";});
  buttons.Children.Add(history);history.Checked+=(_,_)=>Refresh();history.Unchecked+=(_,_)=>Refresh();header.Children.Add(status);Children.Add(report);var timer=new System.Windows.Threading.DispatcherTimer{Interval=TimeSpan.FromSeconds(5)};timer.Tick+=(_,_)=>{if(IsVisible)try{Refresh();}catch(Exception e){status.Text=e.Message;}};Loaded+=(_,_)=>{Refresh();timer.Start();};Unloaded+=(_,_)=>timer.Stop();
 }
 public void Refresh() {var rows=db.Recommend(DateTimeOffset.UtcNow,history.IsChecked==true);status.Text=$"配方 {rows.Length} · 可优先关注 {rows.Count(r=>r.Status=="Suggested")} · 待补数据 {rows.Count(r=>r.Status=="NeedsData")} · 本页不会执行交易";report.Text=string.Join("\n",db.UnifiedCatalogIssues())+"\n"+Format(rows);}
 public static void Preview(string examples,string output) {
  Directory.CreateDirectory(output);using var doc=JsonDocument.Parse(File.ReadAllText(examples));var rows=doc.RootElement.GetProperty("Rows").Deserialize<ProfitAdvice[]>(TradingAnalysis.JsonOptions)!;
  var panel=new ProfitRecommendationPanel(new MarketDatabase(Path.Combine(output,"preview.sqlite")));panel.report.Text="以下为虚构测试案例，不是游戏实时报价。\n\n"+Format(rows.Where(r=>r.Status is "Suggested" or "NeedsData").Select((r,i)=>r with{Rank=i+1,ProductName="示例成品（虚构）",Contact="示例联络人",Evidence=["虚构测试数据；仅用于验证计算，不是游戏行情"]}).Take(2).ToArray());panel.status.Text="测试示例 · 只生成建议 · 无交易输入";
  panel.Width=1150;panel.Height=1200;var bg=new Border{Background=new SolidColorBrush(Color.FromRgb(15,26,35)),Child=panel,Width=1150,Height=1200};bg.Measure(new Size(1150,1200));bg.Arrange(new Rect(0,0,1150,1200));bg.UpdateLayout();
  var bitmap=new System.Windows.Media.Imaging.RenderTargetBitmap(1150,1200,96,96,PixelFormats.Pbgra32);bitmap.Render(bg);var png=new System.Windows.Media.Imaging.PngBitmapEncoder();png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));using var file=File.Create(Path.Combine(output,"profit-advice-preview.png"));png.Save(file);
 }
 public static string Format(ProfitAdvice[] rows) {
  string N(decimal? n)=>n is{} v?v.ToString("N0"):"未知";
  if(rows.Length==0)return "尚无配方。请在‘兑换分析与模拟账本’导入配方，使用确认商品ID或已登记别名。\n市场采集报价自动按商品ID与用途关联；未采集、过期或名称冲突的数据会列为待补数据。\n费用不套用默认8%，完整库存不明也不会默认没有材料。";
  string State(string s)=>s switch{"Suggested"=>"建议优先关注","Historical"=>"历史分析","Cooling"=>"等待冷却","OverBudget"=>"超出预算","LowProfit"=>"利润不足","Unavailable"=>"当前不可兑换","Blocked"=>"配方或配置待核验",_=>"待补数据"};
  return string.Join("\n\n",rows.Select(r=>{var a=r.Amounts;return $"#{r.Rank}  {r.ProductName} · {r.Contact} · {State(r.Status)}\n净利润 {N(a.ExpectedNetProfit)} · 利润率 {(a.ProfitRate is{} roi?roi.ToString("P1"):"未知")} · 综合分 {r.Score.Total:F1}\n材料配方总值 {N(a.RecipeMaterialValue)}（已有 {N(a.OwnedMaterialValue)} / 缺口 {N(a.MissingMaterialValue)}） · 兑换页合计 {N(a.ScreenMaterialValue)}\n本次补齐现金 {N(a.SupplementCash)}（{a.SupplementSource}） · 库存历史成本 {N(a.HistoricalInventoryCost)} · 额外兑换费 {N(a.ExtraExchangeFee)}\n成品单价参考 {N(a.ProductReferenceUnit)} · 兑换页原始参考 {N(a.ProductReferenceTotal)} · 保守销售总额 {N(a.GrossRevenue)}\n成交手续费 {N(a.SaleFee)} · 管理费 {N(a.ManagementFee)} · 风险预留 {N(a.RiskReserve)}\n经济成本 {N(a.EconomicCost)} · 本次现金需求 {N(a.CashRequired)} · 回款后现金差额 {N(a.CashSurplusAfterSale)} · 历史成本口径账面利润 {N(a.HistoricalCostProfit)}\n可换次数 {(r.Repeatable==true?"无限":r.RemainingExchanges?.ToString()??"未知")} · 限时刷新余次 {(r.Limited==false?"不适用（独立刷新）":r.RefreshRemaining?.ToString()??"未知")} · 可兑换时间 {r.NextEligibleAt?.ToOffset(TimeSpan.FromHours(9)).ToString("MM-dd HH:mm")??"看当前状态"}\n限时下次刷新 {r.NextRefreshAt?.ToOffset(TimeSpan.FromHours(9)).ToString("MM-dd HH:mm")??"未读到/不适用"} · 本轮结束 {r.ExpiresAt?.ToOffset(TimeSpan.FromHours(9)).ToString("MM-dd HH:mm")??"未读到/不适用"}\n排序分项：净利 {r.Score.NetProfit:F1} / 利润率 {r.Score.Roi:F1} / 时间 {r.Score.Urgency:F1} / 库存 {r.Score.Inventory:F1} / 新鲜度 {r.Score.Freshness:F1} / 次数 {r.Score.RemainingQuota:F1}\n{string.Join("；",r.Reasons)}\n依据：{string.Join("\n",r.Evidence)}";}));
 }
}
public sealed record ProfitAliasInput(string Alias,string ItemId,string EvidenceId);
