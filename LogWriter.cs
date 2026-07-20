using System.Text;

namespace VideoBatchEncoder;

/// <summary>
/// ログをコンソールとファイルに同時出力する。
/// ファイルは UTF-8 BOM 付き。
/// </summary>
internal sealed class LogWriter : IDisposable
{
    private readonly StreamWriter _writer;

    public string LogPath { get; }

    private static readonly string LineEqual = new('=', 70);
    private static readonly string LineDash  = new('-', 70);

    public LogWriter(string logPath)
    {
        LogPath = logPath;
        _writer = new StreamWriter(logPath, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        _writer.AutoFlush = true;
    }

    public void WriteLine(string text = "")
    {
        Console.WriteLine(text);
        _writer.WriteLine(text);
    }

    public void WriteLineEqual() => WriteLine(LineEqual);
    public void WriteLineDash()  => WriteLine(LineDash);

    // ── セッションヘッダ ──────────────────────────────────
    public void WriteSessionHeader(AppConfig cfg, string gpuName, string processMode, DateTime now, bool isResumed = false)
    {
        WriteLineEqual();
        WriteLine("  ENCODE SUMMARY REPORT");
        WriteLineEqual();
        WriteLine($"実行日時      : {now:yyyy/MM/dd HH:mm:ss}");
        WriteLine($"処理モード    : {processMode}");
        WriteLine($"実行環境      : {gpuName}");
        if (isResumed)
            WriteLine("セッション    : 前回の未完了セッションからレジュームしました");
        WriteLine("エンコード設定:");

        var videoLine = $"  - Video: {cfg.VideoCodec}";
        if (CodecFamily.SupportsPreset(cfg.VideoCodec)) videoLine += $" -preset {cfg.VideoPreset}";
        if (CodecFamily.SupportsTune(cfg.VideoCodec) && !string.IsNullOrEmpty(cfg.VideoTune))
            videoLine += $" -tune {cfg.VideoTune}";
        videoLine += $" {CodecFamily.QualityOption(cfg.VideoCodec)} {cfg.VideoCQ}";
        if (CodecFamily.SupportsRateControl(cfg.VideoCodec)) videoLine += $" -rc {cfg.VideoRateControl}";
        if (!string.IsNullOrEmpty(cfg.VideoPixelFormat)) videoLine += $" -pix_fmt {cfg.VideoPixelFormat}";
        WriteLine(videoLine);

        WriteLine($"  - Audio: {cfg.AudioCodec} -b:a {cfg.AudioBitrate}");
        if (!string.IsNullOrEmpty(cfg.VideoMovFlags))
            WriteLine($"  - MovFlags: {cfg.VideoMovFlags}");
        WriteLine($"  - Duration許容差: {cfg.DurationTolerance}秒");

        if (cfg.RetryMax > 0)
        {
            WriteLine($"  - リトライ: 最大{cfg.RetryMax}回（待機{cfg.RetryDelay}秒）");
            for (int n = 0; n < cfg.RetryOverrides.Count && n < cfg.RetryMax; n++)
            {
                var ov = cfg.RetryOverrides[n];
                if (ov.Preset != null || ov.Options != null || ov.Codec != null)
                {
                    var parts = new List<string>();
                    if (ov.Codec   != null) parts.Add($"Codec={ov.Codec}");
                    if (ov.Preset  != null) parts.Add($"Preset={ov.Preset}");
                    if (ov.Options != null) parts.Add($"Options={ov.Options}");
                    WriteLine($"      {n + 1}回目: {string.Join(", ", parts)}");
                }
            }
        }
        else
        {
            WriteLine("  - リトライ: 無効");
        }

        if (cfg.SecondPassEnabled)
            WriteLine($"  - 第2パス（WARN再チャレンジ）: 有効（最大{cfg.SecondPassMaxRetry}回）");
        else
            WriteLine("  - 第2パス（WARN再チャレンジ）: 無効");

        WriteLineDash();
    }

    // ── 全件サマリ表 ─────────────────────────────────────
    public void WriteSummaryTable(IReadOnlyList<FileResult> results)
    {
        WriteLine();
        WriteLine("  全件サマリ");
        WriteLineDash();
        WriteLine($" {"No",4}  {"Filename",-36} {"Result",-6}  {"Time",-12}  {"Retry",-5}  Size(Out)");
        WriteLine($" {"---",4}  {new string('-', 36)} {"------",-6}  {"----------",-12}  {"-----",-5}  ---------");

        foreach (var r in results)
        {
            var timeStr  = r.Elapsed.HasValue ? FormatElapsed(r.Elapsed.Value) : "-";
            var sizeStr  = r.OutSize > 0 ? FormatSize(r.OutSize) : "-";
            var name     = r.FileName.Length > 36 ? r.FileName[..33] + "..." : r.FileName;
            var retryStr = r.RetryCount > 0 ? $"{r.RetryCount}回" : "-";
            var res      = ResultLabel(r.Result);
            WriteLine($" {r.Index,4}  {name,-36} {res,-6}  {timeStr,-12}  {retryStr,-5}  {sizeStr}");
        }
        WriteLineDash();
    }

    // ── ファイル詳細レポート ──────────────────────────────
    public void WriteFileReport(FileResult r, bool forceDetail)
    {
        bool showDetail = forceDetail || r.Result != EncodeResult.Pass;
        var  resultStr  = ResultLabel(r.Result);

        WriteLine();
        WriteLine($"[FILE] {r.FileName}");
        WriteLineDash();

        if (showDetail)
        {
            var elapsed = r.Elapsed.HasValue ? FormatElapsed(r.Elapsed.Value) : "-";
            WriteLine($"Input Path      : {r.InPath}");
            WriteLine($"Output Path     : {r.OutPath}");
            WriteLine($"Processing Time : {elapsed}");
            WriteLine($"Command         : {r.Command ?? "-"}");
            WriteLine($"ExitCode        : {r.ExitCode?.ToString() ?? "-"}");
            WriteLineDash();

            if (r.OutMeta != null)
            {
                // 入出力比較表
                var inRes  = r.InMeta.Width  > 0 ? $"{r.InMeta.Width}x{r.InMeta.Height}"   : "N/A";
                var outRes = r.OutMeta.Width > 0 ? $"{r.OutMeta.Width}x{r.OutMeta.Height}" : "N/A";
                var inSize = File.Exists(r.InPath) ? new FileInfo(r.InPath).Length : 0L;

                WriteLine($" {"ITEM",-15} {"INPUT",-25} ->  OUTPUT");
                WriteLineDash();
                WriteLine($" {"Extension",-15} {r.InMeta.Extension,-25} ->  {r.OutMeta.Extension}");
                WriteLine($" {"Duration",-15} {FormatDuration(r.InMeta.Duration),-25} ->  {FormatDuration(r.OutMeta.Duration)}");
                WriteLine($" {"Video",-15} {r.InMeta.VideoCodec,-25} ->  {r.OutMeta.VideoCodec}");
                WriteLine($" {"Resolution",-15} {inRes,-25} ->  {outRes}");
                var inFps = r.InMeta.FPS.ToString("F2");
                WriteLine($" {"FPS",-15} {inFps,-25} ->  {r.OutMeta.FPS:F2}");
                WriteLine($" {"Audio",-15} {r.InMeta.AudioCodec,-25} ->  {r.OutMeta.AudioCodec}");
                WriteLine($" {"Bitrate",-15} {FormatBitrate(r.InMeta.Bitrate),-25} ->  {FormatBitrate(r.OutMeta.Bitrate)}");
                WriteLine($" {"Size",-15} {FormatSize(inSize),-25} ->  {FormatSize(r.OutSize)}");
            }
            else
            {
                // 入力のみ
                var inRes  = r.InMeta.Width > 0 ? $"{r.InMeta.Width}x{r.InMeta.Height}" : "N/A";
                var inSize = File.Exists(r.InPath) ? new FileInfo(r.InPath).Length : 0L;

                WriteLine($" {"ITEM",-15} INPUT");
                WriteLineDash();
                WriteLine($" {"Extension",-15} {r.InMeta.Extension}");
                WriteLine($" {"Duration",-15} {FormatDuration(r.InMeta.Duration)}");
                WriteLine($" {"Video",-15} {r.InMeta.VideoCodec}");
                WriteLine($" {"Resolution",-15} {inRes}");
                WriteLine($" {"FPS",-15} {r.InMeta.FPS:F2}");
                WriteLine($" {"Audio",-15} {r.InMeta.AudioCodec}");
                WriteLine($" {"Bitrate",-15} {FormatBitrate(r.InMeta.Bitrate)}");
                WriteLine($" {"Size",-15} {FormatSize(inSize)}");
            }
        }

        WriteLineDash();

        if (r.RetryLog.Count > 0)
        {
            WriteLine("  リトライ履歴:");
            foreach (var rl in r.RetryLog) WriteLine($"    - {rl}");
        }

        if (r.OriginalResult.HasValue && r.SecondPassAttempts > 0)
        {
            WriteLine($"  ※ 第2パス再チャレンジ: 元のステータス={ResultLabel(r.OriginalResult.Value)} → {r.SecondPassAttempts}回再試行");
            foreach (var sl in r.SecondPassLog) WriteLine($"    - {sl}");
        }

        if (r.DurationWarn != null)
            WriteLine($"  ※ Duration差警告: {r.DurationWarn}");

        WriteLine($" [RESULT] {resultStr}");

        if (r.Result != EncodeResult.Pass)
        {
            if (r.WarnInfo?.WarnLines.Count > 0)
            {
                WriteLine("  警告内容:");
                foreach (var wl in r.WarnInfo.WarnLines) WriteLine(wl);
            }
            else if (r.ErrLine != "N/A")
            {
                WriteLine($"  内容    : {r.ErrLine}");
            }

            if (r.WarnInfo?.DefectRanges.Count > 0)
            {
                WriteLine("  欠損箇所:");
                foreach (var dr in r.WarnInfo.DefectRanges) WriteLine($"    {dr}");
            }
            else if (r.ErrFrame != "不明")
            {
                WriteLine($"  欠損箇所: {r.ErrFrame}");
            }
            else
            {
                WriteLine("  欠損箇所: 不明");
            }
        }

        WriteLineDash();
    }

    // ── フッタ ───────────────────────────────────────────
    public void WriteFooter(IReadOnlyList<FileResult> results)
    {
        int pass    = results.Count(r => r.Result == EncodeResult.Pass);
        int warn    = results.Count(r => r.Result == EncodeResult.Warn);
        int fail    = results.Count(r => r.Result == EncodeResult.Fail);
        int skip    = results.Count(r => r.Result == EncodeResult.Skip);
        int retried = results.Count(r => r.RetryCount > 0);
        int secondPassRecovered = results.Count(r =>
            r.SecondPassAttempts > 0 && r.OriginalResult == EncodeResult.Warn && r.Result == EncodeResult.Pass);

        WriteLine();
        WriteLineEqual();
        WriteLine("  処理完了");
        WriteLine($"  PASS: {pass}  WARN: {warn}  FAIL: {fail}  SKIP: {skip}  合計: {results.Count}");
        if (retried > 0)
            WriteLine($"  リトライが発生したファイル: {retried}件");
        if (secondPassRecovered > 0)
            WriteLine($"  第2パスでWARN→PASSに回復したファイル: {secondPassRecovered}件");
        WriteLine($"  ログ: {LogPath}");
        WriteLineEqual();
    }

    private static string ResultLabel(EncodeResult result) => result switch
    {
        EncodeResult.Pass    => "PASS",
        EncodeResult.Warn    => "WARN",
        EncodeResult.Fail    => "FAIL",
        EncodeResult.Skip    => "SKIP",
        _                    => "PENDING",
    };

    // ── フォーマット ─────────────────────────────────────
    public static string FormatDuration(double seconds)
    {
        if (seconds < 0) return "--:--:--.--";
        int h  = (int)(seconds / 3600);
        int m  = (int)(seconds % 3600 / 60);
        int s  = (int)(seconds % 60);
        int cs = (int)((seconds - Math.Floor(seconds)) * 100);
        return $"{h:D2}:{m:D2}:{s:D2}.{cs:D2}";
    }

    public static string FormatElapsed(TimeSpan span)
    {
        int h  = (int)span.TotalHours;
        int cs = span.Milliseconds / 10;
        return $"{h:D2}:{span.Minutes:D2}:{span.Seconds:D2}.{cs:D2}";
    }

    public static string FormatBitrate(double bps) =>
        bps <= 0 ? "N/A" : $"{bps / 1000:F0} kb/s";

    public static string FormatSize(long bytes) =>
        bytes <= 0 ? "N/A" : $"{bytes / 1048576.0:F1} MB";

    public void Dispose() => _writer.Dispose();
}
