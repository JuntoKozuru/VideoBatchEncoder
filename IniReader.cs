namespace VideoBatchEncoder;

/// <summary>
/// シンプルなINIファイルリーダー。
/// セクション・キーは大文字小文字を区別しない。
/// ; または # で始まる行はコメント。
/// </summary>
internal sealed class IniReader
{
    private readonly Dictionary<string, Dictionary<string, string>> _data = new(StringComparer.OrdinalIgnoreCase);

    public static IniReader Load(string path)
    {
        var reader = new IniReader();
        if (!File.Exists(path)) return reader;

        string currentSection = string.Empty;
        foreach (var rawLine in File.ReadLines(path, System.Text.Encoding.UTF8))
        {
            var line = rawLine.Trim();
            if (string.IsNullOrEmpty(line) || line.StartsWith(';') || line.StartsWith('#'))
                continue;

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                currentSection = line[1..^1].Trim();
                if (!reader._data.ContainsKey(currentSection))
                    reader._data[currentSection] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                continue;
            }

            var eq = line.IndexOf('=');
            if (eq > 0 && !string.IsNullOrEmpty(currentSection))
            {
                var key = line[..eq].Trim();
                var val = line[(eq + 1)..].Trim();
                reader._data[currentSection][key] = val;
            }
        }
        return reader;
    }

    public string Get(string section, string key, string defaultValue)
    {
        if (_data.TryGetValue(section, out var sec) &&
            sec.TryGetValue(key, out var val) &&
            !string.IsNullOrWhiteSpace(val))
            return val;
        return defaultValue;
    }

    public double GetDouble(string section, string key, double defaultValue)
    {
        var s = Get(section, key, string.Empty);
        return double.TryParse(s, System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : defaultValue;
    }

    public int GetInt(string section, string key, int defaultValue)
    {
        var s = Get(section, key, string.Empty);
        return int.TryParse(s, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var i) ? i : defaultValue;
    }

    public bool GetBool(string section, string key, bool defaultValue)
    {
        var s = Get(section, key, string.Empty).Trim().ToLowerInvariant();
        if (s.Length == 0) return defaultValue;
        return s is "true" or "1" or "yes" or "on";
    }

    /// <summary>
    /// セクション内の指定プレフィックスに一致するキー一覧を取得する。
    /// 例: GetKeysWithPrefix("retry", "retry1_") → ["retry1_preset", "retry1_options", ...]
    /// </summary>
    public List<string> GetKeysWithPrefix(string section, string prefix)
    {
        var result = new List<string>();
        if (_data.TryGetValue(section, out var sec))
        {
            foreach (var key in sec.Keys)
                if (key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    result.Add(key);
        }
        return result;
    }

    public string[] GetArray(string section, string key, string[] defaultValue)
    {
        var s = Get(section, key, string.Empty);
        if (string.IsNullOrWhiteSpace(s)) return defaultValue;
        return s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(x => x.ToLowerInvariant())
                .ToArray();
    }
}
