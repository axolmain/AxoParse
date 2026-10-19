using System.Buffers.Binary;
using System.Text;
using AxoParse.Evtx.Wevt;

namespace AxoParse.Evtx.Tests;

/// <summary>
/// <see cref="WevtCache"/> as a fallback, mirroring the Rust evtx crate's test_wevt_cache_fallback.rs: when a record's
/// embedded template is corrupt, the provider template with the same GUID renders it; a usable embedded template is
/// never replaced.
/// </summary>
public class WevtFallbackTests
{
    #region Public Methods

    /// <summary>
    /// Verifies that corrupting the first record's embedded template fails it without a cache, and that a
    /// cache holding a provider template with the template's GUID renders the record through it, in XML and JSON.
    /// </summary>
    [Fact]
    public void CacheRecoversRecordWhoseEmbeddedTemplateIsCorrupt()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        (byte[] corrupted, WevtCache cache) = CorruptFirstTemplate();

        // Without a cache the corrupt body is decoded directly, and its first token is invalid: the record fails
        EvtxEvent withoutCache = EvtxParser.Parse(corrupted, 1, cancellationToken: token).GetEvents().First();
        Assert.False(withoutCache.IsSuccess);
        Assert.Empty(withoutCache.Xml);

        EvtxEvent xml = EvtxParser.Parse(corrupted, 1, wevtCache: cache, cancellationToken: token).GetEvents().First();
        Assert.True(xml.IsSuccess, xml.Diagnostic);
        Assert.StartsWith("<Event><Data>", xml.Xml);
        Assert.EndsWith("</Data></Event>", xml.Xml);

