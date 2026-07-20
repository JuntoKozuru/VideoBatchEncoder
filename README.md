# VideoBatchEncoder

動画ファイル・フォルダをドラッグ＆ドロップするだけで、NVENC（またはCPU）を使って
一括MP4変換を行う Windows 向けコンソールツールです。

大量の動画（ドライブレコーダー映像など）を安全かつ効率的にH.265/H.264へ
再エンコードし、失敗ファイルの自動リトライ、途中終了からのレジューム、
詳細な処理レポート出力までを一括で行うことを目的としています。

---

## 主な機能

- **ドラッグ＆ドロップ実行**：EXEに動画ファイル、またはフォルダをドロップするだけ
- **GPUエンコード（NVENC）標準対応**：`hevc_nvenc` / `h264_nvenc`、CPUエンコード（`libx264`/`libx265`）にも切替可能
- **進捗表示の固定2行UI**：全体進捗バー（スピナー・残り時間つき）＋現在ファイルの`frame=`進捗を、画面上の2行に固定して表示（スクロールしない）。WARN/FAIL/SKIPのみその下にログとして追記
- **非対話モード**：`InteractiveUI=false` でカーソル制御を使わない改行ログ表示に切替（CI/CD・非対話シェル向け）
- **自動リトライ**：エンコード失敗時に指定回数まで自動再試行。リトライごとにコーデック／プリセット／追加オプションを変更可能（GPU→CPUフォールバック等）
- **第2パス（WARN再チャレンジ）**：全件処理後、WARN判定になったファイルだけを対象に再エンコードを試み、偶発的エラーからの自動回復を狙う
- **セッション・レジューム**：処理状況を `encode_session_*.json` にアトミック保存。強制終了・クラッシュ後も未処理ファイルから再開可能
- **入出力の整合性チェック**：Duration（尺）の差、出力ファイルサイズ0バイト、出力メタデータ取得不可などを検知してWARN/FAILに分類
- **詳細な処理ログ**：全件サマリ表 or 全件詳細比較表（入力→出力のコーデック・解像度・FPS・ビットレート等）をコンソール＋ログファイルに出力
- **省メモリなstderr解析**：ffmpegのstderrを一時ログファイルへストリームで退避し、1パスで警告集約・フレーム欠損範囲抽出を実施。巨大ログでもメモリ消費は定常

---

## 動作環境

