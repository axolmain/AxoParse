using AxoParse.Evtx.Evtx;

namespace AxoParse.Evtx.Tests.RustTestImitation;

/// <summary>
/// Mirrors the Rust parser's test_full_samples.rs — parses each sample file and asserts record counts.
/// For clean files: exact match with Rust's expected values.
/// For corrupt files (bad checksum/magic): AxoParse recovers more records than Rust, so we assert
/// at-least-Rust-count. The extra records are validated in RecoveredRecordValidationTests.
/// </summary>
public class FullSampleRecordCountTests(ITestOutputHelper testOutputHelper)
{
    #region Public Methods

    /// <summary>
    /// Mirrors test_dirty_sample_single_threaded: 2-system-Security-dirty.evtx must yield 14,621 records.
    /// </summary>
    [Fact]
    public void DirtySecuritySingleThreaded_14621Records()
    {
        AssertRecordCount("2-system-Security-dirty.evtx", expectedOk: 14621, expectedErr: 0, maxThreads: 1);
    }

    /// <summary>
    /// Mirrors test_dirty_sample_parallel: same file, multi-threaded, must still yield 14,621 records.
    /// </summary>
    [Fact]
    public void DirtySecurityParallel_14621Records()
    {
        AssertRecordCount("2-system-Security-dirty.evtx", expectedOk: 14621, expectedErr: 0, maxThreads: 0);
    }

    /// <summary>
    /// Mirrors test_parses_sample_with_irregular_boolean_values: 3,028 records, 0 errors.
    /// </summary>
    [Fact]
    public void IrregularBoolValues_3028Records()
    {
        AssertRecordCount("sample-with-irregular-bool-values.evtx", expectedOk: 3028, expectedErr: 0);
    }

    /// <summary>
    /// Mirrors test_dirty_sample_with_a_bad_checksum: Rust gets 1,910 ok + 4 errors.
    /// AxoParse recovers more records from bad-checksum chunks (validated in RecoveredRecordValidationTests).
    /// </summary>
    [Fact]
    public void BadChecksum_AtLeast1910Ok()
    {
        AssertRecordCountAtLeast(
            "2-vss_0-Microsoft-Windows-RemoteDesktopServices-RdpCoreTS%4Operational.evtx",
            minOk: 1910);
    }

    /// <summary>
    /// Mirrors test_dirty_sample_with_a_bad_checksum_2: Rust gets 1,774 ok + 2 errors.
    /// AxoParse recovers more records from bad-checksum chunks (validated in RecoveredRecordValidationTests).
    /// </summary>
    [Fact]
    public void BadChecksum2_AtLeast1774Ok()
    {
        AssertRecordCountAtLeast(
            "2-vss_0-Microsoft-Windows-TerminalServices-RemoteConnectionManager%4Operational.evtx",
            minOk: 1774);
    }

    /// <summary>
    /// Mirrors test_dirty_sample_with_a_chunk_past_zeros: 1,160 records.
    /// </summary>
    [Fact]
    public void ChunkPastZeros_1160Records()
    {
        AssertRecordCount("2-vss_7-System.evtx", expectedOk: 1160, expectedErr: 0);
    }

    /// <summary>
    /// Mirrors test_dirty_sample_with_a_bad_chunk_magic: Rust gets 270 ok + 2 errors.
    /// AxoParse recovers more records from bad-magic chunks (validated in RecoveredRecordValidationTests).
    /// </summary>
    [Fact]
    public void BadChunkMagic_AtLeast270Ok()
    {
        AssertRecordCountAtLeast("sample_with_a_bad_chunk_magic.evtx", minOk: 270);
    }

    /// <summary>
    /// Mirrors test_dirty_sample_binxml_with_incomplete_token (HelloForBusiness): 6 records.
    /// </summary>
    [Fact]
    public void IncompleteSidToken_6Records()
    {
        AssertRecordCount("Microsoft-Windows-HelloForBusiness%4Operational.evtx", expectedOk: 6, expectedErr: 0);
    }

    /// <summary>
    /// Mirrors test_dirty_sample_binxml_with_incomplete_template (LanguagePackSetup): 17 records.
    /// </summary>
    [Fact]
    public void IncompleteTemplate_17Records()
    {
        AssertRecordCount("Microsoft-Windows-LanguagePackSetup%4Operational.evtx", expectedOk: 17, expectedErr: 0);
    }

    /// <summary>
    /// Mirrors test_sample_with_multiple_xml_fragments (Shell-Core): 1,146 records.
    /// </summary>
    [Fact]
    public void MultipleXmlFragments_1146Records()
    {
        AssertRecordCount(
            "E_Windows_system32_winevt_logs_Microsoft-Windows-Shell-Core%4Operational.evtx",
            expectedOk: 1146, expectedErr: 0);
    }

    /// <summary>
    /// Mirrors test_issue_65 (CAPI2 shadow copy): 459 records.
    /// </summary>
    [Fact]
    public void Issue65_Capi2ShadowCopy_459Records()
    {
        AssertRecordCount(
            "E_ShadowCopy6_windows_system32_winevt_logs_Microsoft-Windows-CAPI2%4Operational.evtx",
            expectedOk: 459, expectedErr: 0);
    }

