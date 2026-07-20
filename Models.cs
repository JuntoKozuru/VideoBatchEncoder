namespace VideoBatchEncoder;

/// <summary>ffmpeg -i で取得したメタデータ</summary>
internal sealed class MediaInfo
{
    public bool   Valid      { get; set; }
    public double Duration   { get; set; } = -1.0;
    public string VideoCodec { get; set; } = "N/A";
    public int    Width      { get; set; }
    public int    Height     { get; set; }
    public double FPS        { get; set; } = -1.0;
    public string AudioCodec { get; set; } = "N/A";
    public double Bitrate    { get; set; } = -1.0;
    public string Extension  { get; set; } = string.Empty;
}

/// <summary>エンコード結果</summary>
internal enum EncodeResult { Pending, Pass, Warn, Fail, Skip }

/// <summary>stderr 解析結果（警告・欠損箇所）</summary>
internal sealed class StderrAnalysis
{
    public bool          HasWarn      { get; set; }
    public List<string>  WarnLines    { get; } = [];
    public List<string>  DefectRanges { get; } = [];
}

/// <summary>リトライ1回分のオーバーライド設定</summary>
internal sealed class RetryOverride
{
    /// <summary>上書きするプリセット（null = 変更なし）</summary>
    public string? Preset  { get; init; }
    /// <summary>追加するffmpegオプション文字列（null = なし）</summary>
    public string? Options { get; init; }
    /// <summary>上書きするコーデック（null = 変更なし）</summary>
    public string? Codec   { get; init; }
}

/// <summary>
/// 1ファイル分の処理結果。
/// JSONセッションへシリアライズされるため、System.Text.Json で
/// 問題なく扱える単純な型（string/数値/bool/List）のみで構成する。
/// </summary>
internal sealed class FileResult
{
    public int             Index        { get; set; }
    public string          FileName     { get; set; } = string.Empty;
    public EncodeResult    Result       { get; set; } = EncodeResult.Pending;
    public TimeSpan?       Elapsed      { get; set; }
    public long            OutSize      { get; set; } = -1L;
    public MediaInfo       InMeta       { get; set; } = new();
    public MediaInfo?      OutMeta      { get; set; }
    public string          InPath       { get; set; } = string.Empty;
    public string          OutPath      { get; set; } = string.Empty;
    public int?            ExitCode     { get; set; }
    public string?         Command      { get; set; }
    public string          ErrLine      { get; set; } = "N/A";
    public string          ErrFrame     { get; set; } = "不明";
    public StderrAnalysis? WarnInfo     { get; set; }
    public string?         DurationWarn { get; set; }
    public int             RetryCount   { get; set; }   // 通常パスでのリトライ回数
    public List<string>    RetryLog     { get; set; } = [];

    // ── 第2パス（WARN再チャレンジ）関連 ──────────────────
    /// <summary>第2パスで再チャレンジした回数（0 = 未実施）</summary>
    public int             SecondPassAttempts { get; set; }
    /// <summary>第2パス突入前の元のステータス（Warnだった証跡として残す）</summary>
    public EncodeResult?   OriginalResult     { get; set; }
    /// <summary>第2パスの履歴ログ</summary>
    public List<string>    SecondPassLog      { get; set; } = [];
}

/// <summary>コーデックファミリー判定</summary>
internal static class CodecFamily
{
    /// <summary>NVENCコーデックか（プリセット p1-p7、-tune hq、-cq、-rc が有効）</summary>
    public static bool IsNvenc(string codec) =>
        codec.EndsWith("_nvenc", StringComparison.OrdinalIgnoreCase);

    /// <summary>品質オプション名を返す（NVENC: -cq / libx264|libx265: -crf）</summary>
    public static string QualityOption(string codec) =>
        IsNvenc(codec) ? "-cq" : "-crf";

    /// <summary>プリセット値がそのコーデックで有効か</summary>
    public static bool SupportsPreset(string codec) =>
        IsNvenc(codec) ||
        codec.StartsWith("libx", StringComparison.OrdinalIgnoreCase);

    /// <summary>-tune オプションがそのコーデックで有効か（NVENCのみ）</summary>
    public static bool SupportsTune(string codec) => IsNvenc(codec);

    /// <summary>-rc オプションがそのコーデックで有効か（NVENCのみ）</summary>
    public static bool SupportsRateControl(string codec) => IsNvenc(codec);
}

/// <summary>設定値</summary>
internal sealed class AppConfig
{
    // ── FFmpeg ───────────────────────────────────────────
    public string   FfmpegPath        { get; set; } = @"C:\ffmpeg\bin\ffmpeg.exe";

    // ── 入力 ─────────────────────────────────────────────
    public string[] TargetExtensions  { get; set; } =
        ["avi","mov","mkv","mp4","m4v","wmv","flv","ts","mts","m2ts","mpeg","mpg","webm"];

    // ── 映像 ─────────────────────────────────────────────
    public string   VideoCodec        { get; set; } = "hevc_nvenc";   // H.265 デフォルト
    public string   VideoPreset       { get; set; } = "p4";
    public string   VideoTune         { get; set; } = "hq";
    public string   VideoCQ           { get; set; } = "20";           // H.265 推奨値
    public string   VideoRateControl  { get; set; } = "vbr";
    public string   VideoPixelFormat  { get; set; } = "yuv420p";
    public string   VideoMovFlags     { get; set; } = "+faststart";

    // ── 音声 ─────────────────────────────────────────────
    public string   AudioCodec        { get; set; } = "aac";
    public string   AudioBitrate      { get; set; } = "192k";

    // ── 出力検証 ─────────────────────────────────────────
    public double   DurationTolerance { get; set; } = 1.0;

    // ── リトライ（第1パス内） ─────────────────────────────
    public int      RetryMax          { get; set; } = 0;              // 0 = リトライなし
    public int      RetryDelay        { get; set; } = 5;              // 秒
    /// <summary>リトライ回ごとのオーバーライド（インデックス0 = 1回目）</summary>
    public List<RetryOverride> RetryOverrides { get; } = [];

    // ── 第2パス（WARN再チャレンジ） ───────────────────────
    public bool      SecondPassEnabled    { get; set; } = false;
    public int       SecondPassMaxRetry   { get; set; } = 3;

    // ── セッション・レジューム ────────────────────────────
    public bool      ResumeEnabled        { get; set; } = true;
    public string    SessionDir           { get; set; } = string.Empty; // 空 = 入力元/ログ出力先と同じ

    // ── stderr 一時退避 ───────────────────────────────────
    public int       MaxStderrLinesInMemory { get; set; } = 2000; // ストリーム解析の安全上限（参考値）

    // ── UI ───────────────────────────────────────────────
    /// <summary>false: カーソル制御を使わず、改行のみで進行を表示（CI/CD・非対話シェル向け）</summary>
    public bool      InteractiveUI        { get; set; } = true;
}
