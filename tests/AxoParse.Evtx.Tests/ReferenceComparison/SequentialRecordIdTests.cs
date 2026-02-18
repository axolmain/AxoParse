using AxoParse.Evtx.Evtx;

namespace AxoParse.Evtx.Tests.ReferenceComparison;

/// <summary>
/// Mirrors the reference parser's inline test_parses_first_10_records test — verifies that
/// EventRecordId values are monotonically increasing (1, 2, 3, ...) within clean files.
/// Divergence here means the parser is skipping, duplicating, or reordering records.
/// </summary>
public class SequentialRecordIdTests(ITestOutputHelper testOutputHelper)
{
    #region Public Methods

    /// <summary>
    /// Verifies record IDs 1..N are sequential in security.evtx (clean file, no gaps expected).
    /// Mirrors reference: assert_eq!(r.event_record_id, i as u64 + 1)
    /// </summary>
    [Fact]
    public void SecurityEvtx_RecordIdsAreSequential()
    {
        AssertSequentialIds("security.evtx");
    }

    /// <summary>
    /// Verifies record IDs are sequential in the dirty security file (14,621 records).
    /// The reference parser unwraps every record — all must succeed and be sequential.
    /// </summary>
    [Fact]
    public void DirtySecurityEvtx_RecordIdsAreSequential()
    {
        AssertSequentialIds("2-system-Security-dirty.evtx");
    }

    /// <summary>
    /// Verifies record IDs are strictly increasing across all chunks in a multi-chunk file.
    /// Does not require contiguity (allows gaps from skipped bad chunks) but requires ordering.
    /// </summary>
    [Fact]
    public void AllCleanFiles_RecordIdsAreStrictlyIncreasing()
    {
        string[] cleanFiles =
        [
            "security.evtx",
            "2-system-Security-dirty.evtx",
            "sysmon.evtx",
            "new-user-security.evtx",
            "Application_no_crc32.evtx"
        ];

        foreach (string fileName in cleanFiles)
        {
            string path = Path.Combine(TestPaths.TestDataDir, fileName);
            if (!File.Exists(path))
                continue;

            byte[] data = File.ReadAllBytes(path);
            EvtxParser parser = EvtxParser.Parse(data, maxThreads: 1, cancellationToken: TestContext.Current.CancellationToken);

            ulong previousId = 0;
            int count = 0;
            foreach (EvtxEvent evt in parser.GetEvents())
            {
                if (!evt.IsSuccess)
                    continue;

                Assert.True(evt.Record.EventRecordId > previousId,
                    $"[{fileName}] Record {evt.Record.EventRecordId} should be > {previousId}");
                previousId = evt.Record.EventRecordId;
                count++;
            }

            testOutputHelper.WriteLine($"[{fileName}] {count} records, all strictly increasing");
        }
    }

    /// <summary>
    /// Verifies that record IDs within each individual chunk are contiguous (no internal gaps).
    /// This matches the reference inline test in evtx_parser.rs that validates per-chunk sequencing.
    /// </summary>
    [Fact]
    public void SecurityEvtx_PerChunkRecordIdsAreContiguous()
    {
        string path = Path.Combine(TestPaths.TestDataDir, "security.evtx");
        byte[] data = File.ReadAllBytes(path);
        EvtxParser parser = EvtxParser.Parse(data, maxThreads: 1, cancellationToken: TestContext.Current.CancellationToken);

        foreach (EvtxChunk chunk in parser.Chunks)
        {
            for (int i = 1; i < chunk.Records.Count; i++)
            {
                ulong prev = chunk.Records[i - 1].EventRecordId;
                ulong curr = chunk.Records[i].EventRecordId;
                Assert.Equal(prev + 1, curr);
            }
        }

        testOutputHelper.WriteLine(
            $"[security.evtx] All {parser.Chunks.Count} chunks have contiguous record IDs");
    }

    #endregion

    #region Non-Public Methods

    /// <summary>
    /// Asserts record IDs start at 1 and increment by 1 for every successful record.
    /// </summary>
    private void AssertSequentialIds(string fileName)
    {
        string path = Path.Combine(TestPaths.TestDataDir, fileName);
        byte[] data = File.ReadAllBytes(path);
        EvtxParser parser = EvtxParser.Parse(data, maxThreads: 1);

        ulong expected = 1;
        foreach (EvtxEvent evt in parser.GetEvents())
        {
            Assert.True(evt.IsSuccess, $"[{fileName}] Record {expected} failed: {evt.Diagnostic}");
            Assert.Equal(expected, evt.Record.EventRecordId);
            expected++;
        }

        testOutputHelper.WriteLine($"[{fileName}] {expected - 1} records, IDs 1..{expected - 1} verified");
    }

    #endregion
}