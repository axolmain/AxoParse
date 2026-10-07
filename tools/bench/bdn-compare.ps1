<#
.SYNOPSIS
    Compares two BenchmarkDotNet full-JSON reports (baseline vs candidate) and prints a verdict per benchmark.

.DESCRIPTION
    Matches benchmarks by FullName (method + parameters). For each pair reports mean time delta,
    allocated bytes/op delta and Gen0 delta, then classifies:
      FASTER / SLOWER  - mean moved by more than the noise threshold AND the 99.9% confidence intervals do not overlap
      LESS-ALLOC / MORE-ALLOC - allocated bytes/op changed (allocations are deterministic, so any change counts)
      NOISE            - otherwise
    Exit code 0 when nothing regressed, 1 when any benchmark is SLOWER or MORE-ALLOC (useful as a gate).

.PARAMETER Baseline
    Baseline report: a *-report-full.json file, or a BDN artifacts directory (its results/ folder is searched).

.PARAMETER Candidate
    Candidate report: same forms as -Baseline.

.PARAMETER NoiseThreshold
    Minimum relative mean change (fraction) to call a time difference real. Default 0.03 (3%).

.EXAMPLE
    pwsh tools/bench/bdn-compare.ps1 perf/results/baseline perf/results/candidate
#>
param(
    [Parameter(Mandatory)][string]$Baseline,
    [Parameter(Mandatory)][string]$Candidate,
    [double]$NoiseThreshold = 0.03
)

$ErrorActionPreference = 'Stop'

function Resolve-Report([string]$path) {
    if (Test-Path $path -PathType Leaf) { return (Resolve-Path $path).Path }
    $dir = if (Test-Path (Join-Path $path 'results')) { Join-Path $path 'results' } else { $path }
    $file = Get-ChildItem $dir -Filter '*-report-full.json' | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $file) { throw "No *-report-full.json found under $path" }
    return $file.FullName
}

function Read-Benchmarks([string]$path) {
    $json = Get-Content (Resolve-Report $path) -Raw | ConvertFrom-Json
    $map = [ordered]@{}
    foreach ($b in $json.Benchmarks) {
        if (-not $b.Statistics) { continue }   # failed benchmark
        $ci = $b.Statistics.ConfidenceInterval
        $map[$b.FullName] = [pscustomobject]@{
            Mean  = [double]$b.Statistics.Mean
            Lower = [double]$ci.Lower
            Upper = [double]$ci.Upper
            Alloc = if ($b.Memory) { [double]$b.Memory.BytesAllocatedPerOperation } else { [double]::NaN }
            Gen0  = if ($b.Memory) { [double]$b.Memory.Gen0Collections / [math]::Max(1, [double]$b.Memory.TotalOperations) * 1000 } else { [double]::NaN }
        }
    }
    return $map
}

function Format-Time([double]$ns) {
    if ($ns -ge 1e9) { return '{0:N2} s' -f ($ns / 1e9) }
    if ($ns -ge 1e6) { return '{0:N2} ms' -f ($ns / 1e6) }
    if ($ns -ge 1e3) { return '{0:N2} us' -f ($ns / 1e3) }
    return '{0:N2} ns' -f $ns
}

function Format-Bytes([double]$b) {
    if ([double]::IsNaN($b)) { return '-' }
    if ($b -ge 1MB) { return '{0:N2} MB' -f ($b / 1MB) }
    if ($b -ge 1KB) { return '{0:N2} KB' -f ($b / 1KB) }
    return '{0:N0} B' -f $b
}

$base = Read-Benchmarks $Baseline
$cand = Read-Benchmarks $Candidate

$rows = foreach ($name in $cand.Keys) {
    if (-not $base.Contains($name)) { continue }
    $b = $base[$name]; $c = $cand[$name]
    $timeRatio = $c.Mean / $b.Mean
    $ciOverlap = -not (($c.Upper -lt $b.Lower) -or ($c.Lower -gt $b.Upper))
    $timeVerdict =
        if (-not $ciOverlap -and $timeRatio -le (1 - $NoiseThreshold)) { 'FASTER' }
        elseif (-not $ciOverlap -and $timeRatio -ge (1 + $NoiseThreshold)) { 'SLOWER' }
        else { 'noise' }
    $allocVerdict =
        if ([double]::IsNaN($b.Alloc) -or [double]::IsNaN($c.Alloc)) { '' }
        elseif ($c.Alloc -lt $b.Alloc) { 'LESS-ALLOC' }
        elseif ($c.Alloc -gt $b.Alloc) { 'MORE-ALLOC' }
        else { '' }

    [pscustomobject]@{
        Benchmark = $name -replace '^AxoParse\.Benchmarks\.', ''
        Base      = Format-Time $b.Mean
        New       = Format-Time $c.Mean
        Time      = '{0:+0.0;-0.0}%' -f (($timeRatio - 1) * 100)
        BaseAlloc = Format-Bytes $b.Alloc
        NewAlloc  = Format-Bytes $c.Alloc
        Verdict   = (@($timeVerdict, $allocVerdict) | Where-Object { $_ -and $_ -ne 'noise' }) -join ' '
    }
}

if (-not $rows) { Write-Error 'No benchmarks in common between the two reports.'; exit 2 }

foreach ($r in $rows) { if (-not $r.Verdict) { $r.Verdict = 'noise' } }
$rows | Format-Table -AutoSize | Out-String -Width 400 | Write-Output

$regressed = @($rows | Where-Object { $_.Verdict -match 'SLOWER|MORE-ALLOC' })
$improved = @($rows | Where-Object { $_.Verdict -match 'FASTER|LESS-ALLOC' })
Write-Output ("Summary: {0} improved, {1} regressed, {2} noise (threshold {3:P0}, CI must not overlap)" -f `
    $improved.Count, $regressed.Count, ($rows.Count - $improved.Count - $regressed.Count), $NoiseThreshold)

if ($regressed.Count -gt 0) { exit 1 }
exit 0
