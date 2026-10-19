using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace AxoParse.Evtx.Tests;

/// <summary>
/// Strict versus salvage rendering of corrupt BinXml. By default a record whose BinXml is corrupt fails with a
/// diagnostic and no output, as in the Rust evtx crate; with <c>salvage: true</c> it keeps the well-formed output
/// decoded before the corruption, with every open element closed, and still reports the diagnostic.
/// </summary>
public class SalvageTests
{
    #region Public Methods

    /// <summary>
    /// Verifies that a corrupt token in the middle of a template body fails the record in strict mode (empty output,
    /// diagnostic naming the token) and that salvage mode keeps a well-formed prefix ending at the corruption.
    /// </summary>
    /// <param name="format">Output format.</param>
    [Theory]
    [InlineData(OutputFormat.Xml)]
    [InlineData(OutputFormat.JsonStructured)]
    public void CorruptTemplateFailsRecordByDefaultAndKeepsWellFormedPrefixWhenSalvaging(OutputFormat format)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        byte[] data = CorruptChannelClose();

        EvtxEvent strict = EvtxParser.Parse(data, new EvtxOptions { MaxThreads = 1, Format = format }, token).Events.First();
        Assert.False(strict.IsSuccess);
        Assert.Contains("unexpected token 0xFF", strict.Diagnostic?.Message);
        Assert.Empty(strict.GetText());
        Assert.True(strict.Utf8.IsEmpty);

        EvtxEvent salvaged = EvtxParser.Parse(data, new EvtxOptions { MaxThreads = 1, Format = format, Salvage = true }, token).Events.First();
        Assert.False(salvaged.IsSuccess);
        Assert.StartsWith("BinXml salvaged", salvaged.Diagnostic?.Message);
        Assert.Contains("unexpected token 0xFF", salvaged.Diagnostic?.Message);

        if (format == OutputFormat.Xml)
        {
            // Channel lost its CloseStartElement, so it renders empty; decoding stops right after it and the open
            // System and Event elements are closed
            XElement root = XDocument.Parse(salvaged.GetText()).Root!;
            Assert.Equal("Event", root.Name.LocalName);
            Assert.EndsWith("<Channel></Channel></System></Event>", salvaged.GetText());
            Assert.DoesNotContain("<Computer>", salvaged.GetText());
        }
        else
        {
            using JsonDocument json = JsonDocument.Parse(salvaged.Utf8);
            Assert.Equal("Event", json.RootElement.GetProperty("#name").GetString());
            Assert.EndsWith("{\"#name\":\"Channel\"}]}]}", Encoding.UTF8.GetString(salvaged.Utf8.Span));
        }
    }

    /// <summary>
    /// Verifies that the streaming writer follows the same rule in both modes: its output equals the concatenated
    /// output of <see cref="EvtxParser.Parse"/>, so strict mode writes nothing for the corrupt records and salvage
    /// mode writes their prefixes.
    /// </summary>
    /// <param name="salvage">Whether to salvage corrupt records.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WriteToMatchesParseInBothModes(bool salvage)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        byte[] data = CorruptChannelClose();

        StringBuilder expected = new();
        foreach (EvtxEvent evt in EvtxParser.Parse(data, new EvtxOptions { MaxThreads = 1, Salvage = salvage }, token).Events)
            expected.Append(evt.GetText());

        using MemoryStream stream = new();
        EvtxParser.WriteTo(data, stream, new EvtxOptions { Format = OutputFormat.Xml, MaxThreads = 1, Salvage = salvage }, token);
        Assert.Equal(expected.ToString(), Encoding.UTF8.GetString(stream.ToArray()));
    }

    #endregion

    #region Non-Public Methods

    /// <summary>
    /// Loads security.evtx and corrupts the first record's embedded template: the CloseStartElement (0x02) that
    /// follows the inline name of its <c>Channel</c> element becomes 0xFF, an unknown token. The template no longer
    /// compiles, so every record of the first chunk that uses it is decoded directly and hits the corruption inside
    /// <c>System</c>. A chunk name structure is 4 next-offset + 2 hash + 2 character count + UTF-16LE characters +
    /// 2 NUL, and the element has no attributes, so its close token follows the NUL.
    /// </summary>
    /// <returns>The corrupted file.</returns>
    private static byte[] CorruptChannelClose()
    {
        byte[] data = File.ReadAllBytes(Path.Combine(TestPaths.TestDataDir, "security.evtx"));
        int binXml = _firstChunkOffset + 512 + 24;
        Assert.Equal(0x0C, data[binXml + 4]);
        int defDataOffset = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(binXml + 4 + 6));
        int bodyStart = _firstChunkOffset + defDataOffset + 24;
        int bodySize = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(bodyStart - 4));

        byte[] name = Encoding.Unicode.GetBytes("Channel\0");
        int at = data.AsSpan(bodyStart, bodySize).IndexOf(name);
        Assert.True(at > 0, "Channel name not found in the first template");
        int close = bodyStart + at + name.Length;
        Assert.Equal(0x02, data[close]);
        data[close] = 0xFF;
        return data;
    }

    #endregion

    #region Non-Public Fields

    /// <summary>
    /// File offset of the first chunk (after the 4 KB file header).
    /// </summary>
    private const int _firstChunkOffset = 4096;

    #endregion
}
