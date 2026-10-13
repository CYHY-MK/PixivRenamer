# 生成 dist 便携目录、ZIP 和免安装部署脚本
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$pub = Join-Path $root "publish\win-x64"
$exe = Join-Path $pub "PixivRenamer.exe"
if (-not (Test-Path $exe)) { throw "请先运行 scripts\publish.ps1" }

$app = Join-Path $root "dist\PixivRenamer"
# 先把用户自己填过的配置（代理 / API Key / Cookie）备份出来：dist 是实际运行目录，
# 重新打包绝不能把它覆盖成模板。
$cfgTarget = Join-Path $app "PixivRenamer.json"
$cfgBackup = $null
if (Test-Path $cfgTarget) {
  $cfgBackup = [System.IO.File]::ReadAllBytes($cfgTarget)
  Write-Host "检测到已有配置，打包后会原样保留。"
}
if (Test-Path $app) { Remove-Item $app -Recurse -Force }
New-Item -ItemType Directory -Force -Path $app, (Join-Path $app "Logs"), (Join-Path $app "cache"), (Join-Path $app "error-reports") | Out-Null

Copy-Item $exe (Join-Path $app "PixivRenamer.exe") -Force
if ($cfgBackup) {
  [System.IO.File]::WriteAllBytes($cfgTarget, $cfgBackup)
  Write-Host "已保留原配置：$cfgTarget"
} else {
  Copy-Item (Join-Path $root "PixivRenamer.json") $app -Force
}
Copy-Item (Join-Path $root "使用说明.txt") $app -Force
Copy-Item (Join-Path $root "README.md") $app -Force
Copy-Item (Join-Path $root "CHANGELOG.md") $app -Force
Copy-Item (Join-Path $root "LICENSE") $app -Force
[System.IO.File]::WriteAllText((Join-Path $app "Logs\日志说明.txt"), "运行日志保存在此目录。`r`n", [System.Text.UTF8Encoding]::new($true))
[System.IO.File]::WriteAllText((Join-Path $app "cache\缓存说明.txt"), "查询缓存保存在此目录，可安全删除。`r`n", [System.Text.UTF8Encoding]::new($true))
[System.IO.File]::WriteAllText((Join-Path $app "error-reports\说明.txt"), "网络/代理出错时，程序会把诊断报告写在这个目录，出问题时可把报告发回定位。`r`n", [System.Text.UTF8Encoding]::new($true))

$installBat = @"
@echo off
setlocal
set "TARGET=%LOCALAPPDATA%\Programs\PixivRenamer"
if not exist "%~dp0PixivRenamer\PixivRenamer.exe" (
  echo [ERROR] PixivRenamer folder not found.
  pause
  exit /b 1
)
xcopy "%~dp0PixivRenamer\*" "%TARGET%\" /E /I /Y >nul
powershell -NoProfile -ExecutionPolicy Bypass -Command "`$s=(New-Object -ComObject WScript.Shell).CreateShortcut([Environment]::GetFolderPath('Programs')+'\PixivRenamer.lnk'); `$s.TargetPath='%TARGET%\PixivRenamer.exe'; `$s.WorkingDirectory='%TARGET%'; `$s.IconLocation='%TARGET%\PixivRenamer.exe'; `$s.Save()"
echo [OK] Installed to %TARGET%
pause
"@
$uninstallBat = @"
@echo off
setlocal
set "TARGET=%LOCALAPPDATA%\Programs\PixivRenamer"
del "%APPDATA%\Microsoft\Windows\Start Menu\Programs\PixivRenamer.lnk" >nul 2>nul
for /l %%i in (1,1,6) do (
  if exist "%TARGET%" (
    taskkill /f /im PixivRenamer.exe >nul 2>nul
    del /f /q "%TARGET%\PixivRenamer.exe" >nul 2>nul
    rd /s /q "%TARGET%" >nul 2>nul
    timeout /t 1 >nul
  )
)
if exist "%TARGET%" (
  echo [WARN] Some files are still locked. Please close PixivRenamer and run again.
) else (
  echo [OK] Removed %TARGET%
)
pause
"@
# .bat 用带 BOM 的 UTF-8 写出：cmd 在默认代码页下也能正确显示中文
[System.IO.File]::WriteAllText((Join-Path $root "dist\安装到本用户.bat"), $installBat, (New-Object System.Text.UTF8Encoding($true)))
[System.IO.File]::WriteAllText((Join-Path $root "dist\卸载本用户安装.bat"), $uninstallBat, (New-Object System.Text.UTF8Encoding($true)))

$zip = Join-Path $root "dist\PixivRenamer_v1.0.0_便携版.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path $app -DestinationPath $zip -Force
Write-Host "便携目录: $app"
Write-Host "便携 ZIP: $zip"