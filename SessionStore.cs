using System.Text.Json;
using System.Text.Json.Serialization;

namespace VideoBatchEncoder;

/// <summary>
/// バッチ全体のセッション状態（全ファイルのプレースホルダー＋進捗）。
/// JSONとしてディスクに永続化し、強制終了後のレジュームに使う。
/// </summary>
internal sealed class SessionState
{
    public string             SessionId    { get; set; } = string.Empty; // yyyyMMdd_HHmmss
    public DateTime           StartedAt    { get; set; }
    public DateTime           UpdatedAt    { get; set; }
    public string             ProcessMode  { get; set; } = string.Empty; // "ファイル" / "フォルダ"
    public string             LogMode      { get; set; } = string.Empty; // "1" / "2"
    public string             OverwriteMode{ get; set; } = string.Empty; // "1" / "2"
    public List<FileResult>   Files        { get; set; } = [];
    /// <summary>第1パスが完了済みか（第2パスのレジューム判定に使う）</summary>
    public bool                FirstPassDone { get; set; }
}

/// <summary>
/// セッションJSONの読み書きを担当する。
///   - アトミック書き込み（*.tmp に書いてから File.Move(overwrite:true)）
///   - レジューム用の既存セッション検出
/// </summary>
internal sealed class SessionStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters    = { new JsonStringEnumConverter() },
    };

    public string SessionPath { get; }

    public SessionStore(string sessionDir, string sessionId)
    {
        Directory.CreateDirectory(sessionDir);
        SessionPath = Path.Combine(sessionDir, $"encode_session_{sessionId}.json");
    }

    public SessionStore(string explicitPath)
    {
        SessionPath = explicitPath;
    }

    /// <summary>
    /// セッションをアトミックに保存する。
    /// 同フォルダに *.tmp を書き出し、書き込み完了後に本ファイルへ置き換える。
    /// これにより保存中の強制終了でJSONが破損する事態を防ぐ。
    /// </summary>
    public void Save(SessionState state)
    {
        state.UpdatedAt = DateTime.Now;
        var tmpPath = SessionPath + ".tmp";

        var json = JsonSerializer.Serialize(state, JsonOptions);
        File.WriteAllText(tmpPath, json, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        // File.Move(overwrite:true) は内部的にファイルシステムのレベルでアトミックに近い置換を行う。
        // 同一ボリューム上であれば、置換途中の中間状態が外部から観測されることはない。
        File.Move(tmpPath, SessionPath, overwrite: true);
    }

    public static SessionState? TryLoad(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var json = File.ReadAllText(path, System.Text.Encoding.UTF8);
            return JsonSerializer.Deserialize<SessionState>(json, JsonOptions);
        }
        catch
        {
            // 破損JSON・読み取り失敗時はレジュームを諦めて null を返す
            // （アトミック書き込みにより通常は発生しないが、念のためのフェイルセーフ）
            return null;
        }
    }

    /// <summary>
    /// 指定フォルダ内で「未完了」のセッションJSONを検索する。
    /// 未完了 = FirstPassDone が false、または Pending のファイルが残っている。
    /// 複数見つかった場合は最新（StartedAt降順）を返す。
    /// </summary>
    public static SessionState? FindResumableSession(string dir)
    {
        if (!Directory.Exists(dir)) return null;

        SessionState? best = null;
        string?       bestPath = null;

        foreach (var path in Directory.GetFiles(dir, "encode_session_*.json"))
        {
            var state = TryLoad(path);
            if (state is null) continue;

            bool incomplete = !state.FirstPassDone ||
                               state.Files.Any(f => f.Result == EncodeResult.Pending);
            if (!incomplete) continue;

            if (best is null || state.StartedAt > best.StartedAt)
            {
                best     = state;
                bestPath = path;
            }
        }

        if (best != null && bestPath != null)
        {
            // 見つかったセッションの実体パスを後で使えるよう一時的にログ出力
            Console.WriteLine($"  レジューム可能なセッションを検出: {Path.GetFileName(bestPath)}");
        }

        return best;
    }

    /// <summary>
    /// 起動直後に呼ぶ。全対象ファイルの Pending プレースホルダーを高速生成して保存する。
    /// ファイル数が多い場合でも、ここではOSのファイル一覧情報だけを使い、
    /// FFmpegによるメタデータ解析（Probe）は一切行わない。
    /// </summary>
    public SessionState CreatePlaceholders(
        IReadOnlyList<string> candidateFiles,
        string                processMode,
        string                logMode,
        string                overwriteMode,
        string                sessionId)
    {
        Console.WriteLine("  対象ファイル数を計算しています...");

        var state = new SessionState
        {
            SessionId     = sessionId,
            StartedAt     = DateTime.Now,
            ProcessMode   = processMode,
            LogMode       = logMode,
            OverwriteMode = overwriteMode,
        };

        for (int i = 0; i < candidateFiles.Count; i++)
        {
            var inPath  = candidateFiles[i];
            var inExt   = Path.GetExtension(inPath).TrimStart('.');
            var stem    = Path.GetFileNameWithoutExtension(inPath);
            var outStem = inExt.Length > 0 ? $"{stem}_{inExt}" : stem;
            var outDir  = Path.Combine(Path.GetDirectoryName(inPath) ?? ".", "mp4");
            var outPath = Path.Combine(outDir, $"{outStem}.mp4");

            state.Files.Add(new FileResult
            {
                Index    = i + 1,
                FileName = Path.GetFileName(inPath),
                Result   = EncodeResult.Pending,
                InPath   = inPath,
                OutPath  = outPath,
            });
        }

        Save(state);
        Console.WriteLine($"  {state.Files.Count}件のファイルをセッションに登録しました。");
        return state;
    }
}
