<#
.SYNOPSIS
    Runs the AxoParse.Benchmarks (BenchmarkDotNet) suite into a named results folder.

.DESCRIPTION
    Wraps `dotnet run -c Release --project perf/AxoParse.Benchmarks` so baseline/candidate runs land in
    perf/results/<Name>/ (git-ignored) with identical settings, ready for tools/bench/bdn-compare.ps1.

.PARAMETER Name
    Results folder name under perf/results, e.g. 'baseline' or 'candidate'. The folder is replaced.

.PARAMETER Filter
    One or more BenchmarkDotNet glob filters. Default '*' (everything).
    Examples: '*FormatterBenchmarks*', '*ChunkBenchmarks.XmlWarm*', '*FileParseBenchmarks*security.evtx*'

.PARAMETER Job
    BenchmarkDotNet job: Dry, Short, Medium, Long or Default. Short is fine for exploration;
    use Medium (or Default) when the result decides whether a change is kept.

.PARAMETER Disasm
    Also export JIT disassembly (DisassemblyDiagnoser) for the benchmarked methods.

.EXAMPLE
    pwsh tools/bench/bdn-run.ps1 baseline -Filter '*ChunkBenchmarks*' -Job Medium
    # ...edit code...
    pwsh tools/bench/bdn-run.ps1 candidate -Filter '*ChunkBenchmarks*' -Job Medium
    pwsh tools/bench/bdn-compare.ps1 perf/results/baseline perf/results/candidate
#>
param(
    [Parameter(Mandatory, Position = 0)][string]$Name,
    [string[]]$Filter = @('*'),
    [ValidateSet('Dry', 'Short', 'Medium', 'Long', 'Default')][string]$Job = 'Short',
    [switch]$Disasm
)

$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..' '..')
$project = Join-Path $root 'perf' 'AxoParse.Benchmarks'
$out = Join-Path $root 'perf' 'results' $Name

if (Test-Path $out) { Remove-Item $out -Recurse -Force }
New-Item -ItemType Directory -Force $out | Out-Null

$bdnArgs = @('--filter') + $Filter + @('--job', $Job, '--artifacts', $out)
if ($Disasm) { $bdnArgs += '--disasm' }

dotnet run -c Release --project $project -- @bdnArgs
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$report = Get-ChildItem (Join-Path $out 'results') -Filter '*-report-github.md' | Select-Object -First 1
if ($report) { Write-Output "`nReport: $($report.FullName)" }
