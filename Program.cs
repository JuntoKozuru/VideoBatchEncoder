using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using VideoBatchEncoder;

// ============================================================
//  コンソールを UTF-8 に設定
// ============================================================
Console.OutputEncoding = System.Text.Encoding.UTF8;
Console.InputEncoding  = System.Text.Encoding.UTF8;

// ============================================================
//  引数チェック
// ============================================================
if (args.Length == 0)
{
    Console.WriteLine("使い方: 動画ファイルまたはフォルダをこの EXE にドラッグ＆ドロップしてください。");
    Pause();
    return 1;
}

// ============================================================
//  設定ファイル読み込み（VideoBatchEncoder.ini）
// ============================================================
var exeDir  = AppContext.BaseDirectory;
var iniPath = Path.Combine(exeDir, "VideoBatchEncoder.ini");
var ini     = IniReader.Load(iniPath);
var cfg     = new AppConfig();

if (File.Exists(iniPath))
    Console.WriteLine($"  設定ファイル読み込み: {iniPath}");
else
{
    Console.WriteLine("  設定ファイルが見つかりません。デフォルト値で動作します。");
    Console.WriteLine($"  （作成場所: {iniPath}）");
}

cfg.FfmpegPath        = ini.Get      ("ffmpeg", "path",              cfg.FfmpegPath);
cfg.VideoCodec        = ini.Get      ("video",  "codec",             cfg.VideoCodec);
cfg.VideoPreset       = ini.Get      ("video",  "preset",            cfg.VideoPreset);
cfg.VideoTune         = ini.Get      ("video",  "tune",              cfg.VideoTune);
cfg.VideoCQ           = ini.Get      ("video",  "cq",                cfg.VideoCQ);
cfg.VideoRateControl  = ini.Get      ("video",  "ratecontrol",       cfg.VideoRateControl);
cfg.VideoPixelFormat  = ini.Get      ("video",  "pixelformat",       cfg.VideoPixelFormat);
cfg.VideoMovFlags     = ini.Get      ("video",  "movflags",          cfg.VideoMovFlags);
cfg.AudioCodec        = ini.Get      ("audio",  "codec",             cfg.AudioCodec);
cfg.AudioBitrate      = ini.Get      ("audio",  "bitrate",           cfg.AudioBitrate);
cfg.DurationTolerance = ini.GetDouble("output", "durationtolerance", cfg.DurationTolerance);
cfg.TargetExtensions  = ini.GetArray ("input",  "extensions",        cfg.TargetExtensions);

// ── リトライ設定（第1パス内） ────────────────────────────
cfg.RetryMax   = ini.GetInt("retry", "maxretry",   cfg.RetryMax);
cfg.RetryDelay = ini.GetInt("retry", "retrydelay", cfg.RetryDelay);

for (int n = 1; n <= Math.Max(cfg.RetryMax, 1); n++)
{
    var preset  = ini.Get("retry", $"retry{n}_preset",  string.Empty);
    var options = ini.Get("retry", $"retry{n}_options", string.Empty);
    var codec   = ini.Get("retry", $"retry{n}_codec",   string.Empty);

    if (!string.IsNullOrEmpty(preset) || !string.IsNullOrEmpty(options) || !string.IsNullOrEmpty(codec))
    {
        cfg.RetryOverrides.Add(new RetryOverride
        {
            Preset  = string.IsNullOrEmpty(preset)  ? null : preset,
            Options = string.IsNullOrEmpty(options) ? null : options,
            Codec   = string.IsNullOrEmpty(codec)   ? null : codec,
        });
    }
    else
    {
        cfg.RetryOverrides.Add(new RetryOverride());
    }
}

// ── 第2パス（WARN再チャレンジ）設定 ──────────────────────
cfg.SecondPassEnabled  = ini.GetBool("secondpass", "enabled",  cfg.SecondPassEnabled);
cfg.SecondPassMaxRetry = ini.GetInt ("secondpass", "maxretry", cfg.SecondPassMaxRetry);

