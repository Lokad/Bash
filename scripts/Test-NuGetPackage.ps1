[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PackagePath,
    [string]$OutputDirectory = 'artifacts/package-validation'
)

$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot
$package = (Resolve-Path -LiteralPath $PackagePath).Path
$outputRoot = [System.IO.Path]::GetFullPath((Join-Path $repository $OutputDirectory))
$runDirectory = Join-Path $outputRoot ([Guid]::NewGuid().ToString('N'))
$feed = Join-Path $runDirectory 'feed'
$cache = Join-Path $runDirectory 'packages'
[void][System.IO.Directory]::CreateDirectory($feed)
$assemblyHashes = @{}

$archive = [System.IO.Compression.ZipFile]::OpenRead($package)
try {
    $nuspec = @($archive.Entries | Where-Object FullName -like '*.nuspec')
    if ($nuspec.Count -ne 1) { throw 'Expected one NuGet specification.' }
    $reader = [System.IO.StreamReader]::new($nuspec[0].Open())
    try { [xml]$specification = $reader.ReadToEnd() } finally { $reader.Dispose() }
    $metadata = $specification.SelectSingleNode('/*[local-name()="package"]/*[local-name()="metadata"]')
    $id = $metadata.SelectSingleNode('*[local-name()="id"]').InnerText
    $version = $metadata.SelectSingleNode('*[local-name()="version"]').InnerText
    if ($id -ne 'Lokad.Bash' -or $version -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$') {
        throw 'Unexpected package identity.'
    }
    if ($metadata.SelectNodes('.//*[local-name()="dependency"]').Count -ne 0) {
        throw 'The runtime NuGet must have no package dependencies.'
    }
    foreach ($framework in @('net10.0', 'net11.0')) {
        $entry = $archive.GetEntry("lib/$framework/Lokad.Bash.dll")
        if ($null -eq $entry) { throw "Missing $framework assembly." }
        $stream = $entry.Open()
        try {
            $assemblyHashes[$framework] = [Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData($stream)).ToLowerInvariant()
        } finally { $stream.Dispose() }
    }
} finally { $archive.Dispose() }

Copy-Item -LiteralPath $package -Destination (Join-Path $feed "Lokad.Bash.$version.nupkg")
Copy-Item -LiteralPath (Join-Path $repository 'validation/PackageConsumer.cs') -Destination (Join-Path $runDirectory 'Program.cs')
$projectPath = Join-Path $runDirectory 'Consumer.csproj'
# Explicit SDK imports prevent repository build policy or a project reference from masking the installed-package boundary.
$project = @"
<Project>
  <PropertyGroup>
    <ImportDirectoryBuildProps>false</ImportDirectoryBuildProps>
    <ImportDirectoryBuildTargets>false</ImportDirectoryBuildTargets>
  </PropertyGroup>
  <Import Project="Sdk.props" Sdk="Microsoft.NET.Sdk" />
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFrameworks>net10.0;net11.0</TargetFrameworks>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup><PackageReference Include="Lokad.Bash" Version="$version" /></ItemGroup>
  <Import Project="Sdk.targets" Sdk="Microsoft.NET.Sdk" />
</Project>
"@
[System.IO.File]::WriteAllText($projectPath, $project.Replace("`r`n", "`n"), [System.Text.UTF8Encoding]::new($false))
& dotnet restore $projectPath --source $feed --packages $cache --no-cache --nologo --verbosity quiet
if ($LASTEXITCODE -ne 0) { throw 'Isolated package restore failed.' }
$results = foreach ($framework in @('net10.0', 'net11.0')) {
    $log = Join-Path $runDirectory "$framework.log"
    & dotnet run --project $projectPath --framework $framework --configuration Release --no-restore --verbosity quiet *> $log
    if ($LASTEXITCODE -ne 0) {
        Get-Content -LiteralPath $log | Out-Host
        throw "Installed consumer failed on $framework."
    }
    $lines = @(Get-Content -LiteralPath $log | Where-Object { $_.StartsWith('{') })
    if ($lines.Count -ne 1) { throw 'Expected exactly one consumer result.' }
    $result = $lines[0] | ConvertFrom-Json
    if (-not $result.passed) { throw 'Consumer did not report success.' }
    if ($result.runtimeMajor -ne ([Version]$framework.Substring(3)).Major) {
        throw 'Consumer ran on a different major runtime.'
    }
    $assembly = Join-Path $runDirectory "bin/Release/$framework/Lokad.Bash.dll"
    if ((Get-FileHash -LiteralPath $assembly -Algorithm SHA256).Hash.ToLowerInvariant() -ne $assemblyHashes[$framework]) {
        throw 'Consumer loaded a different runtime assembly.'
    }
    [pscustomobject]@{ framework = $framework; assemblySha256 = $assemblyHashes[$framework]; result = $result }
}
$manifest = [ordered]@{
    schemaVersion = 1
    packageId = $id
    packageVersion = $version
    packageSha256 = (Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash.ToLowerInvariant()
    sdk = (& dotnet --version).Trim()
    os = [System.Runtime.InteropServices.RuntimeInformation]::OSDescription
    consumerSha256 = (Get-FileHash -LiteralPath (Join-Path $runDirectory 'Program.cs') -Algorithm SHA256).Hash.ToLowerInvariant()
    results = @($results)
}
$manifestPath = Join-Path $runDirectory 'results.json'
[System.IO.File]::WriteAllText($manifestPath, ($manifest | ConvertTo-Json -Depth 8).Replace("`r`n", "`n"), [System.Text.UTF8Encoding]::new($false))
Write-Host "Installed Lokad.Bash $version verified on .NET 10 and .NET 11. Evidence: $manifestPath"
