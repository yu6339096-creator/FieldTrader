namespace FieldTrader.Market;

// Shared recognition aliases, independent of trading executors and UI projects.
public static class ExchangeItemNames {
 public static bool Matches(string raw,string expected,bool lettersOnly=false) {
  string N(string value){var normalized=Geometry.Normalize(value);return lettersOnly?new string(normalized.Where(char.IsLetterOrDigit).ToArray()):normalized;}
  if(N(raw)==N(expected))return true;
  string[] aliases=N(expected)==N("100D战地医疗包")?["1000战地医疗包","100D战地包","1000战地包"]:
   N(expected)==N("03重型战术头盔")?["O3重型战术头盔"]:
   N(expected)==N(".338 AP")?[".338口径 AP子弹",".338毫米 AP子弹",".338 AP子弹"]:[];
  return aliases.Any(alias=>N(alias)==N(raw));
 }
}
