param(
    [Parameter(Mandatory=$true)][string]$CompilerDll,
    [Parameter(Mandatory=$true)][string]$RuntimeDirectory,
    [Parameter(Mandatory=$true)][string]$BepInExCoreDirectory,
    [Parameter(Mandatory=$true)][string]$InteropDirectory,
    [string]$OutputFile = (Join-Path (Split-Path $PSScriptRoot) 'ShirenAsciiProbe.dll')
)
$ErrorActionPreference = 'Stop'
$references = @()
foreach ($file in Get-ChildItem -LiteralPath $RuntimeDirectory -Filter '*.dll') {
    try { [Reflection.AssemblyName]::GetAssemblyName($file.FullName) | Out-Null; $references += $file.FullName } catch {}
}
foreach ($name in @('BepInEx.Core.dll','BepInEx.Unity.IL2CPP.dll','0Harmony.dll','Il2CppInterop.Runtime.dll')) {
    $references += (Join-Path $BepInExCoreDirectory $name)
}
foreach ($name in @('Il2Cppmscorlib.dll','UnityEngine.CoreModule.dll','UnityEngine.IMGUIModule.dll','UnityEngine.TextRenderingModule.dll','UnityEngine.UIModule.dll','UnityEngine.UI.dll','Unity.TextMeshPro.dll','Assembly-CSharp.dll','F7.Localization.Runtime.dll')) {
    $references += (Join-Path $InteropDirectory $name)
}
$argsForCompiler = @('-nologo','-target:library','-langversion:latest','-nullable:enable','-optimize+','-nostdlib+',('-out:'+$OutputFile))
$argsForCompiler += $references | ForEach-Object { '-r:'+$_ }
$argsForCompiler += (Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.cs').FullName
& dotnet $CompilerDll @argsForCompiler
if ($LASTEXITCODE -ne 0) { throw 'Compilation failed.' }
Get-FileHash -LiteralPath $OutputFile | Select-Object Path,Hash