// ── セッション・レジューム設定 ───────────────────────────
cfg.ResumeEnabled = ini.GetBool("session", "resumeenabled", cfg.ResumeEnabled);
cfg.SessionDir    = ini.Get    ("session", "sessiondir",    cfg.SessionDir);

// ── UI設定 ───────────────────────────────────────────────
cfg.InteractiveUI = ini.GetBool("ui", "interactiveui", cfg.InteractiveUI);

// ============================================================
//  FFmpeg 存在確認
// ============================================================
if (!File.Exists(cfg.FfmpegPath))
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine($"[ERROR] FFmpegが見つかりません: {cfg.FfmpegPath}");
    Console.ResetColor();
    Pause();
    return 1;
}

// ============================================================
//  入力検証: ファイル/フォルダ混在チェック
// ============================================================
bool hasFile = false, hasFolder = false;
foreach (var p in args)
{
    if      (File.Exists(p))      hasFile   = true;
    else if (Directory.Exists(p)) hasFolder = true;
    else
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"[ERROR] パスが存在しません: {p}");
        Console.ResetColor();
        Pause();
        return 1;
    }
}

if (hasFile && hasFolder)
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine("[ERROR] ファイルとフォルダを混在してドロップすることはできません。");
    Console.ResetColor();
    Pause();
    return 1;
}

string processMode = hasFolder ? "フォルダ" : "ファイル";

// ============================================================
//  セッション保存先フォルダの決定
//  （ログ出力先選択より前に必要なため、入力元フォルダを暫定的に使う。
//   ログ出力先が確定したらそちらに合わせて再配置する）
// ============================================================
string provisionalSessionDir = !string.IsNullOrEmpty(cfg.SessionDir)
    ? cfg.SessionDir
    : (hasFolder ? args[0] : Path.GetDirectoryName(args[0]) ?? ".");

// ============================================================
//  レジューム確認
// ============================================================
SessionState? resumedSession = null;
bool          isResumed      = false;

if (cfg.ResumeEnabled)
{
    var found = SessionStore.FindResumableSession(provisionalSessionDir);
    if (found != null)
    {
        Console.WriteLine();
        Console.WriteLine($"  前回 {found.StartedAt:yyyy/MM/dd HH:mm:ss} に開始した未完了セッションが見つかりました。");
        Console.WriteLine($"  対象: {found.Files.Count}件中 " +
                          $"{found.Files.Count(f => f.Result != EncodeResult.Pending)}件処理済み。");
        string resumeChoice = ReadChoice("レジュームしますか？ (1:レジューム / 2:新規開始): ", ["1", "2"]);
        if (resumeChoice == "1")
        {
            resumedSession = found;
            isResumed      = true;
        }
    }
}

// ============================================================
//  対象ファイルリスト構築（新規開始の場合のみ走査。レジューム時はセッションJSONから復元）
// ============================================================
var candidateFiles = new List<string>();

if (resumedSession != null)
{
    candidateFiles.AddRange(resumedSession.Files.Select(f => f.InPath));
}
else if (hasFolder)
{
    foreach (var dir in args)
    {
        var files = Directory.GetFiles(dir)
            .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase);
        foreach (var f in files)
        {
            var ext = Path.GetExtension(f).TrimStart('.').ToLowerInvariant();
            if (cfg.TargetExtensions.Contains(ext))
                candidateFiles.Add(f);
            else
            {
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine($"  除外（対象外拡張子）: {Path.GetFileName(f)}");
                Console.ResetColor();
            }
        }
    }
}
else
{
    candidateFiles.AddRange(args);
}

if (candidateFiles.Count == 0)
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine("[ERROR] 処理対象となるファイルが見つかりません。");
    Console.ResetColor();
    Pause();
    return 1;
}

// ============================================================
//  ヘッダ表示
// ============================================================
var lineEqual = new string('=', 70);

Console.WriteLine();
Console.WriteLine(lineEqual);
Console.WriteLine("  動画一括エンコードツール (NVENC)");
Console.WriteLine(lineEqual);

