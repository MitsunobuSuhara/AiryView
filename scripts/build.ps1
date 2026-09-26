param([switch]$Publish, [switch]$Test)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
Set-Location $taskRoot
$taskDotnet = Join-Path $taskRoot '.tools\dotnet\dotnet.exe'
if (!(Test-Path $taskDotnet)) { $taskDotnet = (Get-Command dotnet -ErrorAction Stop).Source }
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
& $taskDotnet build src\AiryView\AiryView.csproj -c Release -p:RestoreLockedMode=true
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
if ($Test) {
    $taskDll = Join-Path $taskRoot 'src\AiryView\bin\Release\net10.0-windows\win-x64\AiryView.dll'
    $taskRun = Start-Process -FilePath $taskDotnet -ArgumentList @($taskDll, '--self-test') -Wait -PassThru -NoNewWindow
    if ($taskRun.ExitCode -ne 0) { throw 'PDF and printing tests failed; see artifacts/test-failure.txt' }
    $taskRun = Start-Process -FilePath $taskDotnet -ArgumentList @($taskDll, '--ui-test') -Wait -PassThru -NoNewWindow
    if ($taskRun.ExitCode -ne 0) { throw 'UI tests failed; see artifacts/test-failure.txt' }
    $taskRun = Start-Process -FilePath $taskDotnet -ArgumentList @($taskDll, '--startup-test') -Wait -PassThru -NoNewWindow
    if ($taskRun.ExitCode -ne 0) { throw 'Startup tests failed; see artifacts/test-failure.txt' }
    $taskRun = Start-Process -FilePath $taskDotnet -ArgumentList @($taskDll, '--mixed-paper-test') -Wait -PassThru -NoNewWindow
    if ($taskRun.ExitCode -ne 0) { throw 'Mixed paper tests failed; see artifacts/test-failure.txt' }
}
if ($Publish) {
    & $taskDotnet publish src\AiryView\AiryView.csproj -c Release -r win-x64 --self-contained true -o artifacts\app -p:PublishReadyToRun=true
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed' }
    Copy-Item README.md artifacts\app\README.md -Force
}
