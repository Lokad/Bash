# Correctness verification only. No comparative timings are collected.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ReferencePath,
    [string]$WslDistribution,
    [string]$OutputPath = 'artifacts/conformance/results.json'
)

$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot
$catalogPath = Join-Path $repository 'tests/Lokad.Bash.Tests/Conformance/cases.json'
$catalog = Get-Content -LiteralPath $catalogPath -Raw -Encoding utf8 | ConvertFrom-Json
if ($catalog.schemaVersion -ne 1) { throw 'Unsupported conformance catalog schema.' }

function Invoke-Reference([string[]]$Arguments, [string]$Locale, [byte[]]$InputBytes, [string]$WorkingDirectory) {
    $start = [System.Diagnostics.ProcessStartInfo]::new()
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardInput = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.Environment.Clear()
    if ($WorkingDirectory) { $start.WorkingDirectory = $WorkingDirectory }
    if ([System.OperatingSystem]::IsWindows()) {
        $start.Environment['SystemRoot'] = [System.Environment]::GetFolderPath('Windows')
    }
    if ($WslDistribution) {
        $start.FileName = Join-Path ([System.Environment]::GetFolderPath('System')) 'wsl.exe'
        foreach ($argument in @('--distribution', $WslDistribution, '--exec', '/usr/bin/env', '-i',
                "LC_ALL=$Locale", "LANG=$Locale", 'PATH=/usr/bin:/bin', $ReferencePath)) {
            $start.ArgumentList.Add($argument)
        }
    } else {
        $start.FileName = $ReferencePath
        $start.Environment['LC_ALL'] = $Locale
        $start.Environment['LANG'] = $Locale
        $start.Environment['PATH'] = if ([System.OperatingSystem]::IsWindows()) {
            'C:\Program Files\Git\usr\bin;C:\Windows\System32'
        } else { '/usr/bin:/bin' }
    }
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    $process = [System.Diagnostics.Process]::Start($start)
    if ($null -eq $process) { throw 'GNU Bash did not start.' }
    $stdout = [System.IO.MemoryStream]::new()
    $stderr = [System.IO.MemoryStream]::new()
    try {
        $copyOut = $process.StandardOutput.BaseStream.CopyToAsync($stdout)
        $copyError = $process.StandardError.BaseStream.CopyToAsync($stderr)
        $process.StandardInput.BaseStream.Write($InputBytes)
        $process.StandardInput.Close()
        if (-not $process.WaitForExit(30000)) { throw 'GNU correctness case exceeded its deadline.' }
        $copyOut.GetAwaiter().GetResult()
        $copyError.GetAwaiter().GetResult()
        return [pscustomobject]@{
            exitCode = $process.ExitCode
            stdoutHex = [Convert]::ToHexString($stdout.ToArray()).ToLowerInvariant()
            stderrHex = [Convert]::ToHexString($stderr.ToArray()).ToLowerInvariant()
            text = [System.Text.Encoding]::UTF8.GetString($stdout.ToArray())
        }
    } finally {
        if (-not $process.HasExited) { $process.Kill($true); $process.WaitForExit() }
        $process.Dispose()
        $stdout.Dispose()
        $stderr.Dispose()
    }
}

$referenceId = if ($WslDistribution -or -not [System.OperatingSystem]::IsWindows()) { 'gnu-linux' } else { 'gnu-msys' }
$reference = @($catalog.references | Where-Object id -eq $referenceId)
if ($reference.Count -ne 1) { throw "Missing pinned reference: $referenceId" }
$version = Invoke-Reference -Arguments @('--noprofile', '--norc', '--version') -Locale 'C' -InputBytes @()
if ($version.exitCode -ne 0 -or ($version.text -split "`n")[0].TrimEnd("`r") -ne $reference[0].version) {
    throw 'GNU Bash version differs from the pinned reference.'
}
$referenceHash = if ($WslDistribution) {
    $hashOutput = & wsl --distribution $WslDistribution --exec sha256sum -- $ReferencePath
    if ($LASTEXITCODE -ne 0) { throw 'Cannot hash the Linux reference binary.' }
    ($hashOutput -split '\s+')[0]
} else { (Get-FileHash -LiteralPath $ReferencePath -Algorithm SHA256).Hash.ToLowerInvariant() }
if ($referenceHash -ne $reference[0].sha256) { throw 'GNU Bash hash differs from the pinned reference.' }

