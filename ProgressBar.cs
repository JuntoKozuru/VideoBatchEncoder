using System.Diagnostics;

namespace VideoBatchEncoder;

/// <summary>
/// バッチ全体の進捗バーをコンソール上部の2行に完全固定して表示する。
///
/// 表示例（対話モード時）:
///   [==========================            ] 427/1024 (41.7%)  残り約 03:42 ⠋
///   [ 427/1024] NORM0427.AVI  |  frame= 847 fps=389 speed=13.5x
///     [WARN] NORM0024.AVI （decode_slice_header error 812件）
///     [FAIL] NORM0312.AVI （ExitCode: -22）
///
/// 実装方針（重要）:
///   Console.SetCursorPosition() と Console.Write() を別々の呼び出しにすると、
///   Windows Terminal 等の ConPTY 環境ではこの2つの処理が非同期に届き、
///   順序が入れ替わって描画が乱れることがある（カーソル移動APIは通常の
///   標準出力ストリームとは別経路で処理されるため）。
///   これを避けるため、本クラスは「カーソル移動＋行クリア＋文字列」を
///   ANSIエスケープシーケンスとして1本の文字列に組み立て、必ず単一の
///   Console.Write() 呼び出しで書き出す。SetCursorPosition/ForegroundColor
///   などの個別Win32 API呼び出しは一切使わない。
///
/// この2行は常に画面上の同じ絶対行（_barRow / _fileRow、0始まり）に
/// 書き込まれるため、1ファイルごとに改行されてスクロールしていくことはない。
/// PASSは表示しない（案B）。WARN/FAIL/SKIPのみ、固定2行の下に1行ずつ追記する
/// （ここはスクロールしてよい領域）。
///
/// 全体バーはバックグラウンドタイマーにより約120ms間隔で自走更新する。
///
/// InteractiveUI=false の場合はカーソル制御・タイマーを一切使わず、
/// 1ファイル完了ごとに改行付きの1行サマリを出すだけのシンプル表示に切り替わる。
/// </summary>
internal sealed class BatchProgressBar : IDisposable
{
    private readonly int  _total;
    private readonly bool _interactive;
    private int           _barWidth;

    private readonly Queue<TimeSpan> _recentElapsed = new();
    private const    int   SampleSize = 10; // 移動平均のサンプル数

    // ── 動画の長さに基づく進捗率 ──────────────────────────
    private readonly double _totalDurationSeconds;
    private double          _cumulativeCompletedSeconds; // 完了済みファイルの合計時間
    private double          _currentOutTimeSeconds;       // 現在処理中ファイルの処理済み時間

    // 固定2行の絶対行番号（0始まり、コンソールバッファ座標）
    private int  _barRow  = -1;
    private int  _fileRow = -1;
    private bool _consoleSupported;

    private int _lastKnownWidth  = -1;
    private int _lastKnownHeight = -1;

    private readonly object _consoleLock = new();

    private int       _completed;
    private TimeSpan? _cachedRemaining;
    private string    _currentFileLine = string.Empty;

    private static readonly char[] SpinnerFrames = ['⠋', '⠙', '⠹', '⠸', '⠼', '⠴', '⠦', '⠧', '⠇', '⠏'];
    private int _spinnerIndex;

    private System.Threading.Timer? _timer;
    private const int TimerIntervalMs = 120;

    // バッチ全体の経過時間（動画の長さベースの残り時間予測に使う）。
    // Initialize() で開始し、以後止めない。
    private readonly Stopwatch _overallStopwatch = new();

    // ── ANSIエスケープシーケンス定数 ──────────────────────
    private const string AnsiHideCursor = "\x1b[?25l";
    private const string AnsiShowCursor = "\x1b[?25h";
    private const string AnsiClearLine  = "\x1b[2K"; // カーソル位置の行全体を消去
    private const string AnsiReset      = "\x1b[0m";
    private const string AnsiYellow     = "\x1b[33m";
    private const string AnsiRed        = "\x1b[31m";
    private const string AnsiGreen      = "\x1b[32m";
    private const string AnsiGray       = "\x1b[90m";

    /// <summary>0始まり行番号 row へ移動する ANSI シーケンス（1始まりに変換）</summary>
    private static string MoveTo(int row) => $"\x1b[{row + 1};1H";

    public BatchProgressBar(int total, bool interactiveUI = true, double totalDurationSeconds = 0)
    {
        _total                = total;
        _interactive          = interactiveUI;
        _totalDurationSeconds = totalDurationSeconds;
        _consoleSupported     = interactiveUI;
        if (_interactive)
            _barWidth = Math.Max(20, Math.Min(50, Console.WindowWidth - 30));
    }

