# ビルド方法

WindowsのPowerShellとdotnetホスト、.NET 6で動くRoslyn C#コンパイラを使います。既存のBuild.ps1はMSBuildプロジェクトではなく、csc.dllを直接呼び出します。

必要なものは次の通りです。

- 正規インストールした対応ゲーム
- BepInEx Unity IL2CPP 6.0.0-be.788+5b766a3
- BepInExを初回起動して生成されたBepInEx/interop
- BepInEx付属dotnetランタイムのDLLフォルダー
- dotnetホストとcsc.dll（Microsoft.Net.Compilers.Toolsetのtasks/netcore/bincoreなど）

```powershell
.\src\Build.ps1 `
  -CompilerDll 'コンパイラの場所\csc.dll' `
  -RuntimeDirectory 'ゲームフォルダー\dotnet' `
  -BepInExCoreDirectory 'ゲームフォルダー\BepInEx\core' `
  -InteropDirectory 'ゲームフォルダー\BepInEx\interop' `
  -OutputFile '.\ShirenAsciiProbe.dll'
```

出力DLLとconfig/ui-settings.jsonをREADME記載の場所へ配置します。コンパイラ、ランタイム、BepInEx、ゲーム由来の参照DLLはこのリポジトリに含みません。

## 互換性を追加する場合

現在のコードはゲームファイルのSHA-256と特定のIL2CPP型・フィールド構造に依存します。別ビルドのハッシュを追加するだけでは動作確認になりません。LiveMapとReaderのフィールド、配列やフラグの意味、interop参照の一致を確認し、描画と階層移動の実機検証が必要です。現在のファイル照合を外して一般公開することは想定していません。
