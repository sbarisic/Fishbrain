using System.Text.RegularExpressions;

namespace Fishbrain;

/// <summary>Example host authorization. It verifies a proposed transaction; it never chooses one.</summary>
public static class DemoAuthorization
{
    public static bool Allow(ReplyRequest request, GameToolInvocation invocation)
    {
        if (invocation.ToolName is not ("BUY" or "SELL")) return true;
        var text = request.Messages[^1].Text.Replace('’', '\'');
        var verb = invocation.ToolName == "BUY" ? "(?:buy|purchase|sell\\s+me)" : "sell";
        if (!Regex.IsMatch(text, "^\\s*(?:please\\s+|(?:I want to|I'd like to|I would like to)\\s+)?" + verb + "\\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) return false;
        if (!invocation.Arguments.TryGetValue("ITEM", out var item) || !invocation.Arguments.TryGetValue("QUANTITY", out var quantity)) return false;
        // Require the quantity and entity together so a multi-clause reply cannot swap arguments.
        var words = new[] { "zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten" };
        if (!int.TryParse(quantity, out var number) || number <= 0) return false;
        var count = quantity == "1" ? "(?:1|one|a|an)" : number < words.Length ? "(?:" + quantity + "|" + words[number] + ")" : Regex.Escape(quantity);
        return Regex.IsMatch(text, verb + "\\s+" + count + "\\s+" + Regex.Escape(item) + "s?\\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}