// ============================================================
//  対話式設定（レジューム時は前回の設定を引き継ぐ）
// ============================================================
string logMode, overwriteMode;

if (resumedSession != null)
{
    logMode       = resumedSession.LogMode;
    overwriteMode = resumedSession.OverwriteMode;
    Console.WriteLine($"  前回の設定を引き継ぎます（ログ形式={logMode}, 既存ファイル扱い={overwriteMode}）");
}
else
{
    Console.WriteLine();
    Console.WriteLine("【ログ出力形式を選択してください】");
    Console.WriteLine("  1 : 全件一覧（WARN/FAIL/SKIPのみ詳細）");
    Console.WriteLine("  2 : 詳細比較表（全ファイル詳細）");
    logMode = ReadChoice("選択 (1/2): ", ["1", "2"]);

    Console.WriteLine();
    Console.WriteLine("【既存ファイルの扱いを選択してください】");
    Console.WriteLine("  1 : 上書き");
    Console.WriteLine("  2 : スキップ");
    overwriteMode = ReadChoice("選択 (1/2): ", ["1", "2"]);
}

// ============================================================
//  ログ出力先（フォルダ選択ダイアログ）
//  非対話モード（InteractiveUI=false）ではダイアログを出さず、
//  常に入力元フォルダを使う（CI/CD環境でダイアログが出ると停止するため）。
// ============================================================
string logFolder;

if (!cfg.InteractiveUI)
{
    logFolder = hasFolder ? args[0] : Path.GetDirectoryName(args[0]) ?? ".";
    Console.WriteLine($"  ログ出力先: {logFolder}（非対話モードのため入力元フォルダを使用）");
}
else
{
    Console.WriteLine();
    Console.WriteLine("ログ出力先フォルダを選択します（キャンセルで入力元フォルダに保存）...");

    string selectedFolder = string.Empty;
    var staThread = new Thread(() =>
    {
        using var dlg = new FolderBrowserDialog
        {
            Description         = "ログ出力先フォルダを選択してください",
            ShowNewFolderButton = true,
        };
        using var owner = new Form { TopMost = true, Visible = false, Width = 0, Height = 0 };
        if (dlg.ShowDialog(owner) == DialogResult.OK)
            selectedFolder = dlg.SelectedPath;
    });
    staThread.SetApartmentState(ApartmentState.STA);
    staThread.Start();
    staThread.Join();

    if (string.IsNullOrEmpty(selectedFolder))
    {
        logFolder = hasFolder ? args[0] : Path.GetDirectoryName(args[0]) ?? ".";
        Console.WriteLine($"  ログ出力先: {logFolder}（入力元フォルダ）");
    }
    else
    {
        logFolder = selectedFolder;
        Console.WriteLine($"  ログ出力先: {logFolder}");
    }
}

// ============================================================
//  ログファイル名・セッションID決定
// ============================================================
var now       = resumedSession?.StartedAt ?? DateTime.Now;
var sessionId = resumedSession?.SessionId ?? now.ToString("yyyyMMdd_HHmmss");
var logName   = candidateFiles.Count == 1
    ? $"{sessionId}_{Path.GetFileNameWithoutExtension(candidateFiles[0])}_encode_summary.log"
    : $"{sessionId}_encode_summary.log";
var logPath   = Path.Combine(logFolder, logName);

// セッションファイルの最終的な保存先（ログ出力先と合わせる）
string sessionDir = !string.IsNullOrEmpty(cfg.SessionDir) ? cfg.SessionDir : logFolder;
var sessionStore = resumedSession != null
    ? new SessionStore(Path.Combine(sessionDir, $"encode_session_{sessionId}.json"))
    : new SessionStore(sessionDir, sessionId);

// ============================================================
//  GPU名取得
// ============================================================
string gpuName = GetGpuName();

