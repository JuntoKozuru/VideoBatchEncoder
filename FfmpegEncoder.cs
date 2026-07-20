using System.Diagnostics;
using System.Text.RegularExpressions;

namespace VideoBatchEncoder;

internal sealed class EncodeOutput
{
    public int     ExitCode      { get; init; }
    public TimeSpan Elapsed      { get; init; }
    /// <summary>
    /// 警告・エラー行が退避された一時ログファイルのパス。
    /// StderrParser.ParseFile() に渡して解析し、使い終えたら呼び出し側が削除すること。
    /// </summary>
    public string   StderrLogPath { get; init; } = string.Empty;
}

/// <summary>
/// ffmpegをリアルタイム進捗表示付きで実行する。
///   - stderr を ReadLine() 同期ループで受信（スレッド競合なし）
///   - frame= 行はコンソール同一行に上書き表示（ログ非記録。非対話モードでは改行表示に切替）
///   - -progress pipe:2 の付加情報行も非表示・非記録
///   - その他行（警告・エラー）は RAM ではなく一時ログファイルへ直接 StreamWriter で書き流す
///     → 破損ファイルで警告が膨大に出てもメモリ消費は定常状態のまま
/// </summary>
internal static partial class FfmpegEncoder
{
    [GeneratedRegex(@"^(fps=|stream_|bitrate=|total_size=|out_time|dup_frames|drop_frames|speed=|progress=)")]
    private static partial Regex ReProgressExtra();

