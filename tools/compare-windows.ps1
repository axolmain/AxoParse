<#
.SYNOPSIS
    Compares AxoParse XML output against Windows-native wevtutil for correctness validation.

.DESCRIPTION
    Parses an .evtx file with both AxoParse and wevtutil, then compares each event by
    EventRecordID. Reports mismatches in core fields (EventID, TimeCreated, Provider,
    Computer, Channel) and EventData values.

    Run this on a Windows machine where wevtutil.exe is available.

.PARAMETER EvtxPath
    Path to the .evtx file to compare.

.PARAMETER AxoParseBin
    Path to the AxoParse AOT binary. Defaults to perf/AxoParse.Bench/bin/publish/AxoParse.Bench.exe

.EXAMPLE
    .\tools\compare-windows.ps1 -EvtxPath tests\data\security.evtx
#>
param(
    [Parameter(Mandatory = $true)]
    [string]$EvtxPath,

    [string]$AxoParseBin = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# ── Resolve paths ────────────────────────────────────────────────
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$rootDir = Split-Path -Parent $scriptDir

if (-not $AxoParseBin) {
    $AxoParseBin = Join-Path $rootDir "perf\AxoParse.Bench\bin\publish\AxoParse.Bench.exe"
}

$EvtxPath = Resolve-Path $EvtxPath
if (-not (Test-Path $EvtxPath)) {
    Write-Error "EVTX file not found: $EvtxPath"
    exit 1
}
if (-not (Test-Path $AxoParseBin)) {
    Write-Error "AxoParse binary not found: $AxoParseBin`nBuild with: dotnet publish perf/AxoParse.Bench -c Release -o perf/AxoParse.Bench/bin/publish"
    exit 1
}

$ns = @{ e = "http://schemas.microsoft.com/win/2004/08/events/event" }

# ── Helper: extract events from XML string ───────────────────────
function Parse-Events([string]$rawXml, [string]$source) {
    # Wrap concatenated events in a root element for valid XML
    $wrapped = "<Events>$rawXml</Events>"
    [xml]$doc = $wrapped

    $events = @{}
    foreach ($node in $doc.Events.ChildNodes) {
        if ($node.LocalName -ne "Event") { continue }

        $sys = $node.System
        $recordId = $sys.EventRecordID

        $eventData = @{}
        $dataNode = $node.EventData
        if (-not $dataNode) { $dataNode = $node.UserData }
        if ($dataNode) {
            foreach ($d in $dataNode.ChildNodes) {
                $name = $d.GetAttribute("Name")
                if (-not $name) { $name = $d.LocalName }
                $eventData[$name] = $d.InnerText.Trim()
            }
        }

        $events[$recordId] = @{
            EventID     = $sys.EventID.InnerText ?? $sys.EventID
            TimeCreated = $sys.TimeCreated.SystemTime
            Provider    = $sys.Provider.Name
            Computer    = $sys.Computer
            Channel     = $sys.Channel
            EventData   = $eventData
        }
    }

    Write-Host "  $source : $($events.Count) events"
    return $events
}

# ── Run wevtutil ─────────────────────────────────────────────────
Write-Host "`n[1/4] Running wevtutil..." -ForegroundColor Cyan
$wevtRaw = & wevtutil qe /lf:true /rd:false "$EvtxPath" 2>&1
if ($LASTEXITCODE -ne 0) {
    Write-Error "wevtutil failed: $wevtRaw"
    exit 1
}
$wevtXml = $wevtRaw -join "`n"

# ── Run AxoParse ─────────────────────────────────────────────────
Write-Host "[2/4] Running AxoParse..." -ForegroundColor Cyan
$axoRaw = & $AxoParseBin "$EvtxPath" -o xml -t 1 2>&1
if ($LASTEXITCODE -ne 0) {
    Write-Error "AxoParse failed: $axoRaw"
    exit 1
}
$axoXml = $axoRaw -join "`n"

# ── Parse both ───────────────────────────────────────────────────
Write-Host "[3/4] Parsing events..." -ForegroundColor Cyan
$wevtEvents = Parse-Events $wevtXml "wevtutil"
$axoEvents  = Parse-Events $axoXml  "AxoParse"

# ── Compare ──────────────────────────────────────────────────────
Write-Host "[4/4] Comparing..." -ForegroundColor Cyan

$missingInAxo = @()
$missingInWevt = @()
$fieldMismatches = @()
$dataMismatches = @()
$matched = 0

$coreFields = @("EventID", "TimeCreated", "Provider", "Computer", "Channel")

foreach ($id in $wevtEvents.Keys) {
    if (-not $axoEvents.ContainsKey($id)) {
        $missingInAxo += $id
        continue
    }

    $w = $wevtEvents[$id]
    $a = $axoEvents[$id]
    $recordOk = $true

    # Compare core fields
    foreach ($field in $coreFields) {
        $wVal = "$($w[$field])".Trim()
        $aVal = "$($a[$field])".Trim()
        if ($wVal -ne $aVal) {
            $fieldMismatches += [PSCustomObject]@{
                RecordID = $id
                Field    = $field
                Wevtutil = $wVal
                AxoParse = $aVal
            }
            $recordOk = $false
        }
    }

    # Compare EventData values
    $wData = $w.EventData
    $aData = $a.EventData
    $allKeys = @($wData.Keys) + @($aData.Keys) | Sort-Object -Unique

    foreach ($key in $allKeys) {
        $wVal = if ($wData.ContainsKey($key)) { "$($wData[$key])".Trim() } else { "<missing>" }
        $aVal = if ($aData.ContainsKey($key)) { "$($aData[$key])".Trim() } else { "<missing>" }
        if ($wVal -ne $aVal) {
            $dataMismatches += [PSCustomObject]@{
                RecordID  = $id
                DataField = $key
                Wevtutil  = $wVal
                AxoParse  = $aVal
            }
            $recordOk = $false
        }
    }

    if ($recordOk) { $matched++ }
}

foreach ($id in $axoEvents.Keys) {
    if (-not $wevtEvents.ContainsKey($id)) {
        $missingInWevt += $id
    }
}

# ── Report ───────────────────────────────────────────────────────
Write-Host "`n=========================================" -ForegroundColor White
Write-Host "  Comparison Results" -ForegroundColor White
Write-Host "=========================================" -ForegroundColor White

$total = $wevtEvents.Count
$axoTotal = $axoEvents.Count
Write-Host "  wevtutil events : $total"
Write-Host "  AxoParse events : $axoTotal"
Write-Host "  Matched         : $matched / $total"

if ($missingInAxo.Count -gt 0) {
    Write-Host "`n  Missing in AxoParse ($($missingInAxo.Count)):" -ForegroundColor Red
    $missingInAxo | Select-Object -First 20 | ForEach-Object { Write-Host "    RecordID $_" }
    if ($missingInAxo.Count -gt 20) { Write-Host "    ... and $($missingInAxo.Count - 20) more" }
}

if ($missingInWevt.Count -gt 0) {
    Write-Host "`n  Extra in AxoParse ($($missingInWevt.Count)):" -ForegroundColor Yellow
    $missingInWevt | Select-Object -First 20 | ForEach-Object { Write-Host "    RecordID $_" }
    if ($missingInWevt.Count -gt 20) { Write-Host "    ... and $($missingInWevt.Count - 20) more" }
}

if ($fieldMismatches.Count -gt 0) {
    Write-Host "`n  Core field mismatches ($($fieldMismatches.Count)):" -ForegroundColor Red
    $fieldMismatches | Select-Object -First 30 | Format-Table -AutoSize | Out-String | Write-Host
}

if ($dataMismatches.Count -gt 0) {
    Write-Host "`n  EventData mismatches ($($dataMismatches.Count)):" -ForegroundColor Yellow
    $dataMismatches | Select-Object -First 30 | Format-Table -AutoSize | Out-String | Write-Host
}

if ($missingInAxo.Count -eq 0 -and $missingInWevt.Count -eq 0 -and $fieldMismatches.Count -eq 0 -and $dataMismatches.Count -eq 0) {
    Write-Host "`n  ALL EVENTS MATCH" -ForegroundColor Green
}

Write-Host "=========================================`n"