// ============================================================
//  残存 .tmp 削除
// ============================================================
foreach (var f in candidateFiles)
{
    var ext     = Path.GetExtension(f).TrimStart('.');
    var stem    = Path.GetFileNameWithoutExtension(f);
    var outStem = ext.Length > 0 ? $"{stem}_{ext}" : stem;
    var tmpPath = Path.Combine(Path.GetDirectoryName(f)!, "mp4", $"{outStem}.mp4.tmp");
    try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch { }
}

// ============================================================
//  セッション状態の準備
//  - 新規開始: プレースホルダーを高速生成して即座にJSON保存
//  - レジューム: 既存のセッション状態をそのまま使う
// ============================================================
SessionState session = resumedSession ?? sessionStore.CreatePlaceholders(
    candidateFiles, processMode, logMode, overwriteMode, sessionId);

if (resumedSession != null)
{
    // セッションのモード情報を今回の選択で更新（レジューム時も一応同期）
    session.LogMode       = logMode;
    session.OverwriteMode = overwriteMode;
}

// ============================================================
//  ログ出力開始
// ============================================================
using var log   = new LogWriter(logPath);
int       total = candidateFiles.Count;

log.WriteSessionHeader(cfg, gpuName, processMode, now, isResumed);

// ============================================================
//  動画の長さを事前スキャン（時間ベースの進捗表示に使う合計時間を算出）
//  ffmpeg -i はヘッダ情報だけを読むため、ファイル数が多くても比較的高速。
//  ここで得た結果は fr.InMeta にそのまま保持し、後段のループでの
//  二重Probeを避ける。
// ============================================================
double totalDurationSeconds = 0;
{
    var pendingForScan = session.Files.Where(f => f.Result == EncodeResult.Pending).ToList();
    for (int si = 0; si < pendingForScan.Count; si++)
    {
        var sfr = pendingForScan[si];
        if (!sfr.InMeta.Valid)
            sfr.InMeta = MediaInfoReader.Probe(cfg.FfmpegPath, sfr.InPath);

        if (sfr.InMeta.Valid && sfr.InMeta.Duration > 0)
            totalDurationSeconds += sfr.InMeta.Duration;

        // \x1b[2K で行全体を消してから書くため、前の文字列より短くても
        // 文字が残留することがない。
        Console.Write($"\x1b[2K\r  動画の長さを解析しています... ({si + 1}/{pendingForScan.Count})");
    }
    if (pendingForScan.Count > 0)
    {
        Console.Write("\x1b[2K\r  動画の長さの解析が完了しました。\n");
    }
}

var reWarnLine  = new Regex(@"(?i)(error|invalid|failed|corrupt|missing|broken|no such)");
var reFrameNum  = new Regex(@"frame=\s*(\d+)");
var reTimeStamp = new Regex(@"time=(\d+:\d+:\d+\.\d+)");

var progressBar = new BatchProgressBar(total, cfg.InteractiveUI, totalDurationSeconds);
progressBar.Initialize();

// レジューム時は既に処理済み（Pending以外）のファイルをスキップする
int startIndex = 0;
if (resumedSession != null)
{
    startIndex = session.Files.FindIndex(f => f.Result == EncodeResult.Pending);
    if (startIndex < 0) startIndex = session.Files.Count; // 全件処理済み
    if (startIndex > 0)
        Console.WriteLine($"  {startIndex}件は前回処理済みのためスキップし、{startIndex + 1}件目から再開します。");
}