    // ────────────────────────────────────────────────────────
    //  引数ビルド（コーデックファミリーに応じてオプションを切替）
    // ────────────────────────────────────────────────────────
    public static string BuildArguments(
        AppConfig cfg,
        string    inPath,
        string    outPath,
        string?   overrideCodec   = null,
        string?   overridePreset  = null,
        string?   extraOptions    = null)
    {
        var codec  = overrideCodec  ?? cfg.VideoCodec;
        var preset = overridePreset ?? cfg.VideoPreset;

        var parts = new List<string>
        {
            "-y",
            "-hide_banner", "-nostdin", "-nostats",
            "-i", inPath,
            "-map", "0:v:0",
            "-map", "0:a?",
            "-sn", "-dn",
            "-c:v", codec,
        };

        if (CodecFamily.SupportsPreset(codec))
            parts.AddRange(["-preset", preset]);

        if (CodecFamily.SupportsTune(codec) && !string.IsNullOrEmpty(cfg.VideoTune))
            parts.AddRange(["-tune", cfg.VideoTune]);

        parts.Add(CodecFamily.QualityOption(codec));
        parts.Add(cfg.VideoCQ);

        if (CodecFamily.SupportsRateControl(codec))
            parts.AddRange(["-rc", cfg.VideoRateControl]);

        if (!string.IsNullOrEmpty(cfg.VideoPixelFormat))
            parts.AddRange(["-pix_fmt", cfg.VideoPixelFormat]);

        parts.AddRange(["-c:a", cfg.AudioCodec, "-b:a", cfg.AudioBitrate]);

        if (!string.IsNullOrEmpty(cfg.VideoMovFlags))
            parts.AddRange(["-movflags", cfg.VideoMovFlags]);

        if (!string.IsNullOrEmpty(extraOptions))
        {
            foreach (var tok in extraOptions.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                parts.Add(tok);
        }

        parts.AddRange(["-progress", "pipe:2"]);

        // 出力先は処理中であることを示すため "*.mp4.tmp" という二重拡張子で書き出す。
        // ffmpegは出力フォーマットを「ファイル名の最後の拡張子」から自動判定するため、
        // 末尾が .tmp だと "Unable to choose an output format" / muxer初期化失敗
        // (Invalid argument) になる。-f mp4 でコンテナ形式を明示し、
        // 拡張子に依存しない判定にすることでこれを回避する。
        parts.AddRange(["-f", "mp4"]);
        parts.Add(outPath);

        return BuildRawArguments(parts);
    }

    public static string BuildRawArguments(IEnumerable<string> parts)
    {
        var tokens = parts.Select(token =>
        {
            token = token.Replace("\"", "\\\"");
            // クォート対象文字:
            //   空白・cmd特殊文字に加えて [ ] も含める。
            //   ffmpegは [ ] をフィルタグラフのストリームラベル区切りとして予約しているため、
            //   パスに [ ] が含まれる場合（例: S:\[TEMP]\...）に未クォートだと
            //   ffmpegが意図しないラベルとして誤解釈し、muxer初期化が
            //   "Invalid argument" で失敗する。
            return token.Any(c => char.IsWhiteSpace(c) || "&|()^<>[]".Contains(c)) || token.Length == 0
                ? $"\"{token}\""
                : token;
        });
        return string.Join(' ', tokens);
    }

    // ────────────────────────────────────────────────────────
    //  エンコード実行
    // ────────────────────────────────────────────────────────
    public static EncodeOutput Run(
        string ffmpegPath,
        string arguments,
        string label,
        int    fileIndex,
        int    fileTotal,
        int    retryIndex    = 0,      // 0=初回, 1以上=リトライ回数
        bool   interactiveUI = true)   // false: カーソル制御なし・改行のみで進捗表示
    {
        var psi = new ProcessStartInfo
        {
            FileName               = ffmpegPath,
            Arguments               = arguments,
            UseShellExecute         = false,
            CreateNoWindow          = true,
            RedirectStandardError   = true,
            RedirectStandardOutput  = true,
        };

        var indexWidth = fileTotal.ToString().Length;
        var retryTag   = retryIndex > 0 ? $" [リトライ{retryIndex}]" : "";

        // stderrの非進捗行（警告・エラー）はRAMではなく一時ファイルへ直接書き流す。
        // StreamWriterはAutoFlush無効のまま使い、書き込み頻度によるI/O過多を避ける
        // （プロセス終了時にusingでFlush・Closeされる）。
        var stderrLogPath = Path.Combine(Path.GetTempPath(), $"videobatchencoder_stderr_{Guid.NewGuid():N}.log");

        using var proc = new Process { StartInfo = psi };
        var sw = Stopwatch.StartNew();
        proc.Start();

        var stdoutTask = proc.StandardOutput.ReadToEndAsync();

        // 非対話モードでは一定間隔（行数ベース）でしか出力しない方が
        // ログが読みやすいため、簡易スロットリングを行う。
        int progressLineCounter = 0;
        const int NonInteractiveThrottle = 30; // 約30回のframe=行ごとに1回出力

        using (var stderrWriter = new StreamWriter(stderrLogPath, append: false, System.Text.Encoding.UTF8))
        {
            while (true)
            {
                var line = proc.StandardError.ReadLine();
                if (line is null) break;

                var stripped = line.TrimStart('\r').Trim();
                if (stripped.Length == 0) continue;

                if (stripped.StartsWith("frame=", StringComparison.Ordinal))
                {
                    if (interactiveUI)
                    {
                        // 同一行上書き表示
                        var display = $"[{fileIndex.ToString().PadLeft(indexWidth)}/{fileTotal}]{retryTag} {label}  |  {stripped}";
                        int maxLen  = Math.Max(1, Console.WindowWidth - 1);
                        if (display.Length > maxLen) display = display[..maxLen];
                        else display = display.PadRight(maxLen);
                        Console.Write($"\r{display}");
                    }
                    else
                    {
                        // 非対話モード: カーソル制御を一切使わず、間引いて改行出力する
                        progressLineCounter++;
                        if (progressLineCounter % NonInteractiveThrottle == 0)
                            Console.WriteLine($"[{fileIndex.ToString().PadLeft(indexWidth)}/{fileTotal}]{retryTag} {label}  |  {stripped}");
                    }
                }
                else if (ReProgressExtra().IsMatch(stripped))
                {
                    // -progress pipe:2 の付加情報行: 非表示・非記録
                }
                else
                {
                    stderrWriter.WriteLine(stripped);
                }
            }
        } // using ブロック終了時に確実にFlush・Closeされる

        proc.WaitForExit();
        stdoutTask.Wait();
        sw.Stop();

        if (interactiveUI) Console.WriteLine(); // 進捗行の末尾改行

        return new EncodeOutput
        {
            ExitCode      = proc.ExitCode,
            Elapsed       = sw.Elapsed,
            StderrLogPath = stderrLogPath,
        };
    }
}
