# 发布 win-x64 自包含单文件 exe 到 publish\win-x64
param([string]$Output = "")
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$out = if ($Output) { $Output } else { Join-Path $root "publish\win-x64" }
dotnet publish (Join-Path $root "src\PixivRenamer\PixivRenamer.csproj") `
  -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true -p:DebugType=None `
  -p:TargetLatestRuntimePatch=true -o $out --nologo
Write-Host "已发布到: $out"