// ============================================================
//  第1パス: 通常エンコードループ
// ============================================================
for (int i = startIndex; i < total; i++)
{
    var fr      = session.Files[i];
    var inPath  = fr.InPath;
    var outPath = fr.OutPath;
    var outDir  = Path.GetDirectoryName(outPath)!;
    var tmpPath = Path.Combine(outDir, Path.GetFileNameWithoutExtension(outPath) + ".mp4.tmp");

    Directory.CreateDirectory(outDir);

    // 「解析中」等の個別メッセージはコンソールに出さない（固定2行レイアウトを保つため）。
    // 進捗は progressBar の現在ファイル行で示される。

    // ── Probe結果の再利用: 事前スキャンで既に取得済みならそれを使う ──
    if (!fr.InMeta.Valid)
        fr.InMeta = MediaInfoReader.Probe(cfg.FfmpegPath, inPath);
    var inMeta = fr.InMeta;
    var fileStartTime = DateTime.Now;

    // ── SKIP: 認識不可 ────────────────────────────────
    if (!inMeta.Valid)
    {
        fr.Result   = EncodeResult.Skip;
        fr.ErrLine  = "ffmpegが入力ファイルとして認識できませんでした";
        fr.ErrFrame = "不明";
        sessionStore.Save(session);

        progressBar.ReportNonPass("SKIP", fr.FileName, "認識不可");
        progressBar.Update(i + 1, DateTime.Now - fileStartTime, fileDurationSeconds: 0);
        continue;
    }

    // ── SKIP: 既存ファイル ────────────────────────────
    if (File.Exists(outPath) && overwriteMode == "2")
    {
        fr.Result   = EncodeResult.Skip;
        fr.ErrLine  = "出力ファイルが既に存在するためスキップ";
        fr.ErrFrame = "不明";
        sessionStore.Save(session);

        progressBar.ReportNonPass("SKIP", fr.FileName, "既存ファイル");
        progressBar.Update(i + 1, DateTime.Now - fileStartTime,
            fileDurationSeconds: inMeta.Duration > 0 ? inMeta.Duration : 0);
        continue;
    }

    // ── エンコード（リトライループ） ──────────────────
    var (encResult, usedFallback) = RunEncodeWithRetry(
        cfg, inPath, tmpPath, Path.GetFileName(inPath), i + 1, total, fr, progressBar);

    FinalizeEncodeResult(cfg, fr, inMeta, encResult, outPath, tmpPath, overwriteMode, logMode,
                         reWarnLine, reFrameNum, reTimeStamp);

    // PASS時は何も表示せず、WARN/FAILのみ固定2行の下に追記する。
    if (fr.Result == EncodeResult.Warn)
    {
        var detail = fr.DurationWarn != null
            ? $"Duration差 {fr.DurationWarn}"
            : (fr.WarnInfo?.WarnLines.Count > 0 ? fr.WarnInfo.WarnLines[0].Trim() : "詳細はログを参照");
        progressBar.ReportNonPass("WARN", fr.FileName, detail);
    }
    else if (fr.Result == EncodeResult.Fail)
    {
        progressBar.ReportNonPass("FAIL", fr.FileName, $"ExitCode: {fr.ExitCode}");
    }

    sessionStore.Save(session);
    progressBar.Update(i + 1, encResult.Elapsed, usedFallback,
        fileDurationSeconds: inMeta.Duration > 0 ? inMeta.Duration : 0);
}

progressBar.Complete();

session.FirstPassDone = true;
sessionStore.Save(session);

