<#
.SYNOPSIS
  Fako をビルドして %LOCALAPPDATA%\Programs\Fako に配置し、起動し直す。

.DESCRIPTION
  作業フォルダーに「低整合性」の必須ラベルが付いていると、そこでビルドした exe は低整合性で起動してしまい、
  トレイに出ない・設定が保存されないなどの不具合になる。ラベルの無い場所へコピーして使う。
  （コピーしたファイルにはコピー先フォルダーの既定のラベル＝なし、が付く）
#>
param(
    [string]$Destination = (Join-Path $env:LOCALAPPDATA "Programs\Fako"),
    [switch]$NoStart
)
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$dist = Join-Path $root "dist"

# 配置先に書き込めるか、先に確かめる（C:\ 直下などは管理者権限が必要。止めてから失敗しないように）
New-Item -ItemType Directory -Force $Destination | Out-Null
$probe = Join-Path $Destination ".fako-write-test"
try { [IO.File]::WriteAllText($probe, ""); Remove-Item $probe -Force }
catch { throw "配置先に書き込めません: $Destination（管理者として実行するか、別の場所を指定してください）" }

# 動いている Fako（配置先・作業フォルダー・旧名の配置先）を終了する（exe がロックされて上書きできないため）
$oldDestination = Join-Path $env:LOCALAPPDATA "Programs\Kakoi"
Get-Process Fako,Kakoi -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -and ($_.Path -like "$Destination*" -or $_.Path -like "$root*" -or $_.Path -like "$oldDestination*") } |
    Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 500

dotnet publish (Join-Path $root "src\Fako") -c Release -r win-x64 --self-contained false `
    -p:PublishSingleFile=true -o $dist -v q -nologo
if ($LASTEXITCODE -ne 0) { throw "ビルドに失敗しました" }

New-Item -ItemType Directory -Force $Destination | Out-Null
Copy-Item (Join-Path $dist "Fako.exe") $Destination -Force
$exe = Join-Path $Destination "Fako.exe"

$label = icacls $exe | Select-String "Mandatory Label"
if ($label) { Write-Warning "配置先の exe に整合性ラベルが付いています: $label" }

Write-Host "配置しました: $exe"
if (-not $NoStart) {
    # テスト用の環境変数を引き継がないようにして起動する
    Remove-Item Env:FAKO_HOME,Env:KAKOI_HOME -ErrorAction SilentlyContinue
    Start-Process $exe
    Write-Host "起動しました"
}