    /// <summary>
    /// 進捗バーの初期2行を確保し、バックグラウンド自走タイマーを開始する。
    /// エンコードループ開始前に1回だけ呼ぶ。
    /// </summary>
    public void Initialize()
    {
        if (!_interactive) return;

        try
        {
            CaptureWindowSize();
            Console.WriteLine(); // バー行
            Console.WriteLine(); // ファイル行
            _barRow  = Console.CursorTop - 2;
            _fileRow = Console.CursorTop - 1;

            Console.Write(AnsiHideCursor);

            _overallStopwatch.Restart();
            RenderBoth();

            _timer = new System.Threading.Timer(OnTimerTick, null, TimerIntervalMs, TimerIntervalMs);
        }
        catch
        {
            _consoleSupported = false;
        }
    }

    /// <summary>
    /// FfmpegEncoder.Run() から呼ぶ。現在ファイルの進捗行（2行目）の内容を更新する。
    /// 実際の再描画はタイマー側でまとめて行う（ffmpegの出力頻度が高いため、
    /// 呼ばれるたびに即描画すると書き込み回数が増えすぎてチラつく）。
    /// </summary>
    public void UpdateCurrentFileLine(string text)
    {
        if (!_interactive || !_consoleSupported || _fileRow < 0) return;
        lock (_consoleLock) { _currentFileLine = text; }
    }

    /// <summary>
    /// FfmpegEncoder.Run() から呼ぶ。現在ファイルの処理済み時間（out_time）を保持する。
    /// </summary>
    public void UpdateCurrentOutTime(TimeSpan outTime)
    {
        if (!_interactive || !_consoleSupported) return;
        lock (_consoleLock) { _currentOutTimeSeconds = outTime.TotalSeconds; }
    }

    /// <summary>
    /// 1ファイル完了時に呼ぶ。全体バーの完了数を進める。
    /// </summary>
    public void Update(int completed, TimeSpan elapsed, bool wasFallback = false, double fileDurationSeconds = 0)
    {
        if (!_interactive)
        {
            Console.WriteLine($"  [{completed}/{_total}] 完了（処理時間: {LogWriter.FormatElapsed(elapsed)}）");
            return;
        }

        if (!_consoleSupported || _barRow < 0) return;

        if (wasFallback)
        {
            _recentElapsed.Clear();
            _cachedRemaining = null;
        }

        _recentElapsed.Enqueue(elapsed);
        if (_recentElapsed.Count > SampleSize) _recentElapsed.Dequeue();

        _completed = completed;
        _cumulativeCompletedSeconds += fileDurationSeconds;
        _currentOutTimeSeconds       = 0;

        try
        {
            lock (_consoleLock)
            {
                if (DetectResizeAndReinitialize()) return;
                _cachedRemaining = EstimateRemaining(_completed);
                RenderBoth();
            }
        }
        catch { _consoleSupported = false; }
    }

    /// <summary>
    /// WARN/FAIL/SKIPが発生したファイルを、固定2行の下に1行追記する。
    /// </summary>
    public void ReportNonPass(string label, string fileName, string detail)
    {
        if (!_interactive || !_consoleSupported || _barRow < 0)
        {
            Console.WriteLine($"  [{label}] {fileName} （{detail}）");
            return;
        }

        try
        {
            lock (_consoleLock)
            {
                var color = label switch
                {
                    "WARN" => AnsiYellow,
                    "FAIL" => AnsiRed,
                    "PASS" => AnsiGreen,
                    _      => AnsiGray,
                };

                // 固定2行の直下（_fileRow+1）に、カーソル移動＋行クリア＋色付き文字列＋
                // 色リセット＋改行までを1本の文字列にまとめて一括で書き出す。
                var text = $"{MoveTo(_fileRow + 1)}{AnsiClearLine}{color}    [{label}] {fileName} （{detail}）{AnsiReset}\n";
                Console.Write(text);

                // この追記で固定2行より下の表示が1行ぶん下がったため、座席も同じだけシフトする。
                _barRow++;
                _fileRow++;

                RenderBoth();
            }
        }
        catch { _consoleSupported = false; }
    }

    /// <summary>完了時にバーを 100% 表示にし、タイマーを停止する。</summary>
    public void Complete()
    {
        if (!_interactive) return;
        if (!_consoleSupported || _barRow < 0) { Dispose(); return; }
        try
        {
            lock (_consoleLock)
            {
                _completed                  = _total;
                _cumulativeCompletedSeconds = _totalDurationSeconds;
                _currentOutTimeSeconds      = 0;
                _cachedRemaining            = TimeSpan.Zero;
                RenderBoth();
            }
        }
        catch { _consoleSupported = false; }
        finally { Dispose(); }
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _timer = null;
        try { Console.Write(AnsiShowCursor); } catch { }
    }

    private void OnTimerTick(object? state)
    {
        if (!_consoleSupported || _barRow < 0) return;

        try
        {
            lock (_consoleLock)
            {
                _spinnerIndex    = (_spinnerIndex + 1) % SpinnerFrames.Length;
                _cachedRemaining = EstimateRemaining(_completed);
                RenderBoth();
            }
        }
        catch { _consoleSupported = false; }
    }

