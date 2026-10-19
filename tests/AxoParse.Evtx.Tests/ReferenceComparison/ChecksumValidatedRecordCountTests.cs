
namespace AxoParse.Evtx.Tests.ReferenceComparison;

/// <summary>
/// Record counts with <c>validateChecksums: true</c> must match the reference parser's
/// <c>evtx_dump --validate-checksums</c> (omerbenamram/evtx @ 7479d02) exactly, for every sample. This pins both
/// CRC32 checks (chunk header and event data) and the <see cref="ChunkFlags.NoCrc32"/> rule: until v0.2.1 the header
/// check XORed two separate CRCs instead of computing one over both ranges, so every chunk failed and every count was 0.
/// </summary>
public class ChecksumValidatedRecordCountTests
{
    #region Public Methods

    /// <summary>
    /// Verifies that checksum-validated parsing keeps exactly the records the reference parser keeps, through
    /// <see cref="EvtxParser.Parse"/>, <see cref="EvtxParser.WriteTo"/> and <see cref="EvtxParser.ParseAsync"/>.
    /// </summary>
    /// <param name="fileName">Sample file in tests/data.</param>
    /// <param name="expected">Record count from <c>evtx_dump -t 1 --validate-checksums</c>.</param>
    [Theory]
    [InlineData("2-system-Microsoft-Windows-LiveId%4Operational.evtx", 399)]
    [InlineData("2-system-Security-dirty.evtx", 14621)]
    [InlineData("2-vss_0-Microsoft-Windows-RemoteDesktopServices-RdpCoreTS%4Operational.evtx", 1719)]
    [InlineData("2-vss_0-Microsoft-Windows-TerminalServices-RemoteConnectionManager%4Operational.evtx", 1608)]
    [InlineData("2-vss_7-System.evtx", 1160)]
    [InlineData("Application_no_crc32.evtx", 17)]
    [InlineData("Archive-ForwardedEvents-test.evtx", 653)]
    [InlineData("E_ShadowCopy6_windows_system32_winevt_logs_Microsoft-Windows-CAPI2%4Operational.evtx", 459)]
    [InlineData("E_Windows_system32_winevt_logs_Microsoft-Windows-CAPI2%4Operational.evtx", 340)]
    [InlineData("E_Windows_system32_winevt_logs_Microsoft-Windows-Shell-Core%4Operational.evtx", 1146)]
    [InlineData("issue_201.evtx", 1)]
    [InlineData("Microsoft-Windows-HelloForBusiness%4Operational.evtx", 0)]
    [InlineData("Microsoft-Windows-LanguagePackSetup%4Operational.evtx", 0)]
    [InlineData("MSExchange_Management_wec.evtx", 1)]
    [InlineData("new-user-security.evtx", 4)]
    [InlineData("post-Security.evtx", 126)]
    [InlineData("sample_with_a_bad_chunk_magic.evtx", 129)]
    [InlineData("sample-with-irregular-bool-values.evtx", 3028)]
    [InlineData("sample-with-zero-data-size-event.evtx", 333)]
    [InlineData("security_bad_string_cache.evtx", 2170)]
    [InlineData("security_big_sample.evtx", 62031)]
    [InlineData("Security_short_selected.evtx", 7)]
    [InlineData("Security_with_size_t.evtx", 636)]
    [InlineData("security.evtx", 2261)]
    [InlineData("sysmon.evtx", 358)]
    [InlineData("system-github.evtx", 1601)]
    public async Task ChecksumValidatedCountMatchesReference(string fileName, int expected)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        byte[] data = await File.ReadAllBytesAsync(Path.Combine(TestPaths.TestDataDir, fileName), token);

        EvtxParser parser = EvtxParser.Parse(data, 1, validateChecksums: true, cancellationToken: token);
        Assert.Equal(expected, parser.TotalRecords);

        int written = EvtxParser.WriteTo(data, Stream.Null, OutputFormat.Xml, 1, validateChecksums: true, token);
        Assert.Equal(expected, written);

        int yielded = 0;
        await foreach (EvtxEvent _ in EvtxParser.ParseAsync(data, 1, validateChecksums: true, cancellationToken: token))
            yielded++;
        Assert.Equal(expected, yielded);
    }

    #endregion
}
