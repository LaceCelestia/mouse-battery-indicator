param(
    [switch]$Test,
    [string]$CompilerPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$projectRoot = $PSScriptRoot

if ([string]::IsNullOrWhiteSpace($CompilerPath)) {
    $compilerCommand = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($null -ne $compilerCommand) {
        $CompilerPath = $compilerCommand.Source
    }
    else {
        $userProgramDirectory = if ([string]::IsNullOrWhiteSpace($env:LOCALAPPDATA)) { '' } else { Join-Path $env:LOCALAPPDATA 'Programs' }
        $programDirectories = @($userProgramDirectory, ${env:ProgramFiles(x86)}, $env:ProgramFiles) |
            Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
            Select-Object -Unique
        foreach ($programDirectory in $programDirectories) {
            $candidate = Join-Path $programDirectory 'Inno Setup 6\ISCC.exe'
            if (Test-Path -LiteralPath $candidate -PathType Leaf) {
                $CompilerPath = $candidate
                break
            }
        }
    }
}

if ([string]::IsNullOrWhiteSpace($CompilerPath) -or
    -not (Test-Path -LiteralPath $CompilerPath -PathType Leaf)) {
    throw 'Inno Setup 6 is required. Install it from https://jrsoftware.org/isdl.php, then rerun with -CompilerPath "C:\path\to\ISCC.exe". This script does not download or install build tools.'
}
$CompilerPath = (Resolve-Path -LiteralPath $CompilerPath).Path

& (Join-Path $projectRoot 'package.ps1') -Test:$Test
if (-not $?) {
    throw 'Portable packaging failed; the installer was not built.'
}

$stagedExecutable = Join-Path $projectRoot '.work\ViperV3BatteryIndicator.exe'
$assemblyVersion = [Reflection.AssemblyName]::GetAssemblyName($stagedExecutable).Version
$version = '{0}.{1}.{2}' -f $assemblyVersion.Major, $assemblyVersion.Minor, $assemblyVersion.Build
if ($assemblyVersion.Revision -gt 0) {
    $version += '.{0}' -f $assemblyVersion.Revision
}
$artifactsDirectory = Join-Path $projectRoot 'artifacts'
$installerPath = Join-Path $artifactsDirectory ("MouseBatteryIndicator-v{0}-Setup.exe" -f $version)

$installerScript = Join-Path $projectRoot 'installer\MouseBatteryIndicator.iss'
& $CompilerPath "/DAppVersion=$version" "/DSourceRoot=$projectRoot" "/DArtifactsDir=$artifactsDirectory" $installerScript
if ($LASTEXITCODE -ne 0) {
    throw "Inno Setup compilation failed with exit code $LASTEXITCODE."
}
if (-not (Test-Path -LiteralPath $installerPath -PathType Leaf)) {
    throw "Inno Setup did not produce the expected installer: $installerPath"
}

$installerHash = (Get-FileHash -LiteralPath $installerPath -Algorithm SHA256).Hash.ToLowerInvariant()
$hashLine = '{0}  {1}' -f $installerHash, [IO.Path]::GetFileName($installerPath)
[IO.File]::WriteAllText($installerPath + '.sha256', $hashLine + [Environment]::NewLine, [Text.Encoding]::ASCII)
Write-Output "Installer: $installerPath"
Write-Output "SHA256: $installerHash"
