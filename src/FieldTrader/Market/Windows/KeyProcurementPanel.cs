using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FieldTrader.Navigation;
namespace FieldTrader.Market;
public sealed class KeyProcurementPanel : UserControl {
 readonly CheckBox enabled=new(){Content="启用钥匙按需采购",FontSize=18,Margin=new Thickness(0,8,0,12)};
 readonly Dictionary<string,(CheckBox Selected,TextBox Quantity)> rows=[];
 readonly TextBlock status=new(){TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,10,0,0)};
 readonly string file;
 KeyProcurementSettings settings;
 public KeyProcurementPanel(string path) {
  file=path;settings=KeySettingsStore.Load(file);Foreground=Brushes.AliceBlue;
  var body=new StackPanel{Margin=new Thickness(16)};Content=new ScrollViewer{Content=body,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};
  body.Children.Add(new TextBlock{Text="钥匙采购清单",FontSize=23});body.Children.Add(enabled);enabled.IsChecked=settings.Enabled;
  body.Children.Add(new TextBlock{Text="仅采购勾选的钥匙，按各自数量补足；未勾选不购买。默认全部关闭。",TextWrapping=TextWrapping.Wrap});
  body.Children.Add(new TextBlock{Text=$"低价判断：比较最近 {settings.ObservationWindowSeconds/60.0:g} 分钟、至少 {settings.MinimumObservations} 次有效报价中的最低单价，并核对剩余使用次数。历史截图不触发购买。",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,6,0,6)});
  body.Children.Add(new TextBlock{Text="当前保存采购偏好与报价判断；真实购买尚未接通。",Foreground=Brushes.LightGoldenrodYellow,Margin=new Thickness(0,0,0,8)});
  foreach(var category in KeyMarketTargets.Categories) {
   body.Children.Add(new TextBlock{Text=category,FontSize=17,Margin=new Thickness(0,12,0,5),Foreground=Brushes.LightSkyBlue});
   foreach(var item in KeyMarketTargets.Items.Where(i=>i.Category==category)) {
    var row=new Grid{Margin=new Thickness(0,2,0,2)};row.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});row.ColumnDefinitions.Add(new(){Width=new GridLength(75)});row.ColumnDefinitions.Add(new(){Width=new GridLength(35)});
    var choice=settings.Choice(item.Id);var check=new CheckBox{Content=item.Name,IsChecked=choice.Selected,VerticalAlignment=VerticalAlignment.Center};var quantity=new TextBox{Text=choice.Quantity.ToString(),Width=65,HorizontalContentAlignment=HorizontalAlignment.Center};
    row.Children.Add(check);Grid.SetColumn(quantity,1);row.Children.Add(quantity);var unit=new TextBlock{Text="把",Margin=new Thickness(6,0,0,0),VerticalAlignment=VerticalAlignment.Center};Grid.SetColumn(unit,2);row.Children.Add(unit);body.Children.Add(row);rows[item.Id]=(check,quantity);
   }
  }
  var buttons=new WrapPanel{Margin=new Thickness(0,16,0,0)};body.Children.Add(buttons);
  var save=new Button{Content="保存采购清单",Padding=new Thickness(15,6,15,6)};buttons.Children.Add(save);save.Click+=(_,_)=>{try{settings=Draft();KeySettingsStore.Save(file,settings);status.Text="已保存；只会处理清单中勾选的钥匙。真实购买尚未接通。";}catch(Exception e){status.Text=e.Message;}};
  var clear=new Button{Content="关闭并取消全部",Margin=new Thickness(10,0,0,0),Padding=new Thickness(15,6,15,6)};buttons.Children.Add(clear);clear.Click+=(_,_)=>{enabled.IsChecked=false;foreach(var row in rows.Values)row.Selected.IsChecked=false;status.Text="已取消选择，请点击保存。";};body.Children.Add(status);
 }
 public KeyProcurementSettings Draft() {
  var value=settings with{Enabled=enabled.IsChecked==true,Choices=rows.Select(r=>new KeyChoice(r.Key,r.Value.Selected.IsChecked==true,int.TryParse(r.Value.Quantity.Text,out var n)&&n>0?n:throw new InvalidDataException("数量必须为正整数"))).ToArray()};value.Validate();return value;
 }
 public static void Preview(string output) {
  Directory.CreateDirectory(output);var panel=new KeyProcurementPanel(Path.Combine(output,"preview-settings.json"));
  if(panel.Draft().Enabled||panel.Draft().Choices.Any(c=>c.Selected))throw new InvalidDataException("Defaults must be off");
  var first=KeyMarketTargets.Items[0].Id;panel.rows[first].Selected.IsChecked=true;panel.rows[first].Quantity.Text="2";panel.enabled.IsChecked=true;
  var draft=panel.Draft();if(draft.Choice(first).Quantity!=2||!draft.Choice(first).Selected)throw new InvalidDataException("Selection failed");
  KeySettingsStore.Save(Path.Combine(output,"selection-roundtrip.json"),draft);if(KeySettingsStore.Load(Path.Combine(output,"selection-roundtrip.json")).Choice(first)!=draft.Choice(first))throw new InvalidDataException("Roundtrip failed");
  panel.rows[first].Selected.IsChecked=false;panel.rows[first].Quantity.Text="1";panel.enabled.IsChecked=false;KeySettingsStore.Save(Path.Combine(output,"key-procurement-default.json"),panel.Draft());
  panel.Width=900;panel.Height=1750;panel.Measure(new Size(900,1750));panel.Arrange(new Rect(0,0,900,1750));panel.UpdateLayout();
  var bitmap=new RenderTargetBitmap(900,1750,96,96,PixelFormats.Pbgra32);var bg=new Border{Background=new SolidColorBrush(Color.FromRgb(15,26,35)),Child=panel,Width=900,Height=1750};bg.Measure(new Size(900,1750));bg.Arrange(new Rect(0,0,900,1750));bg.UpdateLayout();bitmap.Render(bg);
  var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using var stream=File.Create(Path.Combine(output,"key-settings-preview.png"));encoder.Save(stream);
 }
}
