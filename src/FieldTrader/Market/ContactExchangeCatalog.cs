using FieldTrader.Navigation;
namespace FieldTrader.Market;

public sealed record ContactExchangeTarget(string Id,string Contact,string Name,string Category,
    Dictionary<string,int>? Recipe,string? ItemClass=null,bool Mandatory=true,int? CooldownSeconds=null);
public sealed record ContactCircuitSettings(string[] SelectedIds,decimal TotalBudget,int MaximumExchanges=24,
    bool RefreshLimited=true,decimal MinimumProfit=1000,decimal MinimumRoi=.05m,
    decimal SaleRate=0,decimal ManagementRate=0,bool FeesVerified=false);
public static class ContactExchangeCatalog {
    public const string Limited="限时物品";
    public static readonly ContactExchangeTarget[] Items = Build();
    static ContactExchangeTarget[] Build() {
        var result=LarryTargets.Items.Select(i=>new ContactExchangeTarget(i.Id,LarryPageClassifier.Contact,i.Name,i.Category,i.ExpectedRecipe,LarryTargets.ItemClass(i.Name))).ToList();
        foreach(var i in DickTargets.Items.Where(i=>i.Category is "头盔" or "弹挂")) {
            Dictionary<string,int>? recipe=i.Id switch {
                "o3"=>new(){["一袋水泥"]=3,["集邮册"]=1,["维修手册"]=1},
                "m4"=>new(){["杀虫剂"]=1,["笔记本电脑"]=1},
                "a8"=>new(){["汽车蓄电池"]=1,["液压扳手"]=1},
                "a9"=>new(){["维塞尔日记"]=2,["手持补光灯"]=1},
                "sh12"=>new(){["伞绳"]=1},_=>null};
            result.Add(new("dick-"+i.Id,DickPageClassifier.Contact,i.Name,i.Category,recipe,i.ItemClass,i.Mandatory||i.Id=="sh12",i.CooldownSeconds));
        }
        result.AddRange(DickTargets.Items.Select(i=>new ContactExchangeTarget("limited-"+i.Id,DickPageClassifier.Contact,i.Name,Limited,null,i.ItemClass)));
        result.AddRange(RandallTargets.Items.Select(i=>new ContactExchangeTarget("randall-"+i.Id,RandallPageClassifier.Contact,i.Name,i.Category,
            new(){[i.Id=="dk8"?"芯片处理器":"PSU电源"]=1},CooldownSeconds:i.CooldownSeconds)));
        return result.ToArray();
    }
    public static string Key(string text)=>Geometry.Normalize(text);
    public static string Material(string text)=>Key(text) switch {
        "水泥" or "一袋水泥"=>"一袋水泥", "便签" or "一叠便签"=>"一叠便签",
        "电子宠物蛋" or "宠物蛋"=>"宠物蛋",_=>text};
    public static bool Matches(string raw,string expected)=>AutoBuyRules.NameMatches(raw,expected)
        ||expected==Limited&&Key(raw).TrimStart('①')==Key(Limited)
        ||Key(expected)==Key("03重型战术头盔")&&Key(raw)==Key("O3重型战术头盔")
        ||Key(expected)==Key(".338 AP")&&new[]{".338口径 AP子弹",".338毫米 AP子弹",".338 AP子弹"}.Any(s=>Key(raw)==Key(s));
}