// ============================================================
//  第2パス: WARN ファイルの再チャレンジ
// ============================================================
if (cfg.SecondPassEnabled)
{
    var warnFiles = session.Files.Where(f => f.Result == EncodeResult.Warn).ToList();
    if (warnFiles.Count > 0)
    {
        Console.WriteLine();
        Console.WriteLine(lineEqual);
        Console.WriteLine($"  第2パス: WARNファイルの再チャレンジ（{warnFiles.Count}件、最大{cfg.SecondPassMaxRetry}回）");
        Console.WriteLine(lineEqual);

        log.WriteLine();
        log.WriteLine(new string('=', 70));
        log.WriteLine($"  第2パス: WARNファイル再チャレンジ（{warnFiles.Count}件）");
        log.WriteLine(new string('=', 70));

        var secondPassBar = new BatchProgressBar(warnFiles.Count, cfg.InteractiveUI);
        secondPassBar.Initialize();

        for (int wi = 0; wi < warnFiles.Count; wi++)
        {
            var fr = warnFiles[wi];
            fr.OriginalResult = fr.Result;

            // 「再チャレンジ」等の個別メッセージはコンソールに出さない（固定2行レイアウトを保つため）。

            var outDir  = Path.GetDirectoryName(fr.OutPath)!;
            var tmpPath = Path.Combine(outDir, Path.GetFileNameWithoutExtension(fr.OutPath) + ".mp4.tmp");

            bool recovered = false;
            for (int attempt = 1; attempt <= cfg.SecondPassMaxRetry; attempt++)
            {
                fr.SecondPassAttempts = attempt;

                var argStr = FfmpegEncoder.BuildArguments(cfg, fr.InPath, tmpPath);
                var cmd    = $"{cfg.FfmpegPath} {argStr}";

                var enc = FfmpegEncoder.Run(cfg.FfmpegPath, argStr, fr.FileName, wi + 1, warnFiles.Count,
                                            retryIndex: attempt, interactiveUI: cfg.InteractiveUI,
                                            progressBar: secondPassBar);

                var warnInfo = StderrParser.ParseFile(enc.StderrLogPath);
                TryDeleteFile(enc.StderrLogPath);

                if (enc.ExitCode == 0 && File.Exists(tmpPath))
                {
                    File.Delete(fr.OutPath);
                    File.Move(tmpPath, fr.OutPath);
                    var outMeta = MediaInfoReader.Probe(cfg.FfmpegPath, fr.OutPath);

                    bool stillWarn = warnInfo.HasWarn || !outMeta.Valid;
                    if (!stillWarn && fr.InMeta.Duration > 0 && outMeta.Duration > 0)
                    {
                        double diff = Math.Abs(fr.InMeta.Duration - outMeta.Duration);
                        stillWarn = diff > cfg.DurationTolerance;
                    }

                    if (!stillWarn)
                    {
                        fr.Result  = EncodeResult.Pass;
                        fr.OutMeta = logMode == "2" ? outMeta : null;
                        fr.SecondPassLog.Add($"{attempt}回目で問題が解消されPASSへ回復");
                        recovered = true;
                        sessionStore.Save(session);
                        secondPassBar.ReportNonPass("PASS", fr.FileName, $"第2パス{attempt}回目で回復");
                        break;
                    }
                    else
                    {
                        fr.SecondPassLog.Add($"{attempt}回目も警告が解消されずWARNのまま");
                    }
                }
                else
                {
                    try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch { }
                    fr.SecondPassLog.Add($"{attempt}回目失敗（ExitCode={enc.ExitCode}）");
                }
            }

            if (!recovered)
            {
                secondPassBar.ReportNonPass("WARN", fr.FileName,
                    $"{cfg.SecondPassMaxRetry}回再チャレンジしても解消せず");
            }

            sessionStore.Save(session);
            secondPassBar.Update(wi + 1, TimeSpan.Zero);
        }

        secondPassBar.Complete();
    }
}

// ============================================================
//  ログ出力
// ============================================================
var results = session.Files;

if (logMode == "1")
{
    log.WriteSummaryTable(results);

    var needDetail = results.Where(r => r.Result != EncodeResult.Pass).ToList();
    if (needDetail.Count > 0)
    {
        log.WriteLine();
        log.WriteLine("  WARN / FAIL / SKIP 詳細");
        log.WriteLineDash();
        foreach (var r in needDetail)
            log.WriteFileReport(r, forceDetail: false);
    }
}
else
{
    log.WriteLine();
    log.WriteLine("  詳細比較表");
    foreach (var r in results)
        log.WriteFileReport(r, forceDetail: true);
}

log.WriteFooter(results);

Console.WriteLine();
Console.ForegroundColor = ConsoleColor.Cyan;
Console.WriteLine($"ログを保存しました: {logPath}");
Console.ResetColor();

Pause();
return 0;

// ============================================================
//  ローカル関数
// ============================================================

