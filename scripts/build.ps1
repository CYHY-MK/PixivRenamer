# 构建（Debug/Release）
param([string]$Configuration = "Release")
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
dotnet build (Join-Path $root "src\PixivRenamer\PixivRenamer.csproj") -c $Configuration --nologo