        EvtxEvent json = EvtxParser.Parse(corrupted, 1, OutputFormat.Json, wevtCache: cache, cancellationToken: token)
            .GetEvents().First();
        Assert.True(json.IsSuccess, json.Diagnostic);
        string jsonText = Encoding.UTF8.GetString(json.Json.Span);
        Assert.StartsWith("{\"#name\":\"Event\",\"#content\":[{\"#name\":\"Data\",\"#content\":[\"", jsonText);
    }

    /// <summary>
    /// Verifies that the streaming writer applies the same fallback, and that the first record's output matches what
    /// <see cref="EvtxParser.Parse"/> renders with the cache.
    /// </summary>
    [Fact]
    public void WriteToUsesCacheForCorruptEmbeddedTemplate()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        (byte[] corrupted, WevtCache cache) = CorruptFirstTemplate();

        string expected = EvtxParser.Parse(corrupted, 1, wevtCache: cache, cancellationToken: token).GetEvents().First().Xml;
        using MemoryStream stream = new();
        EvtxParser.WriteTo(corrupted, stream, OutputFormat.Xml, 1, cancellationToken: token, wevtCache: cache);

        Assert.StartsWith(expected, Encoding.UTF8.GetString(stream.ToArray()));
    }

    /// <summary>
    /// Verifies that a cache never overrides a usable embedded template: on the intact file, a provider template
    /// registered under the same GUID as the first record's template (with entirely different markup) leaves the
    /// output byte-identical, through both Parse and WriteTo.
    /// </summary>
    [Fact]
    public void CacheNeverReplacesUsableEmbeddedTemplate()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        byte[] data = File.ReadAllBytes(Path.Combine(TestPaths.TestDataDir, "security.evtx"));
        (_, Guid guid) = FirstTemplate(data);
        WevtCache cache = new();
        Assert.True(cache.AddTemplate(guid, MinimalEventDataTemplate()));

        foreach (OutputFormat format in new[] { OutputFormat.Xml, OutputFormat.Json })
        {
            using MemoryStream plain = new();
            using MemoryStream cached = new();
            EvtxParser.WriteTo(data, plain, format, 1, cancellationToken: token);
            EvtxParser.WriteTo(data, cached, format, 1, cancellationToken: token, wevtCache: cache);
            Assert.True(plain.ToArray().AsSpan().SequenceEqual(cached.ToArray()), $"{format}: cache changed output");

            List<EvtxEvent> plainEvents = EvtxParser.Parse(data, 1, format, cancellationToken: token).GetEvents().ToList();
            List<EvtxEvent> cachedEvents = EvtxParser.Parse(data, 1, format, wevtCache: cache, cancellationToken: token)
                .GetEvents().ToList();
            Assert.Equal(plainEvents.Count, cachedEvents.Count);
            for (int i = 0; i < plainEvents.Count; i++)
            {
                Assert.Equal(plainEvents[i].Xml, cachedEvents[i].Xml);
                Assert.True(plainEvents[i].Json.Span.SequenceEqual(cachedEvents[i].Json.Span));
            }
        }
    }

    #endregion

    #region Non-Public Methods

    /// <summary>
    /// Loads security.evtx, corrupts the first byte of the first record's embedded template body (its fragment
    /// header), and builds a cache holding a minimal provider template under that template's GUID.
    /// </summary>
    /// <returns>The corrupted file and the cache.</returns>
    private static (byte[] Data, WevtCache Cache) CorruptFirstTemplate()
    {
        byte[] data = File.ReadAllBytes(Path.Combine(TestPaths.TestDataDir, "security.evtx"));
        (int defDataOffset, Guid guid) = FirstTemplate(data);
        data[_firstChunkOffset + defDataOffset + 24] = 0xFF;

        WevtCache cache = new();
        Assert.True(cache.AddTemplate(guid, MinimalEventDataTemplate()));
        return (data, cache);
    }

    /// <summary>
    /// Finds the template definition used by the first record of the first chunk: record header (24 bytes),
    /// fragment header (4), then the TemplateInstance token (0x0C) + 1 unknown byte + 4-byte template ID and the
    /// 4-byte chunk-relative definition offset. The definition holds next-offset (4), GUID (16) and size (4).
    /// </summary>
    /// <param name="data">File bytes.</param>
    /// <returns>The definition's chunk-relative offset and its GUID.</returns>
    private static (int DefDataOffset, Guid Guid) FirstTemplate(byte[] data)
    {
        int binXml = _firstChunkOffset + 512 + 24;
        Assert.Equal(0x0F, data[binXml]);
        Assert.Equal(0x0C, data[binXml + 4]);
        int defDataOffset = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(binXml + 4 + 6));
        Guid guid = new(data.AsSpan(_firstChunkOffset + defDataOffset + 4, 16));
        return (defDataOffset, guid);
    }

    /// <summary>
    /// Builds a WEVT template body for <c>&lt;Event&gt;&lt;Data&gt;%0&lt;/Data&gt;&lt;/Event&gt;</c> with WEVT inline
    /// names, as in the upstream test: fragment header, then for each element OpenStartElement (0x01) + dependency
    /// ID (2) + data size (4) + inline name and CloseStartElement (0x02); NormalSubstitution 0x0D index 0 type
    /// string; two EndElement (0x04); EOF (0x00).
    /// </summary>
    /// <returns>The template body.</returns>
    private static byte[] MinimalEventDataTemplate()
    {
        List<byte> buf = [0x0F, 0x01, 0x01, 0x00];
        foreach (string element in new[] { "Event", "Data" })
        {
            buf.Add(0x01);
            buf.AddRange([0xFF, 0xFF, 0, 0, 0, 0]);
            buf.AddRange(WevtInlineName(element));
            buf.Add(0x02);
        }

        buf.AddRange([0x0D, 0x00, 0x00, 0x01, 0x04, 0x04, 0x00]);
        return buf.ToArray();
    }

    /// <summary>
    /// Encodes a WEVT inline name: hash (2, unused by the parser) + character count (2) + UTF-16LE + NUL (2).
    /// </summary>
    /// <param name="name">Name to encode.</param>
    /// <returns>The encoded bytes.</returns>
    private static byte[] WevtInlineName(string name)
    {
        byte[] bytes = new byte[4 + (name.Length * 2) + 2];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(2), (ushort)name.Length);
        Encoding.Unicode.GetBytes(name, bytes.AsSpan(4));
        return bytes;
    }

    #endregion

    #region Non-Public Fields

    private const int _firstChunkOffset = 4096;

    #endregion
}