static (EncodeOutput Output, bool UsedFallback) RunEncodeWithRetry(
    AppConfig cfg, string inPath, string tmpPath, string label,
    int fileIndex, int fileTotal, FileResult fr, BatchProgressBar? progressBar = null)
{
    EncodeOutput? enc         = null;
    int           attemptsMax = cfg.RetryMax + 1;
    bool          usedFallback = false;

    for (int attempt = 0; attempt < attemptsMax; attempt++)
    {
        RetryOverride? ov = attempt > 0 && attempt - 1 < cfg.RetryOverrides.Count
            ? cfg.RetryOverrides[attempt - 1]
            : null;

        // コーデックが変更される（GPU→CPU等）場合はフォールバックとみなす
        if (ov?.Codec != null && !string.Equals(ov.Codec, cfg.VideoCodec, StringComparison.OrdinalIgnoreCase))
            usedFallback = true;

        var argStr = FfmpegEncoder.BuildArguments(
            cfg, inPath, tmpPath,
            overrideCodec:  ov?.Codec,
            overridePreset: ov?.Preset,
            extraOptions:   ov?.Options);
        var cmdLine = $"{cfg.FfmpegPath} {argStr}";
        fr.Command = cmdLine;

        // 「エンコード開始」「リトライ実行」等の個別メッセージはコンソールに出さない。
        // 固定2行レイアウト（案B）ではPASS時に何も表示せず、進捗バー2行のみを保つ方針のため。
        // 詳細はログファイル（RetryLog）に記録される。

        enc = FfmpegEncoder.Run(cfg.FfmpegPath, argStr, label, fileIndex, fileTotal,
                                retryIndex: attempt, interactiveUI: cfg.InteractiveUI,
                                progressBar: progressBar);

        if (enc.ExitCode == 0 && File.Exists(tmpPath))
        {
            if (attempt > 0)
            {
                fr.RetryCount = attempt;
                fr.RetryLog.Add($"リトライ{attempt}回目で成功" +
                             (ov?.Preset  != null ? $"（Preset={ov.Preset}）"  : "") +
                             (ov?.Codec   != null ? $"（Codec={ov.Codec}）"    : "") +
                             (ov?.Options != null ? $"（Options={ov.Options}）" : ""));
            }
            break;
        }

        try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch { }

        if (attempt < attemptsMax - 1)
        {
            fr.RetryLog.Add($"試行{attempt + 1}回目失敗（ExitCode={enc.ExitCode}） → {cfg.RetryDelay}秒後にリトライ");
            if (cfg.RetryDelay > 0) Thread.Sleep(cfg.RetryDelay * 1000);
        }
        else
        {
            fr.RetryLog.Add($"試行{attempt + 1}回目失敗（ExitCode={enc.ExitCode}） → リトライ上限に到達");
        }
    }

    return (enc!, usedFallback);
}

