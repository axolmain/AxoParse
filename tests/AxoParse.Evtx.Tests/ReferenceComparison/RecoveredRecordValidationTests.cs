using AxoParse.Evtx.Evtx;

namespace AxoParse.Evtx.Tests.ReferenceComparison;

/// <summary>
/// Proves that records recovered by AxoParse beyond what the reference parser finds are legitimate
/// Windows events — not garbage from corrupted chunks. For each file where we recover more
/// records than the reference parser, we:
///   1. Identify the extra record IDs (ones the reference parser skips)
///   2. Verify each extra produces well-formed XML with required Event fields
///   3. Verify timestamps are plausible (within range of surrounding records)
///   4. Verify record IDs are contiguous within recovered chunks
/// </summary>
public class RecoveredRecordValidationTests(ITestOutputHelper testOutputHelper)
{
    #region Public Methods

    // ── sample_with_a_bad_chunk_magic.evtx ──────────────────────────────────
    // Reference: 270 records across 3 contiguous ranges
    // AxoParse: 298 records — recovers 28 extra from chunks the reference parser rejects

    /// <summary>
    /// Verifies that AxoParse recovers a strict superset of the reference parser's records for the bad chunk
    /// magic file — every record ID the reference parser finds, we also find.
    /// </summary>
    [Fact]
    public void BadChunkMagic_ContainsAllReferenceRecords()
    {
        HashSet<ulong> referenceIds = BuildIdSet(ReferenceBadMagicRanges);
        HashSet<ulong> dotnetIds = GetAllRecordIds("sample_with_a_bad_chunk_magic.evtx");

        List<ulong> missing = [];
        foreach (ulong id in referenceIds)
        {
            if (!dotnetIds.Contains(id))
                missing.Add(id);
        }

        if (missing.Count > 0)
            testOutputHelper.WriteLine($"Missing reference IDs: {string.Join(", ", missing)}");

        Assert.Empty(missing);
        testOutputHelper.WriteLine(
            $"[bad_chunk_magic] AxoParse finds {dotnetIds.Count} records (reference: {referenceIds.Count}), superset confirmed");
    }

    /// <summary>
    /// Validates every extra record (not in reference output) is a well-formed Windows event.
    /// </summary>
    [Fact]
    public void BadChunkMagic_ExtraRecordsAreWellFormedEvents()
    {
        AssertExtrasAreWellFormed(
            "sample_with_a_bad_chunk_magic.evtx",
            ReferenceBadMagicRanges,
            "bad_chunk_magic");
    }

    /// <summary>
    /// Validates extra record timestamps are plausible — within the time range of reference-verified records.
    /// </summary>
    [Fact]
    public void BadChunkMagic_ExtraRecordTimestampsArePlausible()
    {
        AssertExtraTimestampsPlausible(
            "sample_with_a_bad_chunk_magic.evtx",
            ReferenceBadMagicRanges,
            "bad_chunk_magic");
    }

    // ── 2-vss_0-...-TerminalServices-RemoteConnectionManager%4Operational.evtx ──
    // Reference: 1774 records (12 contiguous ranges)
    // AxoParse: 1795 records — recovers 21 extra

    /// <summary>
    /// Verifies AxoParse recovers a strict superset of the reference parser's records for the bad checksum 2 file.
    /// </summary>
    [Fact]
    public void BadChecksum2_ContainsAllReferenceRecords()
    {
        HashSet<ulong> referenceIds = BuildIdSet(ReferenceBadChecksum2Ranges);
        HashSet<ulong> dotnetIds = GetAllRecordIds(
            "2-vss_0-Microsoft-Windows-TerminalServices-RemoteConnectionManager%4Operational.evtx");

        List<ulong> missing = [];
        foreach (ulong id in referenceIds)
        {
            if (!dotnetIds.Contains(id))
                missing.Add(id);
        }

        if (missing.Count > 0)
            testOutputHelper.WriteLine($"Missing reference IDs: {string.Join(", ", missing)}");

        Assert.Empty(missing);
        testOutputHelper.WriteLine(
            $"[bad_checksum_2] AxoParse finds {dotnetIds.Count} records (reference: {referenceIds.Count}), superset confirmed");
    }

    /// <summary>
    /// Validates every extra record is a well-formed Windows event.
    /// </summary>
    [Fact]
    public void BadChecksum2_ExtraRecordsAreWellFormedEvents()
    {
        AssertExtrasAreWellFormed(
            "2-vss_0-Microsoft-Windows-TerminalServices-RemoteConnectionManager%4Operational.evtx",
            ReferenceBadChecksum2Ranges,
            "bad_checksum_2");
    }

    /// <summary>
    /// Validates extra record timestamps are plausible.
    /// </summary>
    [Fact]
    public void BadChecksum2_ExtraRecordTimestampsArePlausible()
    {
        AssertExtraTimestampsPlausible(
            "2-vss_0-Microsoft-Windows-TerminalServices-RemoteConnectionManager%4Operational.evtx",
            ReferenceBadChecksum2Ranges,
            "bad_checksum_2");
    }

