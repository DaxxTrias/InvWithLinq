using System;
using System.Text;
using System.Text.RegularExpressions;

namespace InvWithLinq;

internal static class FilterPreProcessing
{
    private static readonly Regex OpenPrefixRegex = new Regex(@"OpenPrefixCount\s*\(\)\s*(==|>=|<=|>|<)\s*(\d+)", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex OpenSuffixRegex = new Regex(@"OpenSuffixCount\s*\(\)\s*(==|>=|<=|>|<)\s*(\d+)", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static string StripComments(string expr)
    {
        if (string.IsNullOrEmpty(expr)) return string.Empty;
        var sb = new StringBuilder(expr.Length);
        bool inString = false;
        bool inBlock = false;
        for (int i = 0; i < expr.Length; i++)
        {
            char c = expr[i];
            char next = i + 1 < expr.Length ? expr[i + 1] : '\0';
            if (!inString && !inBlock && c == '/' && next == '/')
            {
                while (i < expr.Length && expr[i] != '\n') i++;
                continue;
            }
            if (!inString && !inBlock && c == '/' && next == '*')
            {
                inBlock = true; i++;
                continue;
            }
            if (inBlock)
            {
                if (c == '*' && next == '/') { inBlock = false; i++; }
                continue;
            }
            if (c == '"')
            {
                bool escaped = i > 0 && expr[i - 1] == '\\';
                if (!escaped) inString = !inString;
                sb.Append(c);
                continue;
            }
            sb.Append(c);
        }
        return sb.ToString();
    }

    public static string NormalizeExpression(string expr)
    {
        if (string.IsNullOrWhiteSpace(expr)) return string.Empty;
        var nl = expr.Replace("\r\n", "\n").Replace('\r', '\n');
        var lines = nl.Split('\n');
        var sb = new StringBuilder(nl.Length + 32);
        bool firstWritten = false;
        for (int i = 0; i < lines.Length; i++)
        {
            var raw = lines[i];
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (firstWritten)
            {
                bool startsWithOp = line.StartsWith("&&") || line.StartsWith("||") || line.StartsWith(")") || line.StartsWith("]") || line.StartsWith(",") || line.StartsWith("}") || line.StartsWith(".");
                char last = sb.Length > 0 ? sb[sb.Length - 1] : '\0';
                bool prevOpener = last == '(' || last == '{' || last == '[' || last == ',' || last == '&' || last == '|';
                if (!startsWithOp && !prevOpener) sb.Append(" || "); else sb.Append(' ');
            }
            sb.Append(line);
            firstWritten = true;
        }
        return sb.ToString();
    }

    public static bool TryExtractOpenCounts(string expr, out string cleanedExpr, out int? minPrefixes, out int? minSuffixes, out int? maxPrefixes, out int? maxSuffixes)
    {
        int? localMinPrefixes = null;
        int? localMinSuffixes = null;
        int? localMaxPrefixes = null;
        int? localMaxSuffixes = null;
        var cleaned = NormalizeExpression(StripComments(expr ?? string.Empty));

        cleaned = OpenPrefixRegex.Replace(cleaned, m =>
        {
            var op = m.Groups[1].Value;
            var num = int.Parse(m.Groups[2].Value);
            MergeConstraint(ref localMinPrefixes, ref localMaxPrefixes, op, num);
            return "true";
        });
        cleaned = OpenSuffixRegex.Replace(cleaned, m =>
        {
            var op = m.Groups[1].Value;
            var num = int.Parse(m.Groups[2].Value);
            MergeConstraint(ref localMinSuffixes, ref localMaxSuffixes, op, num);
            return "true";
        });

        cleanedExpr = cleaned;
        minPrefixes = localMinPrefixes;
        minSuffixes = localMinSuffixes;
        maxPrefixes = localMaxPrefixes;
        maxSuffixes = localMaxSuffixes;
        return minPrefixes != null || minSuffixes != null || maxPrefixes != null || maxSuffixes != null;
    }

    private static void MergeConstraint(ref int? minExisting, ref int? maxExisting, string op, int value)
    {
        switch (op)
        {
            case ">":
                minExisting = minExisting is null ? value + 1 : Math.Max(minExisting.Value, value + 1);
                break;
            case ">=":
                minExisting = minExisting is null ? value : Math.Max(minExisting.Value, value);
                break;
            case "<":
                maxExisting = maxExisting is null ? value - 1 : Math.Min(maxExisting.Value, value - 1);
                break;
            case "<=":
                maxExisting = maxExisting is null ? value : Math.Min(maxExisting.Value, value);
                break;
            case "==":
                minExisting = value;
                maxExisting = value;
                break;
            default:
                minExisting = minExisting is null ? value : Math.Max(minExisting.Value, value);
                break;
        }
    }
}