static void FinalizeEncodeResult(
    AppConfig cfg, FileResult fr, MediaInfo inMeta, EncodeOutput enc,
    string outPath, string tmpPath, string overwriteMode, string logMode,
    Regex reWarnLine, Regex reFrameNum, Regex reTimeStamp)
{
    // stderr を一時ログファイルからストリーム解析（メモリ消費を抑える）
    var warnInfo = StderrParser.ParseFile(enc.StderrLogPath);

    // フォールバックエラー抽出用に一時ログを軽く読む（巨大でも先頭部分のみ走査される想定）
    string errLine = "N/A", errFrame = "不明";
    try
    {
        if (File.Exists(enc.StderrLogPath))
        {
            using var reader = new StreamReader(enc.StderrLogPath, System.Text.Encoding.UTF8);
            string? lastErrLine = null;
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                if (reWarnLine.IsMatch(line)) lastErrLine = line.Trim();
                var mf = reFrameNum.Match(line);
                if (mf.Success) errFrame = $"frame {mf.Groups[1].Value}";
            }
            if (lastErrLine != null) errLine = lastErrLine;
        }
    }
    catch { /* 解析失敗時は N/A のまま */ }
    finally
    {
        TryDeleteFile(enc.StderrLogPath);
    }

    long       outSize = -1L;
    string?    durWarn = null;
    MediaInfo? outMeta = null;

    if (enc.ExitCode == 0 && File.Exists(tmpPath))
    {
        if (File.Exists(outPath) && overwriteMode == "1")
            File.Delete(outPath);
        File.Move(tmpPath, outPath);

        if (File.Exists(outPath))
            outSize = new FileInfo(outPath).Length;

        outMeta = MediaInfoReader.Probe(cfg.FfmpegPath, outPath);

        if (outSize <= 0)
        {
            warnInfo.HasWarn = true;
            warnInfo.WarnLines.Add("    出力ファイルのサイズが 0 バイトです");
        }

        if (!outMeta.Valid)
        {
            warnInfo.HasWarn = true;
            warnInfo.WarnLines.Add("    出力ファイルのメタデータを取得できませんでした（ファイルが破損している可能性があります）");
        }

        if (inMeta.Duration > 0 && outMeta.Valid && outMeta.Duration > 0)
        {
            double diff = Math.Abs(inMeta.Duration - outMeta.Duration);
            if (diff > cfg.DurationTolerance)
            {
                durWarn = $"入力 {LogWriter.FormatDuration(inMeta.Duration)} → 出力 {LogWriter.FormatDuration(outMeta.Duration)}" +
                          $"（差 {diff:F2}秒 / 許容 {cfg.DurationTolerance}秒）";
                warnInfo.HasWarn = true;
                warnInfo.WarnLines.Add($"    Duration差が許容値を超えています: {diff:F2}秒");
            }
        }

        if (logMode != "2") outMeta = null;

        var resultLabel = warnInfo.HasWarn ? EncodeResult.Warn : EncodeResult.Pass;

        // PASS/WARNのコンソール表示は呼び出し元(メインループ)が progressBar.ReportNonPass を
        // 介して行う（固定2行レイアウトを保つため、ここでは直接コンソールに書かない）。

        fr.Result       = resultLabel;
        fr.Elapsed       = enc.Elapsed;
        fr.OutSize       = outSize;
        fr.OutMeta       = outMeta;
        fr.ExitCode      = enc.ExitCode;
        fr.ErrLine       = errLine;
        fr.ErrFrame      = errFrame;
        fr.WarnInfo      = warnInfo;
        fr.DurationWarn  = durWarn;
    }
    else
    {
        try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch { }

        // FAILのコンソール表示も呼び出し元が progressBar.ReportNonPass を介して行う。

        fr.Result   = EncodeResult.Fail;
        fr.Elapsed  = enc.Elapsed;
        fr.ExitCode = enc.ExitCode;
        fr.ErrLine  = errLine;
        fr.ErrFrame = errFrame;
        fr.WarnInfo = warnInfo;
    }
}

static void TryDeleteFile(string path)
{
    try { if (!string.IsNullOrEmpty(path) && File.Exists(path)) File.Delete(path); } catch { }
}


static string ReadChoice(string prompt, string[] valid)
{
    while (true)
    {
        Console.Write(prompt);
        var input = (Console.ReadLine() ?? string.Empty).Trim();
        if (valid.Contains(input)) return input;
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("  ※ 無効な入力です。再入力してください。");
        Console.ResetColor();
    }
}

static string GetGpuName()
{
    try
    {
        using var proc = new Process();
        proc.StartInfo = new ProcessStartInfo
        {
            FileName               = "nvidia-smi",
            Arguments              = "--query-gpu=name --format=csv,noheader",
            UseShellExecute        = false,
            CreateNoWindow          = true,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
        };
        proc.Start();
        var output = proc.StandardOutput.ReadLine()?.Trim();
        proc.WaitForExit();
        return !string.IsNullOrEmpty(output) ? output : "UNKNOWN";
    }
    catch { return "UNKNOWN"; }
}

static void Pause()
{
    Console.WriteLine("何かキーを押すと終了します...");
    Console.ReadKey(intercept: true);
}
