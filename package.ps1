[CmdletBinding()]
param(
    [switch]$Test
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$projectDirectory = [System.IO.Path]::GetFullPath($PSScriptRoot)
$workDirectory = Join-Path $projectDirectory '.work'
$artifactDirectory = Join-Path $projectDirectory 'artifacts'

# Build in .work so packaging never replaces the running application in dist.
& (Join-Path $projectDirectory 'build.ps1') -Test:$Test -StageOnly
$stagedExecutable = Join-Path $workDirectory 'MouseBatteryIndicator.exe'
$assemblyVersion = [System.Reflection.AssemblyName]::GetAssemblyName($stagedExecutable).Version
$packageVersion = '{0}.{1}.{2}' -f $assemblyVersion.Major, $assemblyVersion.Minor, $assemblyVersion.Build
if ($assemblyVersion.Revision -gt 0) {
    $packageVersion += '.{0}' -f $assemblyVersion.Revision
}

$packageName = "MouseBatteryIndicator-v$packageVersion-windows.zip"
$packagePath = Join-Path $artifactDirectory $packageName
$hashPath = "$packagePath.sha256"
$firstStepsRelativePath = 'docs/' + (-join [char[]]@(0x306F, 0x3058, 0x3081, 0x306B)) + '.txt'
$includedFiles = @(
    [pscustomobject]@{ Source = $stagedExecutable; RelativePath = 'MouseBatteryIndicator.exe' },
    [pscustomobject]@{ Source = (Join-Path $projectDirectory 'README.md'); RelativePath = 'README.md' },
    [pscustomobject]@{ Source = (Join-Path $projectDirectory $firstStepsRelativePath); RelativePath = $firstStepsRelativePath }
)
foreach ($includedFile in $includedFiles) {
    if (-not (Test-Path -LiteralPath $includedFile.Source -PathType Leaf)) {
        throw "Required package file is missing: $($includedFile.Source)"
    }
}

New-Item -ItemType Directory -Path $artifactDirectory -Force | Out-Null
$stageDirectory = Join-Path $workDirectory ('package-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $stageDirectory | Out-Null

try {
    $contentDirectory = Join-Path $stageDirectory 'content'
    New-Item -ItemType Directory -Path $contentDirectory | Out-Null
    foreach ($includedFile in $includedFiles) {
        $destination = Join-Path $contentDirectory $includedFile.RelativePath
        New-Item -ItemType Directory -Path ([System.IO.Path]::GetDirectoryName($destination)) -Force | Out-Null
        Copy-Item -LiteralPath $includedFile.Source -Destination $destination
    }

    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $stagedPackage = Join-Path $stageDirectory $packageName
    $packageStream = [System.IO.File]::Open($stagedPackage, [System.IO.FileMode]::CreateNew)
    try {
        $archive = [System.IO.Compression.ZipArchive]::new($packageStream, [System.IO.Compression.ZipArchiveMode]::Create, $false, [System.Text.Encoding]::UTF8)
        try {
            foreach ($includedFile in $includedFiles) {
                [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
                    $archive,
                    (Join-Path $contentDirectory $includedFile.RelativePath),
                    $includedFile.RelativePath,
                    [System.IO.Compression.CompressionLevel]::Optimal
                ) | Out-Null
            }
        }
        finally {
            $archive.Dispose()
        }
    }
    finally {
        $packageStream.Dispose()
    }
    $packageHash = (Get-FileHash -LiteralPath $stagedPackage -Algorithm SHA256).Hash.ToLowerInvariant()
    $stagedHash = "$stagedPackage.sha256"
    [System.IO.File]::WriteAllText($stagedHash, "$packageHash  $packageName`r`n", [System.Text.Encoding]::ASCII)

    if ($Test) {
        & (Join-Path $projectDirectory 'tests\PackageTests.ps1') -PackagePath $stagedPackage -HashPath $stagedHash
    }

    Move-Item -LiteralPath $stagedPackage -Destination $packagePath -Force
    Move-Item -LiteralPath $stagedHash -Destination $hashPath -Force
    Write-Host "Packaged: $packagePath"
    Write-Host "SHA256: $hashPath"
}
finally {
    # Validate the absolute target before recursively deleting only this stage.
    $resolvedWorkDirectory = [System.IO.Path]::GetFullPath((Resolve-Path -LiteralPath $workDirectory).ProviderPath).TrimEnd('\') + '\'
    $resolvedStageDirectory = [System.IO.Path]::GetFullPath((Resolve-Path -LiteralPath $stageDirectory).ProviderPath)
    if (-not $resolvedStageDirectory.StartsWith($resolvedWorkDirectory, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to remove a staging directory outside .work: $resolvedStageDirectory"
    }
    Remove-Item -LiteralPath $resolvedStageDirectory -Recurse -Force
}
