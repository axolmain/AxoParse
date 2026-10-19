using System.Buffers.Binary;

namespace AxoParse.Evtx.Tests;

public class EvtxEventTests
{
    #region Public Methods

    /// <summary>
    /// Verifies that a record that fails to decode has empty output, a <see cref="EvtxDiagnosticKind.RecordRenderFailed"/>
    /// diagnostic carrying its record ID and file offset, and <see cref="EvtxEvent.IsSuccess"/> false, while a clean
    /// record has no diagnostic.
    /// </summary>
    [Fact]
    public void FailedRecordCarriesTypedDiagnostic()
    {
        byte[] data = File.ReadAllBytes(Path.Combine(TestPaths.TestDataDir, "security.evtx"));
        EvtxEvent clean = EvtxParser.Parse(data, cancellationToken: TestContext.Current.CancellationToken).Events.First();
        Assert.True(clean.IsSuccess);
        Assert.False(clean.IsSalvaged);
        Assert.Null(clean.Diagnostic);

        // The first record's BinXml (after the 24-byte record header) is a 4-byte fragment header, then a template
        // instance: 0x0C + 1 unknown + 4 template ID + 4 definition offset, the inline definition (24-byte header +
        // body, first use in the chunk), then the u32 substitution count. A count the record can't hold fails it.
        int instance = (int)clean.FileOffset + 24 + 4;
        Assert.Equal(0x0C, data[instance]);
        int definition = instance + 10;
        Assert.Equal((uint)(definition - 4096), BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(instance + 6)));
        int bodySize = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(definition + 20));
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(definition + 24 + bodySize), uint.MaxValue);
        EvtxEvent failed = EvtxParser.Parse(data, cancellationToken: TestContext.Current.CancellationToken).Events.First();

        Assert.False(failed.IsSuccess);
        Assert.False(failed.IsSalvaged);
        Assert.True(failed.Utf8.IsEmpty);
        Assert.Equal(string.Empty, failed.GetText());
        EvtxDiagnostic diagnostic = Assert.NotNull(failed.Diagnostic);
        Assert.Equal(EvtxDiagnosticKind.RecordRenderFailed, diagnostic.Kind);
        Assert.Equal(clean.RecordId, diagnostic.RecordId);
        Assert.Equal(clean.FileOffset, diagnostic.FileOffset);
        Assert.StartsWith("BinXml render failed: ", diagnostic.Message);
    }

    /// <summary>
    /// Verifies that <see cref="EvtxEvent.GetText"/> and <see cref="EvtxEvent.ToString"/> decode <see cref="EvtxEvent.Utf8"/>.
    /// </summary>
    [Fact]
    public void GetTextDecodesUtf8()
    {
        byte[] data = File.ReadAllBytes(Path.Combine(TestPaths.TestDataDir, "security.evtx"));
        EvtxEvent evt = EvtxParser.Parse(data, cancellationToken: TestContext.Current.CancellationToken).Events.First();

        Assert.StartsWith("<Event ", evt.GetText());
        Assert.Equal(System.Text.Encoding.UTF8.GetString(evt.Utf8.Span), evt.GetText());
        Assert.Equal(evt.GetText(), evt.ToString());
    }

    #endregion
}