- Windows（`net8.0-windows` / WinFormsのフォルダ選択ダイアログを使用）
- [.NET 8 SDK](https://dotnet.microsoft.com/download)（ビルド時のみ）
- FFmpeg（`ffmpeg.exe`。NVENC使用時はNVENC対応ビルド）
- GPUエンコードを使う場合はNVIDIA GPU（`nvidia-smi` があればGPU名をログに記録）

---

## ビルド方法

`build.bat` をダブルクリック、またはコマンドプロンプトから実行してください。

```bat
build.bat
```

内部では以下を実行し、単一実行ファイル（自己完結・NativeAOTではなくself-contained）として `dist` フォルダに出力します。

```
dotnet publish -c Release -r win-x64 --self-contained true ^
  -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:DebugType=none ^
  -o "%~dp0dist"
```

ビルド後、`VideoBatchEncoder.ini` を `dist` フォルダにコピーしてから使用してください。

アイコンを設定したい場合は `VideoBatchEncoder.ico` を `VideoBatchEncoder.csproj` と同じフォルダに置き、
`csproj` 内のコメントアウトされた `ApplicationIcon` 行を有効化してから再ビルドします。

---

## 使い方

1. `VideoBatchEncoder.exe` と `VideoBatchEncoder.ini` を同じフォルダに配置
2. `VideoBatchEncoder.ini` の `[ffmpeg] Path=` にお使いのffmpegの場所を設定
3. 動画ファイル（複数可）またはフォルダを `VideoBatchEncoder.exe` にドラッグ＆ドロップ

   - ファイルとフォルダの混在ドロップは不可
   - フォルダをドロップした場合、`[input] Extensions=` に登録された拡張子のみが対象になる
   - ファイルを直接ドロップした場合は拡張子フィルタを行わず、ffmpegに認識可否を判定させる

4. 対話モード（`InteractiveUI=true`）の場合、以下を順に選択
   - ログ出力形式（1: 全件一覧＋WARN/FAIL/SKIP詳細 ／ 2: 全件詳細比較表）
   - 既存ファイルの扱い（1: 上書き ／ 2: スキップ）
   - ログ出力先フォルダ（フォルダ選択ダイアログ。キャンセル時は入力元フォルダ）
5. 出力先は入力ファイルと同じ場所の `mp4` サブフォルダ、ファイル名は `{元のファイル名}_{元の拡張子}.mp4`
6. 処理完了後、コンソールとログファイルにサマリが表示される

未完了セッションが見つかった場合、起動時にレジューム確認が入ります。レジュームを選ぶと
前回の設定（ログ形式・既存ファイル扱い）を引き継ぎ、未処理分から再開します。

---

## 設定ファイル（VideoBatchEncoder.ini）

主要な設定項目は以下の通りです。詳細なコメントは `VideoBatchEncoder.ini` 本体を参照してください。

| セクション | キー | 内容 |
|---|---|---|
| `[ffmpeg]` | `Path` | ffmpeg実行ファイルのフルパス |
| `[input]` | `Extensions` | フォルダ投入時の対象拡張子（カンマ区切り） |
| `[video]` | `Codec` | `hevc_nvenc`（既定）/ `h264_nvenc` / `libx264` / `libx265` |
| `[video]` | `Preset` | NVENC: p1〜p7 ／ libx264/265: ultrafast〜veryslow |
| `[video]` | `Tune` | NVENCのみ（hq/ll/ull/lossless） |
| `[video]` | `CQ` | NVENC時は`-cq`、libx時は`-crf` |
| `[video]` | `RateControl` | NVENCのみ（vbr/cbr/constqp） |
| `[video]` | `PixelFormat` | 既定 `yuv420p` |
| `[video]` | `MovFlags` | 既定 `+faststart` |
| `[audio]` | `Codec` / `Bitrate` | 既定 `aac` / `192k` |
| `[output]` | `DurationTolerance` | 入出力Duration差の許容秒数（超過でWARN） |
| `[retry]` | `MaxRetry` / `RetryDelay` | 自動リトライ回数と待機秒数 |
| `[retry]` | `RetryN_Preset/Options/Codec` | N回目リトライ時の設定上書き（GPU→CPU切替等） |
| `[secondpass]` | `Enabled` / `MaxRetry` | WARNファイルの再チャレンジ機能 |
| `[session]` | `ResumeEnabled` / `SessionDir` | セッション・レジューム設定 |
| `[ui]` | `InteractiveUI` | `true`=固定2行の進捗UI／`false`=改行のみのシンプル表示 |

---

## 結果の分類

| 結果 | 意味 |
|---|---|
| `PASS` | 正常完了（警告なし、Duration差も許容範囲内） |
| `WARN` | エンコードは完了したが、警告あり（フレーム欠損・Duration差超過・出力メタ取得不可等） |
| `FAIL` | ffmpegがエラー終了、または出力ファイルが生成されなかった |
| `SKIP` | 入力ファイルが認識不可、または既存出力ファイルをスキップ設定 |

WARNファイルは、`[secondpass]` を有効にすると全件処理後に自動で再チャレンジされ、
解消されれば `PASS` に昇格します（履歴はログに残ります）。

---

## プロジェクト構成

| ファイル | 役割 |
|---|---|
| `Program.cs` | エントリポイント。引数処理・設定読込・対話フロー・第1/第2パスのループ制御 |
| `Models.cs` | 設定（`AppConfig`）、ファイル結果（`FileResult`）等のデータモデル、コーデックファミリー判定 |
| `IniReader.cs` | 簡易INIパーサー |
| `FfmpegEncoder.cs` | ffmpeg引数の組み立てと、進捗パース付きプロセス実行 |
| `MediaInfoReader.cs` | `ffmpeg -i` のstderrから入出力メタデータを抽出 |
| `StderrParser.cs` | stderr一時ログのストリーム解析（警告集約・フレーム欠損検出） |
| `ProgressBar.cs` | コンソール固定2行の進捗バー描画（`BatchProgressBar`） |
| `LogWriter.cs` | コンソール／ログファイルへの同時出力、サマリ・詳細レポート整形 |
| `SessionStore.cs` | セッション状態のJSON永続化・アトミック保存・レジューム検出 |
| `VideoBatchEncoder.ini` | 設定ファイル本体 |
| `build.bat` | ビルドスクリプト（self-contained single file publish） |

---

## 注意事項

- 出力は一時的に `*.mp4.tmp` として書き出し、完了後に本来のファイル名へリネームします（途中終了時の不完全ファイル混入を防止）
- 起動時に対象フォルダ内の残存 `.tmp` ファイルは自動削除されます
- コンソールリサイズを検知すると、固定2行レイアウトは自動的に再初期化されます
