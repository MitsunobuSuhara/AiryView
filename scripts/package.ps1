param()
$ErrorActionPreference = "Stop"
$taskRoot = Split-Path -Parent $PSScriptRoot
Set-Location $taskRoot
if (!(Test-Path "artifacts/app/Helper/airyview-pdf-helper.exe")) { throw "Run build-pdf-helper.ps1 and build.ps1 -Test -Publish first." }
$taskRelease = Join-Path $taskRoot ("artifacts/release/AiryView-2.0.11-" + (Get-Date -Format "yyyyMMdd-HHmmss"))
New-Item -ItemType Directory -Path $taskRelease -Force | Out-Null
Copy-Item -LiteralPath "artifacts/app" -Destination (Join-Path $taskRelease "app") -Recurse
$taskSetup = @"
@echo off
chcp 65001 >nul
start "" "%~dp0app\AiryView.exe" --install
"@
$taskSetup = $taskSetup.Replace([string][char]13, [string]::Empty).Replace([string][char]10,[string][char]13+[char]10)
$taskSetupName = "AiryViewをセットアップ.cmd"
[IO.File]::WriteAllText((Join-Path $taskRelease $taskSetupName), $taskSetup, [Text.UTF8Encoding]::new($false))
$taskGuide = @(
    'AiryView のセットアップ',
    '',
    'このフォルダでは、次の1つだけ行ってください。',
    '',
    '  AiryViewをセットアップ.cmd をダブルクリック',
    '',
    'Windowsの確認画面が出たら、内容を確認して「はい」を選びます。',
    '完了画面が出たら、このフォルダは閉じて大丈夫です。',
    '以後はスタートメニューの AiryView から開けます。',
    '',
    '更新するときも、AiryViewを閉じてから同じファイルをダブルクリックしてください。'
) -join [Environment]::NewLine
[IO.File]::WriteAllText((Join-Path $taskRelease "はじめにお読みください.txt"), $taskGuide, [Text.UTF8Encoding]::new($false))
Copy-Item -LiteralPath README.md -Destination (Join-Path $taskRelease "README.md")
Copy-Item -LiteralPath LICENSE -Destination (Join-Path $taskRelease "LICENSE")
$taskZip = $taskRelease + ".zip"
Compress-Archive -LiteralPath (Join-Path $taskRelease "app"), (Join-Path $taskRelease $taskSetupName), (Join-Path $taskRelease "はじめにお読みください.txt"), (Join-Path $taskRelease "README.md"), (Join-Path $taskRelease "LICENSE") -DestinationPath $taskZip
Get-FileHash -LiteralPath $taskZip -Algorithm SHA256 | Format-List
$taskZip
