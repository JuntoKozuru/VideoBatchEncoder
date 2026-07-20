@echo off
setlocal
echo ============================================================
echo  VideoBatchEncoder ビルドスクリプト
echo ============================================================
echo.

rem .NET SDK の存在確認
rem where で実体パスを確認する方式を使う
where dotnet >nul 2>nul
set "DOTNET_FOUND=%ERRORLEVEL%"

if not "%DOTNET_FOUND%"=="0" (
    echo [ERROR] dotnet コマンドが見つかりません。
    echo         .NET SDK がインストールされていない、または PATH が通っていません。
    echo.
    echo         対処方法:
    echo           1. https://dotnet.microsoft.com/download から
    echo              .NET 8 SDK をダウンロードしてインストール
    echo           2. インストール後、コマンドプロンプトを開き直して
    echo              dotnet --version を実行し、8.0.x のような表示が出るか確認
    echo           3. それでも見つからない場合はPCを再起動してから再実行
    echo.
    pause
    exit /b 1
)

rem SDK本体の確認
rem dotnet コマンド自体はあっても publish ができない場合があるため別途確認する
dotnet --list-sdks > "%TEMP%\VideoBatchEncoder_sdks.txt" 2>nul
for %%A in ("%TEMP%\VideoBatchEncoder_sdks.txt") do set "SDK_FILE_SIZE=%%~zA"
del "%TEMP%\VideoBatchEncoder_sdks.txt" >nul 2>nul

if "%SDK_FILE_SIZE%"=="0" (
    echo [ERROR] .NET SDK が見つかりません。Runtimeのみインストールされている可能性があります。
    echo         https://dotnet.microsoft.com/download から .NET SDK をインストールしてください。
    echo         Runtime ではなく SDK を選択してください。
    echo.
    pause
    exit /b 1
)

echo [1/2] ビルド中...
echo.

dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:DebugType=none -o "%~dp0dist"

set "BUILD_RESULT=%ERRORLEVEL%"

if not "%BUILD_RESULT%"=="0" (
    echo.
    echo [ERROR] ビルドに失敗しました。終了コード: %BUILD_RESULT%
    echo         上記のエラーメッセージを確認してください。
    echo.
    pause
    exit /b 1
)

rem 実際にexeが生成されたかも確認する
if not exist "%~dp0dist\VideoBatchEncoder.exe" (
    echo.
    echo [ERROR] ビルドは終了しましたが VideoBatchEncoder.exe が生成されていません。
    echo         上記のログを確認してください。
    echo.
    pause
    exit /b 1
)

echo.
echo [2/2] 完了しました。
echo.
echo 出力先: %~dp0dist\VideoBatchEncoder.exe
echo.
echo VideoBatchEncoder.ini を dist フォルダにコピーして使用してください。
echo.

echo ------------------------------------------------------------
echo  アイコンを設定する場合:
echo    1. VideoBatchEncoder.ico を VideoBatchEncoder.csproj と同じフォルダに配置
echo    2. VideoBatchEncoder.csproj の以下の行のコメントを外す
echo       ApplicationIcon タグの行
echo    3. このスクリプトを再実行する
echo ------------------------------------------------------------
echo.
pause
exit /b 0
