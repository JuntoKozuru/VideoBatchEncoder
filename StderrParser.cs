using System.Text.RegularExpressions;

namespace VideoBatchEncoder;

/// <summary>
/// FFmpegのstderr出力を解析する。
///   - フレーム欠損範囲の抽出（decode_slice_header error 等）
///   - 警告行の集約（同種エラーをカウント）
///
/// ストリーム解析方式: 引数は「一時ログファイルのパス」であり、
/// StreamReaderで1行ずつ読み出しながら集計するため、
/// 元ログがどれだけ巨大でもメモリ消費は一定（ストリーム読み取りバッファ分のみ）に保たれる。
/// 元の「2回ループ」方式（フレーム欠損収集→警告集約）は、
/// ファイルを2回読むコストを避けるため1パスへ統合している。
/// </summary>
internal static partial class StderrParser
{
    [GeneratedRegex(@"Frame num change from \d+ to (\d+)", RegexOptions.Compiled)]
    private static partial Regex ReFrameChange();

    [GeneratedRegex(@"Last message repeated (\d+) times", RegexOptions.Compiled)]
    private static partial Regex ReRepeated();

    [GeneratedRegex(@"^\[.+?\]\s*(.+)$")]
    private static partial Regex ReCodecMsg();

    [GeneratedRegex(@"\s+\d+\s*$")]
    private static partial Regex ReTrailingNum();

    [GeneratedRegex(@"\s+from \d+ to \d+")]
    private static partial Regex ReFrameFromTo();

    private static readonly Regex ReWarn = new(
        @"(?i)(error|invalid|failed|corrupt|missing|broken|no such)",
        RegexOptions.Compiled);

    private static readonly Regex ReProgress = new(
        @"^(frame=|fps=|stream_|bitrate=|total_size=|out_time|dup_frames|drop_frames|speed=|progress=)",
        RegexOptions.Compiled);

    /// <summary>
    /// 一時ログファイルをストリームで読み解析する（推奨経路）。
    /// </summary>
    public static StderrAnalysis ParseFile(string logFilePath)
    {
        var result = new StderrAnalysis();
        if (!File.Exists(logFilePath)) return result;

        var frameEvents    = new List<int>();
        var warnCounts      = new Dictionary<string, int>(StringComparer.Ordinal);
        string? lastRepeatable = null;

        using var reader = new StreamReader(logFilePath, System.Text.Encoding.UTF8);
        string? raw;
        while ((raw = reader.ReadLine()) != null)
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;

            ProcessLine(line, frameEvents, warnCounts, ref lastRepeatable);
        }

        Finalize(result, frameEvents, warnCounts);
        return result;
    }

    /// <summary>
    /// 互換用: メモリ上の文字列をそのまま解析する（短いstderrや単体テスト向け）。
    /// 通常のエンコードパスでは ParseFile を使うこと。
    /// </summary>
    public static StderrAnalysis Parse(string stderr)
    {
        var result = new StderrAnalysis();
        if (string.IsNullOrEmpty(stderr)) return result;

        var frameEvents = new List<int>();
        var warnCounts   = new Dictionary<string, int>(StringComparer.Ordinal);
        string? lastRepeatable = null;

        foreach (var raw in stderr.Split('\n', StringSplitOptions.None))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            ProcessLine(line, frameEvents, warnCounts, ref lastRepeatable);
        }

        Finalize(result, frameEvents, warnCounts);
        return result;
    }

    // ────────────────────────────────────────────────────────
    //  1行分の処理（フレーム欠損収集＋警告集約を同時に行う）
    // ────────────────────────────────────────────────────────
    private static void ProcessLine(
        string line,
        List<int> frameEvents,
        Dictionary<string, int> warnCounts,
        ref string? lastRepeatable)
    {
        // ── フレーム欠損収集 ──────────────────────────────
        var mf = ReFrameChange().Match(line);
        if (mf.Success)
        {
            frameEvents.Add(int.Parse(mf.Groups[1].Value));
            lastRepeatable = line;
        }
        else
        {
            var mr = ReRepeated().Match(line);
            if (mr.Success)
            {
                if (lastRepeatable != null && ReFrameChange().IsMatch(lastRepeatable))
                {
                    int rep = int.Parse(mr.Groups[1].Value);
                    for (int k = 0; k < rep; k++) frameEvents.Add(-1); // 不明フレーム
                }
            }
            else
            {
                lastRepeatable = line;
            }
        }

        // ── 警告行集約 ────────────────────────────────────
        if (ReProgress.IsMatch(line))   return;
        if (ReRepeated().IsMatch(line)) return;
        if (!ReWarn.IsMatch(line))      return;

        var m = ReCodecMsg().Match(line);
        string key = m.Success ? m.Groups[1].Value.Trim() : line;
        key = ReTrailingNum().Replace(key, "");
        key = ReFrameFromTo().Replace(key, " from X to Y");

        warnCounts[key] = warnCounts.TryGetValue(key, out int c) ? c + 1 : 1;
    }

    // ────────────────────────────────────────────────────────
    //  集計結果から StderrAnalysis を組み立てる
    // ────────────────────────────────────────────────────────
    private static void Finalize(
        StderrAnalysis result,
        List<int> frameEvents,
        Dictionary<string, int> warnCounts)
    {
        if (frameEvents.Count > 0)
        {
            result.HasWarn = true;
            var known = frameEvents.Where(f => f >= 0).Distinct().OrderBy(f => f).ToList();

            if (known.Count > 0)
            {
                int gStart = known[0], gPrev = known[0];
                for (int ki = 1; ki < known.Count; ki++)
                {
                    int cur = known[ki];
                    if (cur - gPrev <= 50) { gPrev = cur; }
                    else
                    {
                        result.DefectRanges.Add(gStart == gPrev
                            ? $"frame {gStart}"
                            : $"frame {gStart} - {gPrev}");
                        gStart = cur; gPrev = cur;
                    }
                }
                result.DefectRanges.Add(gStart == gPrev
                    ? $"frame {gStart}"
                    : $"frame {gStart} - {gPrev}");
            }
            else
            {
                result.DefectRanges.Add("不明（繰り返し行のみ）");
            }
        }

        foreach (var kv in warnCounts.OrderBy(x => x.Key))
        {
            result.WarnLines.Add(kv.Value == 1
                ? $"    {kv.Key}"
                : $"    {kv.Key}  (x{kv.Value})");
            result.HasWarn = true;
        }
    }
}
