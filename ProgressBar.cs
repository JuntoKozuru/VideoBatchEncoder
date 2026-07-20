namespace VideoBatchEncoder;

/// <summary>
/// バッチ全体の進捗バーをコンソール上部に固定表示する。
///
/// 表示例（2行構成・対話モード時）:
///   [==========================            ] 427/1024 (41.7%)  残り約 03:42
///   [ 427/1024] NORM0427.AVI  |  frame= 847 fps=389 speed=13.5x
///
/// FfmpegEncoder.Run() が同一行上書きで進捗行（2行目）を更新し、
/// このクラスが1行目（全体バー）を別の固定行で上書き更新する。
///
/// InteractiveUI=false の場合はカーソル制御を一切行わず、
/// 1ファイル完了ごとに改行付きの1行サマリを出すだけのシンプル表示に切り替わる
/// （CI/CDログ監視・非対話シェルでの描画破綻を防ぐため）。
/// </summary>
internal sealed class BatchProgressBar
{
    private readonly int  _total;
    private readonly bool _interactive;
    private int           _barWidth;

    private readonly Queue<TimeSpan> _recentElapsed = new();
    private const    int   SampleSize = 10; // 移動平均のサンプル数

    private int  _cursorRow = -1;
    private bool _consoleSupported;

    // リサイズ検知用に直近のウィンドウサイズを保持
    private int _lastKnownWidth  = -1;
    private int _lastKnownHeight = -1;

    public BatchProgressBar(int total, bool interactiveUI = true)
    {
        _total       = total;
        _interactive = interactiveUI;
        _consoleSupported = interactiveUI; // 非対話モードでは最初から無効
        if (_interactive)
            _barWidth = Math.Max(20, Math.Min(50, Console.WindowWidth - 30));
    }

    /// <summary>
    /// 進捗バーの初期行を確保する。
    /// エンコードループ開始前に1回だけ呼ぶ。
    /// </summary>
    public void Initialize()
    {
        if (!_interactive) return; // 非対話モードでは何もしない

        try
        {
            CaptureWindowSize();
            Console.WriteLine();
            _cursorRow = Console.CursorTop - 1;
            Console.WriteLine();
            Render(0, null);
        }
        catch
        {
            _consoleSupported = false;
        }
    }

    /// <summary>
    /// 1ファイル完了時に呼ぶ。バーを更新する。
    /// </summary>
    /// <param name="completed">完了済みファイル数</param>
    /// <param name="elapsed">このファイルの処理時間</param>
    /// <param name="wasFallback">
    /// このファイルでGPU→CPU等のフォールバック（リトライによる設定変更）が発生したか。
    /// true の場合、移動平均の履歴をリセットし、異常値による残り時間予測の狂いを防ぐ。
    /// </param>
    public void Update(int completed, TimeSpan elapsed, bool wasFallback = false)
    {
        if (!_interactive)
        {
            // 非対話モード: シンプルな改行ログのみ
            Console.WriteLine($"  [{completed}/{_total}] 完了（処理時間: {LogWriter.FormatElapsed(elapsed)}）");
            return;
        }

        if (!_consoleSupported || _cursorRow < 0) return;

        if (wasFallback)
        {
            // フォールバック発生: 古い（高速だった）履歴を捨て、予測をリセットする。
            // 数件分のデータが溜まるまでは「計算中...」表示に戻る。
            _recentElapsed.Clear();
        }

        _recentElapsed.Enqueue(elapsed);
        if (_recentElapsed.Count > SampleSize) _recentElapsed.Dequeue();

        try
        {
            if (DetectResizeAndReinitialize()) return; // リサイズ時は再初期化のみ行い今回の描画はスキップ
            Render(completed, EstimateRemaining(completed));
        }
        catch { _consoleSupported = false; }
    }

    /// <summary>完了時にバーを 100% 表示にする。</summary>
    public void Complete()
    {
        if (!_interactive) return;
        if (!_consoleSupported || _cursorRow < 0) return;
        try { Render(_total, TimeSpan.Zero); }
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

    /// <summary>
    /// コンソールのリサイズを検知した場合、画面をクリアして
    /// 進捗バーの表示領域を再確保する。
    /// 戻り値 true: リサイズが発生し再初期化した（呼び出し元は今回の描画をスキップしてよい）
    /// </summary>
    private bool DetectResizeAndReinitialize()
    {
        int curWidth  = Console.WindowWidth;
        int curHeight = Console.WindowHeight;

        if (curWidth == _lastKnownWidth && curHeight == _lastKnownHeight)
            return false; // 変化なし

        // リサイズ発生: 座標系が変わったため、安全のため画面をクリアして
        // 進捗バー領域を確保し直す。
        try
        {
            Console.Clear();
        }
        catch { /* リダイレクト等でClear不可な場合は無視して続行 */ }

        _barWidth = Math.Max(20, Math.Min(50, curWidth - 30));
        Console.WriteLine();
        _cursorRow = Console.CursorTop - 1;
        Console.WriteLine();

        CaptureWindowSize();
        return true;
    }

    private void Render(int completed, TimeSpan? remaining)
    {
        int savedTop  = Console.CursorTop;
        int savedLeft = Console.CursorLeft;

        Console.SetCursorPosition(0, _cursorRow);

        double pct      = _total > 0 ? (double)completed / _total : 0.0;
        int    filled   = (int)Math.Round(pct * _barWidth);
        int    unfilled = _barWidth - filled;

        var bar    = "[" + new string('=', filled) + new string(' ', unfilled) + "]";
        var pctStr = $"{pct * 100:F1}%";

        string remStr = remaining.HasValue
            ? (remaining.Value == TimeSpan.Zero
                ? "完了          "
                : $"残り約 {(int)remaining.Value.TotalMinutes:D2}:{remaining.Value.Seconds:D2}")
            : "計算中...     ";

        var line = $" {bar} {completed}/{_total} ({pctStr})  {remStr}";
        int maxLen = Math.Max(1, Console.WindowWidth - 1);
        if (line.Length > maxLen) line = line[..maxLen];
        else line = line.PadRight(maxLen);

        Console.Write(line);

        try { Console.SetCursorPosition(savedLeft, savedTop); }
        catch { /* リサイズ直後等で座標が無効な場合は無視 */ }
    }

    private TimeSpan? EstimateRemaining(int completed)
    {
        if (_recentElapsed.Count == 0 || completed == 0) return null;
        var avg = TimeSpan.FromTicks((long)_recentElapsed.Average(t => t.Ticks));
        int remaining = _total - completed;
        return TimeSpan.FromTicks(avg.Ticks * remaining);
    }
}