    /// <summary>
    /// Mirrors test_sample_with_binxml_as_substitution_tokens_and_pi_target (CAPI2): 340 records.
    /// </summary>
    [Fact]
    public void SubstitutionTokensAndPiTarget_340Records()
    {
        AssertRecordCount(
            "E_Windows_system32_winevt_logs_Microsoft-Windows-CAPI2%4Operational.evtx",
            expectedOk: 340, expectedErr: 0);
    }

    /// <summary>
    /// Mirrors test_sample_with_dependency_identifier_edge_case: 653 records.
    /// </summary>
    [Fact]
    public void DependencyIdEdgeCase_653Records()
    {
        AssertRecordCount("Archive-ForwardedEvents-test.evtx", expectedOk: 653, expectedErr: 0);
    }

    /// <summary>
    /// Mirrors test_sample_with_no_crc32: 17 records.
    /// </summary>
    [Fact]
    public void NoCrc32_17Records()
    {
        AssertRecordCount("Application_no_crc32.evtx", expectedOk: 17, expectedErr: 0);
    }

    /// <summary>
    /// Mirrors test_sample_with_invalid_flags_in_header: 126 records.
    /// </summary>
    [Fact]
    public void InvalidHeaderFlags_126Records()
    {
        AssertRecordCount("post-Security.evtx", expectedOk: 126, expectedErr: 0);
    }

    /// <summary>
    /// Greedy recovery variant of Rust's test_sample_with_zero_data_size_event.
    /// Rust stops scanning a chunk on the first zero-data-size record (336 total).
    /// We skip bad records and keep scanning, recovering 449 valid records from the same file.
    /// </summary>
    [Fact]
    public void ZeroDataSizeEvent_GreedyRecovery()
    {
        string path = Path.Combine(TestPaths.TestDataDir, "sample-with-zero-data-size-event.evtx");
        byte[] data = File.ReadAllBytes(path);
        EvtxParser parser = EvtxParser.Parse(data, maxThreads: 1);

        int okCount = 0;
        int errCount = 0;
        foreach (EvtxEvent evt in parser.GetEvents())
        {
            if (evt.IsSuccess)
                okCount++;
            else
                errCount++;
        }

        int total = okCount + errCount;
        testOutputHelper.WriteLine(
            $"[sample-with-zero-data-size-event.evtx] ok={okCount}, err={errCount}, total={total}");

        // Greedy recovery finds more records than Rust's 336 (Rust stops chunk on first bad record)
        Assert.True(total >= 336, $"Expected at least 336 records (Rust baseline) but got {total}");
        Assert.Equal(449, okCount);
    }

    #endregion

    #region Non-Public Methods

    /// <summary>
    /// Asserts that the parser recovers at least <paramref name="minOk"/> records.
    /// Used for corrupt files where AxoParse's recovery mode finds more records than the Rust parser.
    /// </summary>
    /// <param name="fileName">File name relative to the test data directory.</param>
    /// <param name="minOk">Minimum number of successfully rendered records (Rust parser's count).</param>
    /// <param name="maxThreads">Thread count (1 = single-threaded, 0 = all cores).</param>
    private void AssertRecordCountAtLeast(string fileName, int minOk, int maxThreads = 1)
    {
        string path = Path.Combine(TestPaths.TestDataDir, fileName);
        byte[] data = File.ReadAllBytes(path);
        EvtxParser parser = EvtxParser.Parse(data, maxThreads: maxThreads);

        int okCount = 0;
        int errCount = 0;
        foreach (EvtxEvent evt in parser.GetEvents())
        {
            if (evt.IsSuccess)
                okCount++;
            else
                errCount++;
        }

        testOutputHelper.WriteLine(
            $"[{fileName}] ok={okCount}, err={errCount} (Rust baseline: {minOk} ok)");

        Assert.True(okCount >= minOk,
            $"Expected at least {minOk} ok records (Rust baseline) but got {okCount}");
    }

    /// <summary>
    /// Core assertion helper mirroring the Rust test_full_sample() function.
    /// Parses the file and counts successful vs failed event renders.
    /// </summary>
    /// <param name="fileName">File name relative to the test data directory.</param>
    /// <param name="expectedOk">Expected number of successfully rendered records.</param>
    /// <param name="expectedErr">Expected number of render failures.</param>
    /// <param name="maxThreads">Thread count (1 = single-threaded, 0 = all cores).</param>
    private void AssertRecordCount(string fileName, int expectedOk, int expectedErr, int maxThreads = 1)
    {
        string path = Path.Combine(TestPaths.TestDataDir, fileName);
        byte[] data = File.ReadAllBytes(path);
        EvtxParser parser = EvtxParser.Parse(data, maxThreads: maxThreads);

        int okCount = 0;
        int errCount = 0;
        foreach (EvtxEvent evt in parser.GetEvents())
        {
            if (evt.IsSuccess)
                okCount++;
            else
                errCount++;
        }

        testOutputHelper.WriteLine($"[{fileName}] ok={okCount}, err={errCount}");

        Assert.Equal(expectedOk, okCount);
        Assert.Equal(expectedErr, errCount);
    }

    #endregion
}