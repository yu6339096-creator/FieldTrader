using FieldTrader.Market;
namespace FieldTrader.Navigation;

// Session-local intent memory. It never manufactures an observation or a success result.
public sealed class OcrNavigationContext {
    private PlannedAction? issued;
    private CapturedScreen? origin;
    public void Issued(PlannedAction action,CapturedScreen screen){issued=action;origin=screen;}
    public PageObservation Reconcile(PageObservation current) {
        if(current.Page!="Unknown"||current.Errors.Length>0||issued is null||origin is null)return current;
        var s=current.Screen;var age=s.Frame.CapturedAt-origin.Frame.CapturedAt;
        if(s.Frame.Id==origin.Frame.Id||age<=TimeSpan.Zero||age>TimeSpan.FromSeconds(30)
            ||s.WindowHandle!=origin.WindowHandle||s.Width!=origin.Width||s.Height!=origin.Height
            ||s.ScreenX!=origin.ScreenX||s.ScreenY!=origin.ScreenY||s.Live!=origin.Live)return current;
        var text=current.Vision.Texts.Where(t=>t.Confidence >= OcrPolicy.MinimumConfidence&&t.Bounds.Valid).ToArray();
        bool Has(string value)=>text.Any(t=>Geometry.Normalize(t.Label)==Geometry.Normalize(value));
        // Do not recover through an unidentified popup, error, or conflicting modal text.
        if(text.Any(t=>new[]{"验证码","安全验证","失败","异常","已集齐全部兑换道具"}.Any(t.Label.Contains))
            ||current.Vision.Regions.Any(r=>r.Confidence>=.6&&r.Label is "confirm_dialog" or "result_region"))return current;
        var expected=issued.Step.ExpectedAfter;
        bool modalWords=Has("购买数量")||Has("道具兑换");
        bool supported=expected switch {
            "Market"=>!modalWords&&Has("购买")&&Has("我的交易")&&Has("输入物品名称搜索"),
            "Purchase"=>!Has("道具兑换")&&Has("购买数量")&&Has("平均单价")&&Has("合计"),
            "Exchange"=>!Has("购买数量")&&Has("道具兑换")&&Has("兑换列表")&&Has("需要提交以下道具"),
            "ContactShop"=>!modalWords&&NavigationSceneAdapter.Contacts.Any(c=>text.Any(t=>t.Bounds.Y<.12&&Geometry.Normalize(t.Label)==Geometry.Normalize(c)))
                &&text.Count(t=>t.Bounds.Y>=.115&&t.Bounds.Y<.16&&new[]{"医疗包","伤害救治","手雷","头盔","防弹衣","弹挂","限时物品","背包","耳机","头套面罩"}.Contains(t.Label))>=2,
            "Warehouse"=>!modalWords&&Has("装备")&&text.Any(t=>t.Label.Contains("仓库容量")),
            _=>false
        };
        if(!supported)return current;
        return current with{Page=expected,Confidence=.95,Sources=[..current.Sources??[],"ClickContext:"+issued.Step.Id+"; fresh OCR anchors verified; YOLO optional"]};
    }
}