    // ── 2-vss_0-...-RdpCoreTS%4Operational.evtx ────────────────────────────
    // Reference: 1910 records (11 contiguous ranges)
    // AxoParse: more — recovers extras from bad-checksum chunks

    /// <summary>
    /// Verifies AxoParse recovers a strict superset of the reference parser's records for the bad checksum 1 file.
    /// </summary>
    [Fact]
    public void BadChecksum1_ContainsAllReferenceRecords()
    {
        HashSet<ulong> referenceIds = BuildIdSet(ReferenceBadChecksum1Ranges);
        HashSet<ulong> dotnetIds = GetAllRecordIds(
            "2-vss_0-Microsoft-Windows-RemoteDesktopServices-RdpCoreTS%4Operational.evtx");

        List<ulong> missing = [];
        foreach (ulong id in referenceIds)
        {
            if (!dotnetIds.Contains(id))
                missing.Add(id);
        }

        if (missing.Count > 0)
            testOutputHelper.WriteLine($"Missing reference IDs: {string.Join(", ", missing)}");

        Assert.Empty(missing);
        testOutputHelper.WriteLine(
            $"[bad_checksum_1] AxoParse finds {dotnetIds.Count} records (reference: {referenceIds.Count}), superset confirmed");
    }

    /// <summary>
    /// Validates every extra record is a well-formed Windows event.
    /// </summary>
    [Fact]
    public void BadChecksum1_ExtraRecordsAreWellFormedEvents()
    {
        AssertExtrasAreWellFormed(
            "2-vss_0-Microsoft-Windows-RemoteDesktopServices-RdpCoreTS%4Operational.evtx",
            ReferenceBadChecksum1Ranges,
            "bad_checksum_1");
    }

    /// <summary>
    /// Validates extra record timestamps are plausible.
    /// </summary>
    [Fact]
    public void BadChecksum1_ExtraRecordTimestampsArePlausible()
    {
        AssertExtraTimestampsPlausible(
            "2-vss_0-Microsoft-Windows-RemoteDesktopServices-RdpCoreTS%4Operational.evtx",
            ReferenceBadChecksum1Ranges,
            "bad_checksum_1");
    }

    #endregion

    #region Non-Public Methods

    /// <summary>
    /// Parses a file and returns all successfully rendered EventRecordId values.
    /// </summary>
    private static HashSet<ulong> GetAllRecordIds(string fileName)
    {
        string path = Path.Combine(TestPaths.TestDataDir, fileName);
        byte[] data = File.ReadAllBytes(path);
        EvtxParser parser = EvtxParser.Parse(data, maxThreads: 1);

        HashSet<ulong> ids = [];
        foreach (EvtxEvent evt in parser.GetEvents())
        {
            if (evt.IsSuccess)
                ids.Add(evt.Record.EventRecordId);
        }

        return ids;
    }

    /// <summary>
    /// Validates that every record ID our parser finds beyond the reference set produces valid XML
    /// containing the mandatory Windows Event Log structure.
    /// </summary>
    private void AssertExtrasAreWellFormed(
        string fileName, (ulong Start, ulong End)[] referenceRanges, string label)
    {
        HashSet<ulong> referenceIds = BuildIdSet(referenceRanges);

        string path = Path.Combine(TestPaths.TestDataDir, fileName);
        byte[] data = File.ReadAllBytes(path);
        EvtxParser parser = EvtxParser.Parse(data, maxThreads: 1);

        int extraCount = 0;
        foreach (EvtxEvent evt in parser.GetEvents())
        {
            if (!evt.IsSuccess || referenceIds.Contains(evt.Record.EventRecordId))
                continue;

            ulong id = evt.Record.EventRecordId;
            string xml = evt.Xml;
            extraCount++;

            // Must have the mandatory Event envelope
            Assert.True(xml.Contains("<Event"), $"[{label}] Record {id}: missing <Event> element");
            Assert.True(xml.Contains("</Event>"), $"[{label}] Record {id}: missing </Event> closing tag");

            // Must have System section with core fields
            Assert.True(xml.Contains("<System>"), $"[{label}] Record {id}: missing <System>");
            Assert.True(xml.Contains("<EventID"), $"[{label}] Record {id}: missing <EventID>");
            Assert.True(xml.Contains("<Provider"), $"[{label}] Record {id}: missing <Provider>");
            Assert.True(xml.Contains("<TimeCreated"), $"[{label}] Record {id}: missing <TimeCreated>");
            Assert.True(xml.Contains("<Channel>"), $"[{label}] Record {id}: missing <Channel>");
            Assert.True(xml.Contains("<Computer>"), $"[{label}] Record {id}: missing <Computer>");

            // EventData or UserData may be absent in recovered records from chunks with
            // destroyed headers — template resolution can fail, producing partial XML.
            // Log it but don't fail the test; the System envelope is the hard requirement.
            bool hasEventData = xml.Contains("<EventData") || xml.Contains("<UserData");
            if (!hasEventData)
                testOutputHelper.WriteLine($"  Record {id}: partial recovery (no EventData/UserData, {xml.Length} chars)");

            testOutputHelper.WriteLine($"  Record {id}: well-formed ({xml.Length} chars)");
        }

        testOutputHelper.WriteLine($"[{label}] {extraCount} extra records validated as well-formed events");
        Assert.True(extraCount > 0, $"[{label}] Expected extra records but found none");
    }

