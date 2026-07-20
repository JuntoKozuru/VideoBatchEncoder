using System.Diagnostics;
using System.Text.RegularExpressions;

namespace VideoBatchEncoder;

/// <summary>
/// ffmpeg -i のstderr出力を解析してメタデータを取得する。
/// ffprobeは使用しない。
/// </summary>
internal static partial class MediaInfoReader
{
    // コンパイル時正規表現
    [GeneratedRegex(@"Duration:\s*(\d+):(\d{2}):(\d{2})\.(\d{2})")]
    private static partial Regex ReDuration();

    [GeneratedRegex(@"bitrate:\s*([\d.]+)\s*kb/s")]
    private static partial Regex ReBitrate();

    [GeneratedRegex(@"Video:\s*(\S+?)[\s,]")]
    private static partial Regex ReVideoCodec();

    [GeneratedRegex(@"Video:.*?\s(\d{2,5})x(\d{2,5})[\s,\[]")]
    private static partial Regex ReResolutionInVideo();

    [GeneratedRegex(@"(\d{2,5})x(\d{2,5})")]
    private static partial Regex ReResolutionFallback();

    [GeneratedRegex(@"([\d.]+)\s+fps")]
    private static partial Regex ReFps();

    [GeneratedRegex(@"Audio:\s*(\S+?)[\s,]")]
    private static partial Regex ReAudioCodec();

    public static MediaInfo Probe(string ffmpegPath, string filePath)
    {
        var info = new MediaInfo
        {
            Extension = Path.GetExtension(filePath).TrimStart('.')
        };

        var errFile = Path.GetTempFileName();
        var outFile = Path.GetTempFileName();
        try
        {
            using var proc = new Process();
            proc.StartInfo = new ProcessStartInfo
            {
                FileName               = ffmpegPath,
                Arguments              = $"-hide_banner -nostdin -nostats -i \"{filePath}\"",
                UseShellExecute        = false,
                CreateNoWindow         = true,
                RedirectStandardError  = true,
                RedirectStandardOutput = true,
            };
            proc.Start();
            // stdout/stderr を同時に読み切る（デッドロック防止）
            var stderrTask = proc.StandardError.ReadToEndAsync();
            proc.StandardOutput.ReadToEnd();
            proc.WaitForExit();
            var stderr = stderrTask.Result;

            if (string.IsNullOrEmpty(stderr)) return info;

            // Duration
            var m = ReDuration().Match(stderr);
            if (m.Success)
            {
                info.Duration = int.Parse(m.Groups[1].Value) * 3600.0
                              + int.Parse(m.Groups[2].Value) * 60.0
                              + int.Parse(m.Groups[3].Value)
                              + int.Parse(m.Groups[4].Value) / 100.0;
                info.Valid = true;
            }

            // Bitrate
            m = ReBitrate().Match(stderr);
            if (m.Success) info.Bitrate = double.Parse(m.Groups[1].Value,
                System.Globalization.CultureInfo.InvariantCulture) * 1000.0;

            // Video codec
            m = ReVideoCodec().Match(stderr);
            if (m.Success) info.VideoCodec = m.Groups[1].Value.TrimEnd(',');

            // Resolution（Video:行を優先、なければ fallback）
            m = ReResolutionInVideo().Match(stderr);
            if (!m.Success) m = ReResolutionFallback().Match(stderr);
            if (m.Success)
            {
                info.Width  = int.Parse(m.Groups[1].Value);
                info.Height = int.Parse(m.Groups[2].Value);
            }

            // FPS
            m = ReFps().Match(stderr);
            if (m.Success) info.FPS = double.Parse(m.Groups[1].Value,
                System.Globalization.CultureInfo.InvariantCulture);

            // Audio codec
            m = ReAudioCodec().Match(stderr);
            if (m.Success) info.AudioCodec = m.Groups[1].Value.TrimEnd(',');
        }
        catch { /* 解析失敗 → Valid=false のまま返す */ }
        finally
        {
            TryDelete(errFile);
            TryDelete(outFile);
        }
        return info;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
