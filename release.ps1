# 打包发布版：自带 .NET 运行库的单个 exe（别人电脑上不用装任何东西），连同任务栏助手 DLL 一起打成 zip。
#   powershell -ExecutionPolicy Bypass -File release.ps1
# 产物：out\GlassBox-v<版本>-win-x64.zip（解压后双击 GlassBox.exe 就能用）
param([string]$Version)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

if (-not $Version) { $Version = ([xml](Get-Content GlassBox.csproj)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1 }
$name = "GlassBox-v$Version-win-x64"
$stage = Join-Path out $name

# 任务栏透明用的 DLL：没有就先编译（要装 VS 的“使用 C++ 的桌面开发”）
if (-not (Test-Path GlassTap\bin\GlassTap.dll)) {
    cmd /c GlassTap\build.cmd
    if ($LASTEXITCODE -ne 0) { throw "GlassTap.dll 编译失败" }
}

if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
dotnet publish GlassBox.csproj -c Release -o $stage `
    -p:SelfContained=true -p:PublishSingleFile=true -p:DebugType=none -p:DebugSymbols=false
if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失败" }
# 桌面运行库包会顺带放几个 WPF 的原生 DLL，WinForms 用不到
Remove-Item (Join-Path $stage '*') -Include D3DCompiler_47_cor3.dll, PenImc_cor3.dll, PresentationNative_cor3.dll, vcruntime140_cor3.dll, wpfgfx_cor3.dll -ErrorAction SilentlyContinue

# 卸载脚本 + 说明，跟 exe 放在一起
Set-Content -Path (Join-Path $stage '卸载玻璃收纳.cmd') -Encoding Default -Value @"
@echo off
rem 还原桌面图标和任务栏、关掉开机自启；你的文件不会动。之后直接删掉这个文件夹即可。
start "" "%~dp0GlassBox.exe" --uninstall
"@
Copy-Item README.md, LICENSE (Join-Path $stage '.') -ErrorAction SilentlyContinue

$zip = Join-Path out "$name.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path $stage -DestinationPath $zip -CompressionLevel Optimal
Get-Item $zip | ForEach-Object { "{0}  {1:N1} MB" -f $_.FullName, ($_.Length / 1MB) }