    // ────────────────────────────────────────────────────────
    //  内部処理
    // ────────────────────────────────────────────────────────

    private void CaptureWindowSize()
    {
        _lastKnownWidth  = Console.WindowWidth;
        _lastKnownHeight = Console.WindowHeight;
    }

    private bool DetectResizeAndReinitialize()
    {
        int curWidth  = Console.WindowWidth;
        int curHeight = Console.WindowHeight;

        if (curWidth == _lastKnownWidth && curHeight == _lastKnownHeight)
            return false;

        try { Console.Clear(); }
        catch { /* リダイレクト等でClear不可な場合は無視して続行 */ }

        _barWidth = Math.Max(20, Math.Min(50, curWidth - 30));
        Console.WriteLine();
        Console.WriteLine();
        _barRow  = Console.CursorTop - 2;
        _fileRow = Console.CursorTop - 1;

        CaptureWindowSize();
        return true;
    }

    /// <summary>
    /// バー行とファイル行を、1回の Console.Write() にまとめて同時に描画する。
    /// 2回に分けて書くと、その間に他の書き込みが割り込む余地が生まれるため、
    /// 必ず1本の文字列として送る。
    /// </summary>
    private void RenderBoth()
    {
        int maxLen = Math.Max(1, Console.WindowWidth - 1);

        var sb = new System.Text.StringBuilder();
        sb.Append(MoveTo(_barRow)).Append(AnsiClearLine).Append(ClipToWidth(BuildBarText(), maxLen));
        sb.Append(MoveTo(_fileRow)).Append(AnsiClearLine).Append(ClipToWidth(BuildFileText(), maxLen));
        Console.Write(sb.ToString());
    }

    /// <summary>
    /// 画面幅を超える文字列は自動折り返しの原因になり、はみ出した分が
    /// 行クリアの対象外になってゴミとして残るため、必ず幅内に切り詰める。
    /// </summary>
    private static string ClipToWidth(string text, int maxLen) =>
        text.Length > maxLen ? text[..maxLen] : text;

    private string BuildBarText()
    {
        double pct;
        if (_totalDurationSeconds > 0)
        {
            pct = Math.Clamp((_cumulativeCompletedSeconds + _currentOutTimeSeconds) / _totalDurationSeconds, 0.0, 1.0);
        }
        else
        {
            pct = _total > 0 ? (double)_completed / _total : 0.0;
        }

        int filled   = (int)Math.Round(pct * _barWidth);
        int unfilled = _barWidth - filled;

        var bar    = "[" + new string('=', filled) + new string(' ', unfilled) + "]";
        var pctStr = $"{pct * 100:F1}%";

        string remStr;
        if (_completed >= _total)
        {
            remStr = "完了          ";
        }
        else if (_cachedRemaining.HasValue)
        {
            var r = _cachedRemaining.Value;
            remStr = $"残り約 {(int)r.TotalMinutes:D2}:{r.Seconds:D2}";
        }
        else
        {
            remStr = "計算中...     ";
        }

        string spinner = _completed >= _total ? " " : SpinnerFrames[_spinnerIndex].ToString();

        return $" {bar} 完了{_completed}/{_total} ({pctStr})  {remStr} {spinner}";
    }

    private string BuildFileText() => _currentFileLine;

    private TimeSpan? EstimateRemaining(int completed)
    {
        if (_totalDurationSeconds > 0)
        {
            // 動画の長さベース: これまでに処理した動画の秒数 ÷ 実際にかかった時間 で
            // 「実時間1秒あたり何秒ぶんの動画を処理できているか」を求め、
            // 残り秒数をその速度で割る。out_time は描画のたびに更新されるため、
            // 1ファイルの完了を待たずに毎回（約120ms間隔で）再計算される。
            double processed   = _cumulativeCompletedSeconds + _currentOutTimeSeconds;
            double elapsedWall = _overallStopwatch.Elapsed.TotalSeconds;

            // 立ち上がり直後（数秒未満・処理量ほぼゼロ）は値が暴れるため、
            // 安定するまでは「計算中...」のままにする。
            if (elapsedWall < 2.0 || processed <= 0.0) return null;

            double rate = processed / elapsedWall;
            if (rate <= 0.0) return null;

            double remainingSeconds = Math.Max(0.0, _totalDurationSeconds - processed) / rate;
            return TimeSpan.FromSeconds(remainingSeconds);
        }

        // 長さ不明時のフォールバック: ファイル完了時間の移動平均（従来方式）
        if (_recentElapsed.Count == 0 || completed == 0) return null;
        var avg = TimeSpan.FromTicks((long)_recentElapsed.Average(t => t.Ticks));
        int remainingFiles = _total - completed;
        return TimeSpan.FromTicks(avg.Ticks * remainingFiles);
    }
}
