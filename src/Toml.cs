using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace FortItemTracker;

/// <summary>
/// The part of TOML that config.toml uses: [sections], key = value with strings, numbers,
/// booleans and (multi-line) arrays, and # comments.
/// </summary>
static class Toml
{
    public static Dictionary<string, Dictionary<string, object>> Parse(string text)
    {
        var tables = new Dictionary<string, Dictionary<string, object>>();
        var current = tables[""] = new Dictionary<string, object>();
        var lines = text.Replace("\r\n", "\n").Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            int lineNo = i + 1;
            string line = StripComment(lines[i]).Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith('['))
            {
                if (line.StartsWith("[[") || !line.EndsWith(']')) throw Error(lineNo, "unsupported table header");
                string name = line[1..^1].Trim();
                current = tables.TryGetValue(name, out var t) ? t : tables[name] = new Dictionary<string, object>();
                continue;
            }
            int eq = line.IndexOf('=');
            if (eq <= 0) throw Error(lineNo, "expected key = value");
            string key = line[..eq].Trim().Trim('"');
            string value = line[(eq + 1)..].Trim();
            while (value.StartsWith('[') && Depth(value) > 0 && i + 1 < lines.Length)
                value += " " + StripComment(lines[++i]).Trim();
            int pos = 0;
            current[key] = ParseValue(value, ref pos, lineNo);
            SkipSpace(value, ref pos);
            if (pos != value.Length) throw Error(lineNo, "unexpected text after the value");
        }
        return tables;
    }

    /// <summary>
    /// Sets one value in a config file, keeping its comments and layout. <paramref name="literal"/> is
    /// already TOML (e.g. <c>true</c> or <c>"text"</c>).
    /// </summary>
    public static void SetValue(string path, string section, string key, string literal)
    {
        string original = File.Exists(path) ? File.ReadAllText(path) : "";
        string newline = original.Contains("\r\n") || original.Length == 0 ? "\r\n" : "\n";
        var lines = original.Replace("\r\n", "\n").Split('\n').ToList();
        if (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);  // the final newline
        int start = -1, end = lines.Count;
        for (int i = 0; i < lines.Count; i++)
        {
            string t = StripComment(lines[i]).Trim();
            if (!t.StartsWith('[') || !t.EndsWith(']')) continue;
            if (start >= 0) { end = i; break; }
            if (t[1..^1].Trim() == section) start = i;
        }
        var keyLine = new Regex(@"^(\s*" + Regex.Escape(key) + @"\s*=\s*)");
        if (start < 0)
        {
            if (lines.Count > 0 && lines[^1].Trim().Length > 0) lines.Add("");
            lines.Add($"[{section}]");
            lines.Add($"{key} = {literal}");
        }
        else
        {
            int found = -1;
            for (int i = start + 1; i < end && found < 0; i++)
                if (keyLine.IsMatch(lines[i])) found = i;
            if (found >= 0)
            {
                string line = lines[found];
                string prefix = keyLine.Match(line).Value;
                string rest = line[prefix.Length..];
                string valuePart = StripComment(rest);
                string tail = rest[valuePart.Length..];  // the comment, if any
                string old = valuePart.TrimEnd();
                string gap = valuePart[old.Length..];
                if (tail.Length > 0)  // keep the comment in its column when the value changes length
                    gap = new string(' ', Math.Max(1, gap.Length + old.Length - literal.Length));
                lines[found] = prefix + literal + (tail.Length > 0 ? gap + tail : "");
            }
            else
            {
                int at = end;
                while (at > start + 1 && lines[at - 1].Trim().Length == 0) at--;
                lines.Insert(at, $"{key} = {literal}");
            }
        }
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, string.Join(newline, lines) + newline, new UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
    }

    static FormatException Error(int line, string message) => new($"line {line}: {message}");

    static string StripComment(string line)
    {
        char quote = '\0';
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (quote != '\0')
            {
                if (c == '\\' && quote == '"') i++;
                else if (c == quote) quote = '\0';
            }
            else if (c is '"' or '\'') quote = c;
            else if (c == '#') return line[..i];
        }
        return line;
    }

    static int Depth(string text)
    {
        int depth = 0;
        char quote = '\0';
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (quote != '\0')
            {
                if (c == '\\' && quote == '"') i++;
                else if (c == quote) quote = '\0';
            }
            else if (c is '"' or '\'') quote = c;
            else if (c == '[') depth++;
            else if (c == ']') depth--;
        }
        return depth;
    }

    static void SkipSpace(string s, ref int pos)
    {
        while (pos < s.Length && char.IsWhiteSpace(s[pos])) pos++;
    }

    static object ParseValue(string s, ref int pos, int line)
    {
        SkipSpace(s, ref pos);
        if (pos >= s.Length) throw Error(line, "missing value");
        char c = s[pos];
        if (c == '"') return BasicString(s, ref pos, line);
        if (c == '\'')
        {
            int close = s.IndexOf('\'', pos + 1);
            if (close < 0) throw Error(line, "unterminated string");
            string lit = s[(pos + 1)..close];
            pos = close + 1;
            return lit;
        }
        if (c == '[')
        {
            pos++;
            var list = new List<object>();
            while (true)
            {
                SkipSpace(s, ref pos);
                if (pos < s.Length && s[pos] == ']') { pos++; return list; }
                list.Add(ParseValue(s, ref pos, line));
                SkipSpace(s, ref pos);
                if (pos < s.Length && s[pos] == ',') { pos++; continue; }
                if (pos < s.Length && s[pos] == ']') { pos++; return list; }
                throw Error(line, "expected , or ] in array");
            }
        }
        var word = Regex.Match(s[pos..], @"^[A-Za-z0-9_.+\-]+").Value;
        pos += word.Length;
        if (word == "true") return true;
        if (word == "false") return false;
        string num = word.Replace("_", "");
        if (Regex.IsMatch(num, @"^[+-]?\d+$") && long.TryParse(num, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long l))
            return l;
        if (double.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out double d)) return d;
        throw Error(line, $"can't read value '{(word.Length > 0 ? word : s[pos..])}'");
    }

    static string BasicString(string s, ref int pos, int line)
    {
        var sb = new StringBuilder();
        for (int i = pos + 1; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '"') { pos = i + 1; return sb.ToString(); }
            if (c != '\\') { sb.Append(c); continue; }
            if (++i >= s.Length) break;
            switch (s[i])
            {
                case 'n': sb.Append('\n'); break;
                case 't': sb.Append('\t'); break;
                case 'r': sb.Append('\r'); break;
                case 'u' when i + 4 < s.Length:
                    sb.Append((char)Convert.ToInt32(s.Substring(i + 1, 4), 16));
                    i += 4;
                    break;
                default: sb.Append(s[i]); break;
            }
        }
        throw Error(line, "unterminated string");
    }
}
