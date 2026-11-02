param(
    [Parameter(Mandatory=$true)][string]$Executable,
    [Parameter(Mandatory=$true)][string]$SourcePdf
)
$ErrorActionPreference = 'Stop'
$testRoot = Join-Path $env:LOCALAPPDATA ('Temp\AiryView-command-print-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot | Out-Null
$Executable = (Resolve-Path -LiteralPath $Executable).Path
$SourcePdf = (Resolve-Path -LiteralPath $SourcePdf).Path
$sourceHash = (Get-FileHash -LiteralPath $SourcePdf).Hash
function Run-PrintCase([string[]]$Options) {
    $argumentText = ($Options | ForEach-Object { '"' + $_ + '"' }) -join ' '
    $process = Start-Process -FilePath $Executable -ArgumentList $argumentText -WindowStyle Hidden -Wait -PassThru
    return $process.ExitCode
}
$result = Join-Path $testRoot 'success.json'
$output = Join-Path $testRoot 'printed.pdf'
$code = Run-PrintCase @('--print',$SourcePdf,'--printer','Microsoft Print to PDF','--output',$output,'--result',$result)
$report = Get-Content -LiteralPath $result -Raw | ConvertFrom-Json
if ($code -ne 0 -or -not $report.success -or $report.pages -lt 1 -or -not (Test-Path -LiteralPath $output)) { throw 'Virtual print failed' }
Write-Output ('PASS virtual print: '+$report.pages+' pages')
foreach ($caseName in @('missing-file','invalid-printer','existing-output')) {
    $result = Join-Path $testRoot ($caseName+'.json')
    $options = switch ($caseName) {
        'missing-file' { @('--print',(Join-Path $testRoot 'missing.pdf'),'--result',$result) }
        'invalid-printer' { @('--print',$SourcePdf,'--printer','AiryView-nonexistent-printer','--result',$result) }
        'existing-output' { @('--print',$SourcePdf,'--printer','Microsoft Print to PDF','--output',$SourcePdf,'--result',$result) }
    }
    $code = Run-PrintCase $options
    $report = Get-Content -LiteralPath $result -Raw | ConvertFrom-Json
    if ($code -eq 0 -or $report.success -or -not $report.error) { throw ('Failure reporting failed: '+$caseName) }
    Write-Output ('PASS '+$caseName)
}
$code = Run-PrintCase @('--print',$SourcePdf,'--result',$SourcePdf)
if ($code -eq 0 -or (Get-FileHash -LiteralPath $SourcePdf).Hash -ne $sourceHash) { throw 'Input protection failed' }
Write-Output 'PASS input protection'
Write-Output ('Results: '+$testRoot)
