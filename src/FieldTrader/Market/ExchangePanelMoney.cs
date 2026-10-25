using System.Globalization;
using System.Text.RegularExpressions;
namespace FieldTrader.Market;

// Exchange footer fields share a row. Never cross a following field label,
// including OCR tokens that merge a number, vertical separator and next label.
public static class ExchangePanelMoney {
    private static string Clean(string text)=>Geometry.Normalize(text.Replace("①",""));
    private static decimal? Amount(string text)=>Regex.IsMatch(text,@"^\d[\d,]*$")
        &&decimal.TryParse(text,NumberStyles.AllowThousands,CultureInfo.InvariantCulture,out var n)?n:null;
    public static decimal? Read(IEnumerable<VisualToken> tokens,string label) {
        var all=tokens.Where(t=>t.Confidence >= OcrPolicy.MinimumConfidence&&t.Bounds.Valid).ToArray();var values=new List<decimal>();
        var escaped=Regex.Escape(label);
        foreach(var token in all) {
            var text=Clean(token.Label);
            var inline=Regex.Match(text,"^"+escaped+@"[:：](\d[\d,]*)(?:$|[|｜丨][\u4e00-\u9fff])");
            if(inline.Success&&Amount(inline.Groups[1].Value) is {} direct){values.Add(direct);continue;}
            if(!Regex.IsMatch(text,@"^(?:\d[\d,]*)?[|｜丨]?"+escaped+@"[:：]?$"))continue;
            var right=all.Where(t=>t.Bounds.X>=token.Bounds.X+token.Bounds.Width-.001
                &&Math.Abs(Geometry.CenterY(t.Bounds)-Geometry.CenterY(token.Bounds))<Math.Max(t.Bounds.Height,token.Bounds.Height)*.7).OrderBy(t=>t.Bounds.X);
            var numeric=new List<(VisualToken Token,decimal Value)>();
            foreach(var next in right) {
                var s=Clean(next.Label);var prefix=Regex.Match(s,@"^(\d[\d,]*)(?:$|[|｜丨]?(?:合计价格|补齐价格|产品市场价)[:：]?$)");
                if(prefix.Success&&Amount(prefix.Groups[1].Value) is {} n)numeric.Add((next,n));
                if(Regex.IsMatch(s,@"[\u4e00-\u9fff]"))break;
            }
            if(numeric.Count==0)continue;
            var last=numeric[^1];
            // Overlapping OCR passes may repeat or disagree on the same amount.
            // Agreement is deduplicated; a conflict remains unavailable.
            values.AddRange(numeric.Where(n=>Geometry.IoU(n.Token.Bounds,last.Token.Bounds)>.3||n.Token==last.Token).Select(n=>n.Value));
        }
        var unique=values.Distinct().ToArray();return unique.Length==1?unique[0]:null;
    }
}
