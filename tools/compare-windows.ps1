<#
.SYNOPSIS
    Compares AxoParse XML output against Windows-native wevtutil for correctness validation.

.DESCRIPTION
    Parses .evtx files with both AxoParse and wevtutil, then compares each event by
    EventRecordID. Reports mismatches in core fields (EventID, TimeCreated, Provider,
    Computer, Channel) and EventData values.

    When wevtutil fails on a file (e.g. corrupt VSS copies), the script still runs AxoParse
    to check if it can handle the file, and reports both outcomes.

    When no EvtxPath is given, iterates all *.evtx files under tests/data and prints
    a combined summary of all failures at the end.

    Run this on a Windows machine where wevtutil.exe is available.

.PARAMETER EvtxPath
    Path to a single .evtx file to compare. If omitted, all files in tests/data are compared.

.PARAMETER AxoParseBin
    Path to the AxoParse AOT binary. Defaults to perf/AxoParse.Bench/bin/publish/AxoParse.Bench.exe

.EXAMPLE
    .\tools\compare-windows.ps1 -EvtxPath tests\data\security.evtx

.EXAMPLE
    .\tools\compare-windows.ps1
#>
param(
    [string]$EvtxPath = "",

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

if (-not (Test-Path $AxoParseBin)) {
    Write-Error "AxoParse binary not found: $AxoParseBin`nBuild with: dotnet publish perf/AxoParse.Bench -c Release -o perf/AxoParse.Bench/bin/publish"
    exit 1
}

# ── Helper: normalize a value for comparison ─────────────────────
$guidPattern = '^\{?[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\}?$'
$tsPattern = '^(\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d+?)0*Z$'

function Normalize-Value([string]$val) {
    if ($val -match $tsPattern) {
        return $Matches[1] + "Z"
    }
    return $val
}

function Normalize-Guid([string]$val) {
    if ($val -match $guidPattern) {
        return $val.Trim('{}').ToLowerInvariant()
    }
    return $val
}

# ── Helper: extract events from XML string ───────────────────────
function Parse-Events([string]$rawXml, [string]$source) {
    $wrapped = "<Events>$rawXml</Events>"
    [xml]$doc = $wrapped

    $events = @{}
    foreach ($node in $doc.Events.ChildNodes) {
        if ($node.LocalName -ne "Event") { continue }

        $sys = $node.System
        $recordId = $sys.EventRecordID

        $eventData = @{}
        $dataNode = $node["EventData"]
        if (-not $dataNode) { $dataNode = $node["UserData"] }
        if ($dataNode) {
            foreach ($d in $dataNode.ChildNodes) {
                if ($d.NodeType -ne [System.Xml.XmlNodeType]::Element) { continue }
                $name = $d.GetAttribute("Name")
                if (-not $name) { $name = $d.LocalName }
                $eventData[$name] = $d.InnerText.Trim()
            }
        }

        $eidNode = $sys["EventID"]
        $eid = if ($eidNode -is [System.Xml.XmlElement]) { $eidNode.InnerText } else { "$eidNode" }

        $tcNode = $sys["TimeCreated"]
        $tc = if ($tcNode) { $tcNode.GetAttribute("SystemTime") } else { "" }

        $provNode = $sys["Provider"]
        $prov = if ($provNode) { $provNode.GetAttribute("Name") } else { "" }

        $events[$recordId] = @{
            EventID     = $eid
            TimeCreated = $tc
            Provider    = $prov
            Computer    = $sys.Computer
            Channel     = $sys.Channel
            EventData   = $eventData
        }
    }

    Write-Host "  $source : $($events.Count) events"
    return ,$events
}

# ── Run an external command, suppressing stderr ──────────────────
function Run-Tool([string]$exe, [string[]]$arguments) {
    $prevEAP = $ErrorActionPreference
    $ErrorActionPreference = "SilentlyContinue"
    $output = & $exe @arguments 2>$null
    $code = $LASTEXITCODE
    $ErrorActionPreference = $prevEAP
    return @{ Output = $output; ExitCode = $code }
}

# ── Compare a single EVTX file ──────────────────────────────────
function Compare-EvtxFile([string]$filePath) {
    $fileName = Split-Path -Leaf $filePath
    Write-Host "  $fileName" -ForegroundColor White

    # Run wevtutil
    Write-Host "    [1/4] Running wevtutil..." -ForegroundColor DarkGray
    $wevtResult = Run-Tool "wevtutil" @("qe", "/lf:true", "/rd:false", $filePath)
    $wevtOk = ($wevtResult.ExitCode -eq 0) -and ($wevtResult.Output)
    if (-not $wevtOk) {
        Write-Host "    wevtutil failed (code $($wevtResult.ExitCode))" -ForegroundColor Red
    }

    # Run AxoParse
    Write-Host "    [2/4] Running AxoParse..." -ForegroundColor DarkGray
    $axoResult = Run-Tool $AxoParseBin @($filePath, "-o", "xml", "-t", "1")
    $axoOk = ($axoResult.ExitCode -eq 0) -and ($axoResult.Output)
    if (-not $axoOk) {
        Write-Host "    AxoParse failed (code $($axoResult.ExitCode))" -ForegroundColor Red
    }

    # If both failed, nothing to compare
    if ((-not $wevtOk) -and (-not $axoOk)) {
        Write-Host "    Both failed - skipping" -ForegroundColor Red
        return ,@{
            File = $fileName; Skipped = "both failed"; OK = $false
            Total = 0; AxoTotal = 0; Matched = 0
            MissingInAxo = @(); MissingInWevt = @()
            FieldMismatches = @(); DataMismatches = @()
        }
    }

    # If only wevtutil failed, report AxoParse event count but can't compare
    if (-not $wevtOk) {
        $axoCount = 0
        $axoXml = $axoResult.Output -join "`n"
        try {
            $axoEvents = Parse-Events $axoXml "AxoParse"
            $axoCount = $axoEvents.Count
        } catch {
            Write-Host "    AxoParse XML also failed to parse" -ForegroundColor Yellow
        }
        Write-Host "    wevtutil failed, AxoParse produced $axoCount events - cannot compare" -ForegroundColor Yellow
        return ,@{
            File = $fileName; Skipped = "wevtutil failed (code $($wevtResult.ExitCode)), AxoParse got $axoCount events"; OK = $false
            Total = 0; AxoTotal = $axoCount; Matched = 0
            MissingInAxo = @(); MissingInWevt = @()
            FieldMismatches = @(); DataMismatches = @()
        }
    }

    # If only AxoParse failed
    if (-not $axoOk) {
        Write-Host "    AxoParse failed but wevtutil succeeded - skipping" -ForegroundColor Red
        return ,@{
            File = $fileName; Skipped = "AxoParse failed (code $($axoResult.ExitCode))"; OK = $false
            Total = 0; AxoTotal = 0; Matched = 0
            MissingInAxo = @(); MissingInWevt = @()
            FieldMismatches = @(); DataMismatches = @()
        }
    }

    # Parse both
    Write-Host "    [3/4] Parsing events..." -ForegroundColor DarkGray
    $wevtXml = $wevtResult.Output -join "`n"
    $axoXml = $axoResult.Output -join "`n"

    $wevtEvents = $null
    $axoEvents = $null
    try {
        $wevtEvents = Parse-Events $wevtXml "wevtutil"
    } catch {
        Write-Host "    Failed to parse wevtutil XML" -ForegroundColor Red
    }
    try {
        $axoEvents = Parse-Events $axoXml "AxoParse"
    } catch {
        Write-Host "    Failed to parse AxoParse XML" -ForegroundColor Red
    }

    if ((-not $wevtEvents) -or (-not $axoEvents)) {
        $skipMsg = if (-not $wevtEvents) { "wevtutil XML parse error" } else { "AxoParse XML parse error" }
        return ,@{
            File = $fileName; Skipped = $skipMsg; OK = $false
            Total = 0; AxoTotal = 0; Matched = 0
            MissingInAxo = @(); MissingInWevt = @()
            FieldMismatches = @(); DataMismatches = @()
        }
    }

    # Compare
    Write-Host "    [4/4] Comparing..." -ForegroundColor DarkGray

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

        foreach ($field in $coreFields) {
            $wVal = Normalize-Value "$($w[$field])".Trim()
            $aVal = Normalize-Value "$($a[$field])".Trim()
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

        $wData = $w.EventData
        $aData = $a.EventData
        $allKeys = @($wData.Keys) + @($aData.Keys) | Sort-Object -Unique

        foreach ($key in $allKeys) {
            $wVal = if ($wData.ContainsKey($key)) { "$($wData[$key])".Trim() } else { "<missing>" }
            $aVal = if ($aData.ContainsKey($key)) { "$($aData[$key])".Trim() } else { "<missing>" }
            $wVal = Normalize-Value (Normalize-Guid $wVal)
            $aVal = Normalize-Value (Normalize-Guid $aVal)
            if ($wVal -eq "???") { continue }
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

    $total = $wevtEvents.Count
    $ok = ($missingInAxo.Count -eq 0) -and ($missingInWevt.Count -eq 0) -and ($fieldMismatches.Count -eq 0) -and ($dataMismatches.Count -eq 0)

    if ($ok) {
        Write-Host "    OK ($matched / $total)" -ForegroundColor Green
    } else {
        Write-Host "    ISSUES ($matched / $total matched)" -ForegroundColor Yellow
    }

    return ,@{
        File             = $fileName
        Skipped          = ""
        Total            = $total
        AxoTotal         = $axoEvents.Count
        Matched          = $matched
        MissingInAxo     = $missingInAxo
        MissingInWevt    = $missingInWevt
        FieldMismatches  = $fieldMismatches
        DataMismatches   = $dataMismatches
        OK               = $ok
    }
}

# ── Main ─────────────────────────────────────────────────────────
if ($EvtxPath) {
    $EvtxPath = Resolve-Path $EvtxPath
    if (-not (Test-Path $EvtxPath)) {
        Write-Error "EVTX file not found: $EvtxPath"
        exit 1
    }
    $r = Compare-EvtxFile $EvtxPath
    if ($r.Skipped) { exit 1 }
    if (-not $r.OK) { exit 1 }
    exit 0
}

# Batch mode
$testDataDir = Join-Path $rootDir "tests\data"
$evtxFiles = Get-ChildItem -Path $testDataDir -Filter "*.evtx" | Sort-Object Name
if ($evtxFiles.Count -eq 0) {
    Write-Error "No .evtx files found in $testDataDir"
    exit 1
}

Write-Host "`nComparing $($evtxFiles.Count) .evtx files in tests\data...`n" -ForegroundColor Cyan

$results = [System.Collections.Generic.List[hashtable]]::new()
for ($i = 0; $i -lt $evtxFiles.Count; $i++) {
    $file = $evtxFiles[$i]
    Write-Host "[$($i + 1)/$($evtxFiles.Count)]" -ForegroundColor Cyan -NoNewline
    $r = Compare-EvtxFile $file.FullName
    $results.Add($r)
}

# ── Final summary ────────────────────────────────────────────────
$passed = [System.Collections.Generic.List[hashtable]]::new()
$failed = [System.Collections.Generic.List[hashtable]]::new()
foreach ($r in $results) {
    if ($r.OK -and (-not $r.Skipped)) { $passed.Add($r) } else { $failed.Add($r) }
}

Write-Host "`n`n=========================================" -ForegroundColor White
$skippedCount = @($failed | Where-Object { $_.Skipped }).Count
$mismatchCount = $failed.Count - $skippedCount
Write-Host "  $($passed.Count) passed, $mismatchCount mismatched, $skippedCount skipped (out of $($results.Count) files)" -ForegroundColor White
Write-Host "=========================================" -ForegroundColor White

if ($failed.Count -eq 0) {
    Write-Host "`n  ALL FILES MATCH`n" -ForegroundColor Green
    exit 0
}

$skipped = [System.Collections.Generic.List[hashtable]]::new()
$mismatched = [System.Collections.Generic.List[hashtable]]::new()
foreach ($r in $failed) {
    if ($r.Skipped) { $skipped.Add($r) } else { $mismatched.Add($r) }
}

if ($skipped.Count -gt 0) {
    Write-Host "`n  Skipped ($($skipped.Count)):" -ForegroundColor DarkGray
    foreach ($r in $skipped) {
        Write-Host "    $($r.File): $($r.Skipped)" -ForegroundColor DarkGray
    }
}

foreach ($r in $mismatched) {
    Write-Host "`n  $($r.File)" -ForegroundColor Red
    Write-Host "    $($r.Matched) / $($r.Total) matched"

    if ($r.MissingInAxo.Count -gt 0) {
        Write-Host "    Missing in AxoParse ($($r.MissingInAxo.Count)):" -ForegroundColor Red
        $r.MissingInAxo | Select-Object -First 10 | ForEach-Object { Write-Host "      RecordID $_" }
        if ($r.MissingInAxo.Count -gt 10) { Write-Host "      ... and $($r.MissingInAxo.Count - 10) more" }
    }

    if ($r.MissingInWevt.Count -gt 0) {
        Write-Host "    Extra in AxoParse ($($r.MissingInWevt.Count)):" -ForegroundColor Yellow
        $r.MissingInWevt | Select-Object -First 10 | ForEach-Object { Write-Host "      RecordID $_" }
        if ($r.MissingInWevt.Count -gt 10) { Write-Host "      ... and $($r.MissingInWevt.Count - 10) more" }
    }

    if ($r.FieldMismatches.Count -gt 0) {
        Write-Host "    Core field mismatches ($($r.FieldMismatches.Count)):" -ForegroundColor Red
        $r.FieldMismatches | Select-Object -First 20 | Format-Table -AutoSize -Wrap | Out-String | Write-Host
    }

    if ($r.DataMismatches.Count -gt 0) {
        Write-Host "    EventData mismatches ($($r.DataMismatches.Count)):" -ForegroundColor Yellow
        $r.DataMismatches | Select-Object -First 20 | Format-Table -AutoSize -Wrap | Out-String | Write-Host
    }
}

Write-Host "`n=========================================`n"
exit 1