$results = foreach ($sample in $catalog.cases) {
    $scriptBytes = [System.Text.Encoding]::UTF8.GetBytes($sample.script)
    $scriptHash = [Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData($scriptBytes)).ToLowerInvariant()
    if ($scriptHash -ne $sample.scriptSha256) { throw "Script hash mismatch: $($sample.id)" }
    if ($sample.referenceIds -and $referenceId -notin $sample.referenceIds) {
        if (-not $sample.referenceReason) { throw 'Every excluded reference needs a reason.' }
        [pscustomobject]@{ id = $sample.id; classification = $sample.classification; passed = $null; skipped = $sample.referenceReason; actual = $null }
        continue
    }
    if ($sample.id -notmatch '^[a-z0-9][a-z0-9-]+$') { throw 'Unsafe correctness case identifier.' }
    $fixtureRoot = Join-Path $repository ("artifacts/conformance/fixtures/" + [Guid]::NewGuid().ToString('N'))
    [void][System.IO.Directory]::CreateDirectory($fixtureRoot)
    foreach ($file in $sample.files) {
        if ([System.IO.Path]::IsPathRooted($file.path) -or '..' -in ($file.path -split '/')) { throw 'Unsafe fixture path.' }
        $fixturePath = [System.IO.Path]::GetFullPath((Join-Path $fixtureRoot $file.path))
        if (-not $fixturePath.StartsWith($fixtureRoot + [System.IO.Path]::DirectorySeparatorChar, [StringComparison]::Ordinal)) {
            throw 'Fixture must stay within its isolated directory.'
        }
        $content = [Convert]::FromHexString($file.contentHex)
        $contentHash = [Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData($content)).ToLowerInvariant()
        if ($contentHash -ne $file.sha256) { throw 'Fixture hash mismatch.' }
        [void][System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($fixturePath))
        [System.IO.File]::WriteAllBytes($fixturePath, $content)
    }
    $arguments = @($catalog.options) + @('-c', $sample.script, 'lokad-conformance')
    $actual = Invoke-Reference -Arguments $arguments -Locale $sample.locale -InputBytes ([Convert]::FromHexString($sample.stdinHex)) -WorkingDirectory $fixtureRoot
    $comparedStderr = $actual.stderrHex
    if ($sample.stderrNormalization) {
        if ($sample.stderrNormalization -ne 'bash-command-location') { throw 'Unknown diagnostic normalization.' }
        # Latin-1 maps every byte reversibly, including malformed UTF-8 diagnostic operands.
        $raw = [System.Text.Encoding]::Latin1.GetString([Convert]::FromHexString($actual.stderrHex))
        $normalized = [regex]::Replace($raw, '(?m)^lokad-conformance: line [1-9][0-9]*: ', '')
        $comparedStderr = [Convert]::ToHexString([System.Text.Encoding]::Latin1.GetBytes($normalized)).ToLowerInvariant()
        $actual | Add-Member -NotePropertyName normalizedStderrHex -NotePropertyValue $comparedStderr
    }
    $passed = $actual.exitCode -eq $sample.exitCode -and $actual.stdoutHex -eq $sample.stdoutHex -and $comparedStderr -eq $sample.stderrHex
    foreach ($effect in $sample.fileEffects) {
        if ([System.IO.Path]::IsPathRooted($effect.path) -or '..' -in ($effect.path -split '/')) { throw 'Unsafe expected effect path.' }
        $effectPath = [System.IO.Path]::GetFullPath((Join-Path $fixtureRoot $effect.path))
        if (-not $effectPath.StartsWith($fixtureRoot + [System.IO.Path]::DirectorySeparatorChar, [StringComparison]::Ordinal)) {
            throw 'Expected effect must stay within its isolated directory.'
        }
        if (-not [System.IO.File]::Exists($effectPath)) { $passed = $false; continue }
        $actualHex = [Convert]::ToHexString([System.IO.File]::ReadAllBytes($effectPath)).ToLowerInvariant()
        $passed = $passed -and $actualHex -eq $effect.contentHex
    }
    [pscustomobject]@{ id = $sample.id; classification = $sample.classification; reason = $sample.reason; passed = $passed; actual = $actual }
}
$destination = if ([System.IO.Path]::IsPathRooted($OutputPath)) { $OutputPath } else { Join-Path $repository $OutputPath }
[void][System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($destination))
$export = [ordered]@{
    schemaVersion = 1
    referenceId = $referenceId
    referenceVersion = $reference[0].version
    referenceSha256 = $referenceHash
    catalogSha256 = (Get-FileHash -LiteralPath $catalogPath -Algorithm SHA256).Hash.ToLowerInvariant()
    results = @($results)
}
$export | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $destination -Encoding utf8NoBOM
$failed = @($results | Where-Object { $_.passed -eq $false })
$skipped = @($results | Where-Object skipped)
$verifiedCount = $results.Count - $skipped.Count
$deviations = @($results | Where-Object { $_.classification -eq 'deviation' -and $null -ne $_.passed })
Write-Output "$referenceId : $($verifiedCount - $failed.Count)/$verifiedCount native expectations verified; $($deviations.Count) declared managed deviations; $($skipped.Count) labelled reference exclusions. Results: $destination"
if ($failed.Count -ne 0) { throw "GNU correctness mismatches: $($failed.id -join ', ')" }
