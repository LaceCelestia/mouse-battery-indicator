[CmdletBinding()]
param(
    [switch]$Test,
    [switch]$StageOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$projectDirectory = $PSScriptRoot
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) {
    $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
if (-not (Test-Path -LiteralPath $compiler)) {
    throw '.NET Framework 4.8 is required. The Windows C# compiler was not found.'
}

$outputDirectory = Join-Path $projectDirectory 'dist'
$workDirectory = Join-Path $projectDirectory '.work'
New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
New-Item -ItemType Directory -Path $workDirectory -Force | Out-Null

$commonArguments = @(
    '/nologo',
    '/langversion:5',
    '/platform:anycpu',
    '/optimize+',
    '/warn:4',
    '/reference:System.dll',
    '/reference:System.Core.dll',
    '/reference:System.Drawing.dll',
    '/reference:System.Windows.Forms.dll',
    '/reference:System.Xml.dll'
)

$sourceFiles = @(Get-ChildItem -LiteralPath (Join-Path $projectDirectory 'src') -Filter '*.cs' -File |
    Sort-Object Name | ForEach-Object { $_.FullName })
if ($sourceFiles.Count -eq 0) {
    throw 'No C# source files were found in src.'
}
$manifestPath = Join-Path $projectDirectory 'app.manifest'

if ($Test) {
    foreach ($testName in @('ProtocolTests', 'LogitechProtocolTests', 'XenicsProtocolTests', 'WindowsBatteryTests', 'HidInventoryTests', 'ServiceTests', 'HistoryTests', 'UiTests')) {
        $testSource = Join-Path $projectDirectory "tests\$testName.cs"
        if (-not (Test-Path -LiteralPath $testSource)) {
            throw "Required test source is missing: $testSource"
        }
        $testExecutable = Join-Path $workDirectory "$testName.exe"
        $testArguments = $commonArguments + @('/target:exe', "/main:$testName", "/out:$testExecutable", "/win32manifest:$manifestPath", $testSource) + $sourceFiles
        & $compiler @testArguments
        if ($LASTEXITCODE -ne 0) {
            throw "$testName compilation failed (exit code $LASTEXITCODE)."
        }
        & $testExecutable
        if ($LASTEXITCODE -ne 0) {
            throw "$testName failed (exit code $LASTEXITCODE)."
        }
    }
}

$stagedExecutable = Join-Path $workDirectory 'MouseBatteryIndicator.exe'
$outputExecutable = Join-Path $outputDirectory 'MouseBatteryIndicator.exe'
$buildArguments = $commonArguments + @('/target:winexe', "/out:$stagedExecutable", "/win32manifest:$manifestPath") + $sourceFiles
& $compiler @buildArguments
if ($LASTEXITCODE -ne 0) {
    throw "Application compilation failed (exit code $LASTEXITCODE)."
}
if ($StageOnly) {
    Write-Host "Staged: $stagedExecutable"
}
else {
    Copy-Item -LiteralPath $stagedExecutable -Destination $outputExecutable -Force
    Write-Host "Built: $outputExecutable"
}
