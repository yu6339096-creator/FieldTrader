using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Controls.Primitives;
namespace FieldTrader.Market;

public sealed class ContactCircuitOptions:StackPanel {
    public sealed class Choice {
        public string Id {get;set;}="";
        public bool Enabled {get;set;}=true;
        public string Contact {get;set;}="";
        public string Category {get;set;}="";
        public string Name {get;set;}="";
    }
    readonly List<Choice> choices=ContactExchangeCatalog.Items.Select(t=>new Choice{Id=t.Id,Contact=t.Contact,Category=t.Category,Name=t.Name}).ToList();
    readonly TextBox maximum=new(){Text="40"},profit=new(){Text="1000"},roi=new(){Text="5"},sale=new(){Text="0"},management=new(){Text="0"};
    readonly CheckBox refresh=new(){Content="迪克限时物品：可刷新时，本轮最多刷新一次；冷却时跳过，不等待",IsChecked=true};
    readonly CheckBox fees=new(){Content="已核实下面的手续费和管理费比例（仅用于按利润选择的商品）"};
    readonly DataGrid grid=new(){AutoGenerateColumns=false,CanUserAddRows=false,CanUserDeleteRows=false,Height=260};
    readonly string path;
    public ContactCircuitOptions(string settingsPath) {
        path=settingsPath;
        Children.Add(new TextBlock{Text="整轮只处理勾选商品，每个可用商品最多兑换一份。没有登记兑换商品的其他联络人会跳过。",TextWrapping=TextWrapping.Wrap});
        var buttons=new WrapPanel();var all=new Button{Content="全选"};var none=new Button{Content="全不选"};
        all.Click+=(_,_)=>{foreach(var c in choices)c.Enabled=true;grid.Items.Refresh();};none.Click+=(_,_)=>{foreach(var c in choices)c.Enabled=false;grid.Items.Refresh();};buttons.Children.Add(all);buttons.Children.Add(none);Children.Add(buttons);
        var dark=new SolidColorBrush(Color.FromRgb(16,32,44));var line=new SolidColorBrush(Color.FromRgb(50,72,88));
        grid.Background=dark;grid.Foreground=Brushes.AliceBlue;grid.RowBackground=dark;grid.AlternatingRowBackground=new SolidColorBrush(Color.FromRgb(22,40,54));grid.HorizontalGridLinesBrush=line;grid.VerticalGridLinesBrush=line;grid.RowHeight=28;
        var headerStyle=new Style(typeof(DataGridColumnHeader));headerStyle.Setters.Add(new Setter(Control.BackgroundProperty,new SolidColorBrush(Color.FromRgb(36,54,72))));headerStyle.Setters.Add(new Setter(Control.ForegroundProperty,Brushes.AliceBlue));headerStyle.Setters.Add(new Setter(Control.PaddingProperty,new Thickness(6)));grid.ColumnHeaderStyle=headerStyle;
        var cellStyle=new Style(typeof(DataGridCell));cellStyle.Setters.Add(new Setter(Control.ForegroundProperty,Brushes.AliceBlue));cellStyle.Setters.Add(new Setter(Control.PaddingProperty,new Thickness(4,2,4,2)));grid.CellStyle=cellStyle;
        grid.Columns.Add(new DataGridCheckBoxColumn{Header="兑换",Binding=new Binding(nameof(Choice.Enabled)){UpdateSourceTrigger=UpdateSourceTrigger.PropertyChanged},Width=50});
        grid.Columns.Add(new DataGridTextColumn{Header="联络人",Binding=new Binding(nameof(Choice.Contact)),IsReadOnly=true,Width=140});
        grid.Columns.Add(new DataGridTextColumn{Header="目录",Binding=new Binding(nameof(Choice.Category)),IsReadOnly=true,Width=90});
        grid.Columns.Add(new DataGridTextColumn{Header="商品",Binding=new Binding(nameof(Choice.Name)),IsReadOnly=true,Width=400});
        grid.ItemsSource=choices;Children.Add(grid);Children.Add(refresh);
        void Field(string label,TextBox box){Children.Add(new TextBlock{Text=label,Margin=new Thickness(0,4,0,0)});Children.Add(box);}
        Field("整轮最多兑换份数（1—100）",maximum);Children.Add(fees);
        Field("挂单手续费比例（%）",sale);Field("管理费比例（%）",management);Field("926 / A8 / A9 最低预计净利润",profit);Field("926 / A8 / A9 最低利润率（%）",roi);
        Children.Add(new TextBlock{Text="03头盔、M4及其他必换目标按预算处理；926、A8、A9需达到设定利润。净利润计算会扣除整套材料价值和已设置费用，补齐支出单独计入预算。",TextWrapping=TextWrapping.Wrap});
        try {
            if(File.Exists(path)&&JsonSerializer.Deserialize<ContactCircuitSettings>(File.ReadAllText(path),TradingAnalysis.JsonOptions) is {} s) {
                foreach(var c in choices)c.Enabled=s.SelectedIds.Contains(c.Id);
                maximum.Text=s.MaximumExchanges.ToString();profit.Text=s.MinimumProfit.ToString();roi.Text=(s.MinimumRoi*100).ToString();sale.Text=(s.SaleRate*100).ToString();management.Text=(s.ManagementRate*100).ToString();fees.IsChecked=s.FeesVerified;refresh.IsChecked=s.RefreshLimited;
            }
        }catch{ /* A damaged preferences file never arms trading. The explicit start still needs a budget. */ }
    }
    public ContactCircuitSettings Read(decimal totalBudget) {
        grid.CommitEdit(DataGridEditingUnit.Cell,true);grid.CommitEdit(DataGridEditingUnit.Row,true);
        if(!int.TryParse(maximum.Text,out var max)||!decimal.TryParse(profit.Text,out var min)||!decimal.TryParse(roi.Text,out var rate)||!decimal.TryParse(sale.Text,out var sf)||!decimal.TryParse(management.Text,out var mf)
            ||max is <1 or >100||min<0||rate<0||sf<0||mf<0||sf+mf>=100||!choices.Any(c=>c.Enabled))throw new InvalidOperationException("请勾选商品并填写有效数量、利润和费率");
        var settings=new ContactCircuitSettings(choices.Where(c=>c.Enabled).Select(c=>c.Id).ToArray(),totalBudget,max,refresh.IsChecked==true,min,rate/100,sf/100,mf/100,fees.IsChecked==true);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);File.WriteAllText(path,JsonSerializer.Serialize(settings,TradingAnalysis.JsonOptions));return settings;
    }
}
