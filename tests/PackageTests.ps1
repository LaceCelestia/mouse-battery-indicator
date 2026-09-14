[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$PackagePath,
    [string]$HashPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$assertionCount = 0

function Assert-Package {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) {
        throw "Package test failed: $Message"
    }
    $script:assertionCount++
}

$projectDirectory = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$PackagePath = [System.IO.Path]::GetFullPath((Resolve-Path -LiteralPath $PackagePath).ProviderPath)
if ([string]::IsNullOrWhiteSpace($HashPath)) {
    $HashPath = "$PackagePath.sha256"
}
$HashPath = [System.IO.Path]::GetFullPath((Resolve-Path -LiteralPath $HashPath).ProviderPath)
$packageName = [System.IO.Path]::GetFileName($PackagePath)
$firstStepsRelativePath = 'docs/' + (-join [char[]]@(0x306F, 0x3058, 0x3081, 0x306B)) + '.txt'
$expectedEntries = @('MouseBatteryIndicator.exe', 'README.md', $firstStepsRelativePath)

Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [System.IO.Compression.ZipFile]::OpenRead($PackagePath)
try {
    $entryNames = @($archive.Entries | ForEach-Object { $_.FullName })
    Assert-Package ($entryNames.Count -eq $expectedEntries.Count) 'The ZIP must contain only the three approved distribution files.'
    foreach ($expectedEntry in $expectedEntries) {
        Assert-Package (@($entryNames | Where-Object { $_ -ceq $expectedEntry }).Count -eq 1) "The ZIP must contain exactly one $expectedEntry entry, preserving Unicode names."
    }
    foreach ($entry in $archive.Entries) {
        Assert-Package ($entry.Length -gt 0) "The packaged file must not be empty: $($entry.FullName)"
        Assert-Package ($entry.FullName -notmatch '(^[\\/]|^[A-Za-z]:|(^|[\\/])\.\.([\\/]|$))') 'All ZIP entries must be safe relative paths.'
    }
}
finally {
    $archive.Dispose()
}

$actualHash = (Get-FileHash -LiteralPath $PackagePath -Algorithm SHA256).Hash.ToLowerInvariant()
$hashRecord = [System.IO.File]::ReadAllText($HashPath).TrimEnd([char]13, [char]10)
Assert-Package ($hashRecord -ceq "$actualHash  $packageName") 'The SHA256 sidecar must match both the archive contents and its exact file name.'

$workDirectory = Join-Path $projectDirectory '.work'
New-Item -ItemType Directory -Path $workDirectory -Force | Out-Null
$extractionDirectory = Join-Path $workDirectory ('package-test-' + [Guid]::NewGuid().ToString('N'))
try {
    [System.IO.Compression.ZipFile]::ExtractToDirectory($PackagePath, $extractionDirectory)
    Assert-Package ((Test-Path -LiteralPath $extractionDirectory -PathType Container)) 'The ZIP must extract into a separate directory.'
    $extractedFiles = @(Get-ChildItem -LiteralPath $extractionDirectory -Recurse -File)
    Assert-Package ($extractedFiles.Count -eq $expectedEntries.Count) 'Extraction must not introduce additional files.'
    foreach ($expectedEntry in $expectedEntries) {
        Assert-Package ((Test-Path -LiteralPath (Join-Path $extractionDirectory $expectedEntry) -PathType Leaf)) "The extracted file must exist: $expectedEntry"
    }

    $extractedExecutable = Join-Path $extractionDirectory 'MouseBatteryIndicator.exe'
    $extractedAssemblyName = [System.Reflection.AssemblyName]::GetAssemblyName($extractedExecutable)
    Assert-Package ($extractedAssemblyName.Name -ceq 'MouseBatteryIndicator') 'The assembly identity must be MouseBatteryIndicator; renaming a legacy executable is not sufficient.'
    $extractedVersion = $extractedAssemblyName.Version
    $versionMatch = [regex]::Match($packageName, '^MouseBatteryIndicator-v(\d+\.\d+\.\d+(?:\.\d+)?)-windows\.zip$')
    Assert-Package $versionMatch.Success 'The ZIP name must identify the product, assembly version, and Windows platform.'
    $versionInName = [Version]$versionMatch.Groups[1].Value
    $normalizedRevision = [Math]::Max(0, $versionInName.Revision)
    Assert-Package ($extractedVersion.Major -eq $versionInName.Major -and $extractedVersion.Minor -eq $versionInName.Minor -and $extractedVersion.Build -eq $versionInName.Build -and $extractedVersion.Revision -eq $normalizedRevision) 'The executable assembly version must match the package file name.'

    $fileVersionInfo = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($extractedExecutable)
    Assert-Package ($fileVersionInfo.OriginalFilename -ceq 'MouseBatteryIndicator.exe') 'The original executable filename in Windows file properties must use the generic product name.'
    Assert-Package (-not [string]::IsNullOrWhiteSpace($fileVersionInfo.FileDescription) -and $fileVersionInfo.FileDescription -notmatch 'Viper') 'The Windows file description must exist and must not imply Viper-only support.'
    Assert-Package (-not [string]::IsNullOrWhiteSpace($fileVersionInfo.ProductName) -and $fileVersionInfo.ProductName -notmatch 'Viper') 'The Windows product name must exist and must not imply Viper-only support.'

    # Inspect metadata without executing the application or locking the extracted file.
    $metadataAssembly = [System.Reflection.Assembly]::ReflectionOnlyLoad([System.IO.File]::ReadAllBytes($extractedExecutable))
    foreach ($providerType in @('RazerBatteryReader', 'LogitechBatteryReader', 'XenicsBatteryReader', 'WindowsBatteryReader')) {
        Assert-Package ($null -ne $metadataAssembly.GetType("ViperBattery.$providerType", $false, $false)) "The distribution must include the $providerType provider."
    }

    $buildExecutable = Join-Path $workDirectory 'MouseBatteryIndicator.exe'
    Assert-Package ((Get-FileHash -LiteralPath $extractedExecutable -Algorithm SHA256).Hash -eq (Get-FileHash -LiteralPath $buildExecutable -Algorithm SHA256).Hash) 'The packaged executable must equal the staged build.'
    foreach ($documentName in @('README.md', $firstStepsRelativePath)) {
        $sourceHash = (Get-FileHash -LiteralPath (Join-Path $projectDirectory $documentName) -Algorithm SHA256).Hash
        $extractedHash = (Get-FileHash -LiteralPath (Join-Path $extractionDirectory $documentName) -Algorithm SHA256).Hash
        Assert-Package ($sourceHash -eq $extractedHash) "The packaged document must equal the source document: $documentName"
    }
}
finally {
    if (Test-Path -LiteralPath $extractionDirectory) {
        $resolvedWorkDirectory = [System.IO.Path]::GetFullPath((Resolve-Path -LiteralPath $workDirectory).ProviderPath).TrimEnd('\') + '\'
        $resolvedExtractionDirectory = [System.IO.Path]::GetFullPath((Resolve-Path -LiteralPath $extractionDirectory).ProviderPath)
        if (-not $resolvedExtractionDirectory.StartsWith($resolvedWorkDirectory, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to remove a test directory outside .work: $resolvedExtractionDirectory"
        }
        Remove-Item -LiteralPath $resolvedExtractionDirectory -Recurse -Force
    }
}

Write-Host "Package tests passed: $assertionCount assertions."