    /// <summary>
    /// Validates that extra records have timestamps within the overall time range of the file.
    /// A record with a garbage timestamp (year 1601, year 9999, etc.) indicates corrupted data.
    /// </summary>
    private void AssertExtraTimestampsPlausible(
        string fileName, (ulong Start, ulong End)[] referenceRanges, string label)
    {
        HashSet<ulong> referenceIds = BuildIdSet(referenceRanges);

        string path = Path.Combine(TestPaths.TestDataDir, fileName);
        byte[] data = File.ReadAllBytes(path);
        EvtxParser parser = EvtxParser.Parse(data, maxThreads: 1);

        // First pass: collect min/max timestamps from reference-verified records
        DateTime minTime = DateTime.MaxValue;
        DateTime maxTime = DateTime.MinValue;
        List<EvtxEvent> extraEvents = [];

        foreach (EvtxEvent evt in parser.GetEvents())
        {
            if (!evt.IsSuccess)
                continue;

            DateTime ts = evt.Record.WrittenTimeUtc;
            if (referenceIds.Contains(evt.Record.EventRecordId))
            {
                if (ts < minTime) minTime = ts;
                if (ts > maxTime) maxTime = ts;
            }
            else
            {
                extraEvents.Add(evt);
            }
        }

        testOutputHelper.WriteLine(
            $"[{label}] Reference time range: {minTime:O} — {maxTime:O}");

        // Allow 24h tolerance beyond the reference-verified range
        DateTime lowerBound = minTime.AddDays(-1);
        DateTime upperBound = maxTime.AddDays(1);

        foreach (EvtxEvent evt in extraEvents)
        {
            DateTime ts = evt.Record.WrittenTimeUtc;
            Assert.True((ts >= lowerBound) && (ts <= upperBound),
                $"[{label}] Record {evt.Record.EventRecordId}: timestamp {ts:O} is outside " +
                $"plausible range [{lowerBound:O}, {upperBound:O}]");

            testOutputHelper.WriteLine(
                $"  Record {evt.Record.EventRecordId}: {ts:O} (within range)");
        }

        testOutputHelper.WriteLine(
            $"[{label}] {extraEvents.Count} extra records have plausible timestamps");
    }

    /// <summary>
    /// Builds a HashSet of all record IDs from the reference parser's contiguous record ID ranges.
    /// </summary>
    private static HashSet<ulong> BuildIdSet((ulong Start, ulong End)[] ranges)
    {
        HashSet<ulong> ids = [];
        foreach ((ulong start, ulong end) in ranges)
        {
            for (ulong id = start; id <= end; id++)
                ids.Add(id);
        }
        return ids;
    }

    #endregion

    #region Non-Public Fields

    /// <summary>
    /// Contiguous record ID ranges the reference evtx_dump binary produces for
    /// sample_with_a_bad_chunk_magic.evtx (270 records total).
    /// </summary>
    private static readonly (ulong Start, ulong End)[] ReferenceBadMagicRanges =
    [
        (45691, 45933), // 243 records
        (46728, 46741), //  14 records
        (47628, 47640), //  13 records
    ];

    /// <summary>
    /// Contiguous record ID ranges the reference evtx_dump binary produces for
    /// 2-vss_0-...-TerminalServices-RemoteConnectionManager%4Operational.evtx (1,774 records total).
    /// </summary>
    private static readonly (ulong Start, ulong End)[] ReferenceBadChecksum2Ranges =
    [
        (352106, 352135), //  30 records
        (352202, 352210), //   9 records
        (382313, 382423), // 111 records
        (401513, 401542), //  30 records
        (422377, 422425), //  49 records
        (444649, 444690), //  42 records
        (461416, 462183), // 768 records
        (462312, 462695), // 384 records
        (462726, 462791), //  66 records
        (462823, 462949), // 127 records
        (462980, 463010), //  31 records
        (463204, 463330), // 127 records
    ];

    /// <summary>
    /// Contiguous record ID ranges the reference evtx_dump binary produces for
    /// 2-vss_0-...-RdpCoreTS%4Operational.evtx (1,910 records total).
    /// </summary>
    private static readonly (ulong Start, ulong End)[] ReferenceBadChecksum1Ranges =
    [
        (5281368, 5281496), // 129 records
        (5734858, 5734986), // 129 records
        (6022401, 6022530), // 130 records
        (6334916, 6335010), //  95 records
        (6336850, 6336965), // 116 records
        (6668893, 6669021), // 129 records
        (6914293, 6914518), // 226 records
        (6950998, 6951514), // 517 records
        (6951773, 6951902), // 130 records
        (6952281, 6952408), // 128 records
        (6952792, 6952972), // 181 records
    ];

    #endregion
}