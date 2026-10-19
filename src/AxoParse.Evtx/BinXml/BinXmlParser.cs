using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using AxoParse.Evtx.Evtx;
using AxoParse.Evtx.Wevt;

namespace AxoParse.Evtx.BinXml;

/// <summary>
/// Core BinXml parser. One instance per chunk. Produces XML strings from BinXml token streams.
/// </summary>
internal sealed partial class BinXmlParser
{
    #region Constructors And Destructors

    /// <summary>
    /// Initialises a parser scoped to a single 64 KB EVTX chunk.
    /// Names are resolved lazily (compiled templates never look them up), but the 64-entry common string
    /// offset table at chunk offset 128 is still checked so a corrupt entry fails the chunk exactly as before.
    /// </summary>
    /// <param name="fileData">Complete EVTX file bytes.</param>
    /// <param name="chunkFileOffset">Absolute byte offset of the chunk within <paramref name="fileData"/>.</param>
    /// <param name="templates">Preloaded template definitions for this chunk, keyed by chunk-relative offset.</param>
    /// <param name="compiledCache">Cross-chunk cache of templates compiled for the output format this parser renders, keyed by
    /// GUID, or null to decode every template body. Never share a cache between formats.</param>
    /// <param name="wevtCache">Provider templates used when a record's embedded template is missing or corrupt, or null.</param>
    /// <param name="salvage">True to keep the well-formed prefix of a record whose BinXml is corrupt instead of failing it.</param>
    public BinXmlParser(
        byte[] fileData,
        int chunkFileOffset,
        Dictionary<uint, BinXmlTemplateDefinition> templates,
        Dictionary<Guid, CompiledTemplate?>? compiledCache,
        WevtCache? wevtCache = null,
        bool salvage = false)
    {
        _salvage = salvage;
        _wevtCache = wevtCache;
        _fileData = fileData;
        _chunkFileOffset = chunkFileOffset;
        _chunkEnd = Math.Min(chunkFileOffset + EvtxChunk.ChunkSize, fileData.Length);
        _templates = templates;
        _compiledCache = compiledCache;

        // Common string offset table: 64 uint32 entries at chunk offset 128. Entries are no longer decoded up front,
        // but offsets 0xFFFFFFF8..0xFFFFFFFF wrap past the bounds check and made the eager read throw; keep that
        // failure so corrupt chunks are reported identically. Every other entry decodes without throwing.
        ReadOnlySpan<byte> chunkData = fileData.AsSpan(chunkFileOffset, EvtxChunk.ChunkSize);
        ReadOnlySpan<uint> commonOffsets = MemoryMarshal.Cast<byte, uint>(chunkData.Slice(128, 256));
        for (int i = 0; i < commonOffsets.Length; i++)
        {
            uint offset = commonOffsets[i];
            if ((offset != 0) && (offset + 8 < EvtxChunk.ChunkSize) && ((int)offset < 0))
            {
                ReadNameFromChunk(offset);
            }
        }
    }

    #endregion

    #region Properties

    /// <summary>
    /// Salvage mode only: the first corruption that cut the last rendered record short, or null if it rendered in full.
    /// </summary>
    internal string? SalvageDiagnostic => _salvageDiagnostic;

    #endregion

    #region Public Methods

    /// <summary>
    /// Renders a record's BinXml event data in <paramref name="format"/> and appends it to <paramref name="sink"/>
    /// as UTF-8 without materialising a string or array for the record. Nothing is appended if rendering throws.
    /// </summary>
    /// <param name="record">The EVTX record whose BinXml event data will be rendered.</param>
    /// <param name="format">Output format (XML or JSON).</param>
    /// <param name="sink">Buffer that receives the UTF-8 output.</param>
    internal void AppendRecordUtf8(EvtxRecord record, OutputFormat format, ArrayBufferWriter<byte> sink)
    {
        // Render straight into the sink's free space; only a record larger than it is copied from a pooled buffer
        Span<byte> free = sink.GetSpan(_recordBufferBytes);
        ValueUtf8Builder vub = new(free);
        RenderRecord(record, format, ref vub);
        ReadOnlySpan<byte> utf8 = vub.AsSpan();
        if (vub.IsInInitialBuffer)
        {
            sink.Advance(utf8.Length);
        }
        else
        {
            utf8.CopyTo(sink.GetSpan(utf8.Length));
            sink.Advance(utf8.Length);
        }

        vub.Dispose();
    }

    /// <summary>
    /// Renders a record's BinXml event data in <paramref name="format"/> into <paramref name="vub"/>.
    /// </summary>
    /// <param name="record">The EVTX record whose BinXml event data will be rendered.</param>
    /// <param name="format">Output format (XML or JSON).</param>
    /// <param name="vub">UTF-8 builder that receives the output.</param>
    internal void RenderRecord(EvtxRecord record, OutputFormat format, ref ValueUtf8Builder vub)
    {
        ReadOnlySpan<byte> eventData = record.GetEventData(_fileData);
        int binxmlChunkBase = record.EventDataFileOffset - _chunkFileOffset;
        int pos = 0;
        _salvageDiagnostic = null;
        if (format == OutputFormat.Json)
            DecodeTopLevel<JsonWriter>(eventData, ref pos, binxmlChunkBase, ref vub, recordLevel: true, escaped: false);
        else
            DecodeTopLevel<XmlWriter>(eventData, ref pos, binxmlChunkBase, ref vub, recordLevel: true, escaped: false);
    }

    #endregion

    #region Non-Public Methods

    /// <summary>
    /// Returns <paramref name="size"/> bytes at <paramref name="fileOffset"/>, which must lie within this chunk.
    /// Offsets and sizes come from the file; a range running past the chunk end throws, failing only the current
    /// record (the chunk render loop reports it as a diagnostic) instead of reading the next chunk's bytes.
    /// </summary>
    /// <param name="fileOffset">Offset into <see cref="_fileData"/>, at or after the chunk start.</param>
    /// <param name="size">Number of bytes.</param>
    /// <returns>The requested bytes.</returns>
    private ReadOnlySpan<byte> ChunkBytes(int fileOffset, int size)
    {
        if ((uint)fileOffset + (uint)size > (uint)_chunkEnd)
            ThrowValuePastChunkEnd(fileOffset, size);
        return _fileData.AsSpan(fileOffset, size);
    }

    /// <summary>
    /// Throws for a substitution value that runs past the chunk end (kept out of line so the check stays small).
    /// </summary>
    /// <param name="fileOffset">Value offset read from the file.</param>
    /// <param name="size">Value size read from the file.</param>
    [DoesNotReturn]
    private void ThrowValuePastChunkEnd(int fileOffset, int size) =>
        throw new InvalidDataException(
            $"Substitution value at 0x{fileOffset:X} ({size} bytes) runs past the end of its chunk at 0x{_chunkEnd:X}");

    /// <summary>
    /// Reads a template instance's substitution count (u32 at <paramref name="countPos"/>) and rejects counts the
    /// remaining data can't hold: each substitution has a 4-byte descriptor (u16 size, u8 type, u8 pad) after the
    /// count. Checked before the count sizes any buffer, since it comes straight from file bytes. Throwing fails only
    /// the current record, which the chunk render loop reports as a diagnostic.
    /// </summary>
    /// <param name="data">BinXml bytes containing the template instance.</param>
    /// <param name="countPos">Position of the u32 substitution count in <paramref name="data"/>.</param>
    /// <returns>The substitution count, at most <c>(data.Length - countPos - 4) / 4</c>.</returns>
    private static int ReadSubstitutionCount(ReadOnlySpan<byte> data, int countPos)
    {
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(countPos, 4));
        if (count > (uint)(data.Length - countPos - 4) / 4)
            ThrowSubstitutionCountTooLarge(count);
        return (int)count;
    }

    /// <summary>
    /// Throws for a substitution count larger than the record data can hold (kept out of line so the check stays small).
    /// </summary>
    /// <param name="count">Substitution count read from the file.</param>
    [DoesNotReturn]
    private static void ThrowSubstitutionCountTooLarge(uint count) =>
        throw new InvalidDataException($"Template instance claims {count} substitutions, more than the record data can hold");

    /// <summary>
    /// Skips an inline name structure if one is present at the current position.
    /// Inline bytes exist only when <paramref name="nameOffset"/> equals the chunk-relative
    /// position (i.e., the name is defined here for the first time, not a back-reference).
    /// Layout: 4 unknown + 2 hash + 2 numChars + numChars*2 UTF-16LE string + 2 null terminator
    /// = 10 + numChars*2 bytes total.
    /// </summary>
    /// <param name="data">BinXml byte stream.</param>
    /// <param name="pos">Current read position; advanced past inline bytes on success.</param>
    /// <param name="nameOffset">The chunk-relative name offset read from the preceding token.</param>
    /// <param name="binxmlChunkBase">Chunk-relative base offset of <paramref name="data"/>, used for inline detection.</param>
    /// <returns>True if parsing can continue; false if bounds check failed (caller should bail).</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TrySkipInlineName(ReadOnlySpan<byte> data, ref int pos, uint nameOffset, int binxmlChunkBase)
    {
        if (nameOffset != (uint)(binxmlChunkBase + pos))
            return true;

        if (pos + 8 > data.Length)
            return false;

        ushort numChars = MemoryMarshal.Read<ushort>(data[(pos + 6)..]);
        int inlineNameBytes = 10 + numChars * 2;

        if (pos + inlineNameBytes > data.Length)
            return false;

        pos += inlineNameBytes;
        return true;
    }

    /// <summary>
    /// Renders a TemplateInstance token (0x0C): reads its header and substitution values, then writes the template
    /// through its compiled form, compiling it on first use. A template that is missing, runs past its chunk or is
    /// corrupt falls back to the provider template with the same GUID, as in the Rust evtx crate (a usable embedded
    /// template is never replaced); a usable template that isn't compiled is decoded directly.
    /// </summary>
    /// <typeparam name="TWriter">Output format.</typeparam>
    /// <param name="data">BinXml byte stream.</param>
    /// <param name="pos">Current read position; advanced past the entire template instance on return.</param>
    /// <param name="binxmlChunkBase">Chunk-relative base offset of <paramref name="data"/>.</param>
    /// <param name="vsb">Output builder.</param>
    /// <param name="escaped">JSON only: emit the instance JSON-string-escaped (embedded BinXml 0x21 context).</param>
    private void WriteTemplateInstance<TWriter>(ReadOnlySpan<byte> data, ref int pos, int binxmlChunkBase,
                                                ref ValueUtf8Builder vsb, bool escaped)
        where TWriter : struct, IBinXmlWriter
    {
        // Peek at numValues to size the stackalloc buffers.
        // Header layout: 6 skip + 4 defDataOffset [+ optional inline] + 4 numValues.
        // ReadTemplateInstanceData handles the full parse; we just need numValues for buffer sizing.
        int peekPos = pos + 6;
        uint defDataOffset = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(peekPos, 4));
        peekPos += 4;
        uint peekChunkRel = (uint)(binxmlChunkBase + peekPos);
        if (defDataOffset == peekChunkRel)
        {
            // inline: skip 4 next-ptr + 16 GUID + 4 dataSize + dataSize body
            uint inlineDataSize = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(peekPos + 20, 4));
            peekPos += 24 + (int)inlineDataSize;
        }
        int numVals = ReadSubstitutionCount(data, peekPos);

        // stackalloc for the common case (≤ 64 substitutions); heap fallback for rare large templates
        const int StackThreshold = 64;
        Span<int> valueOffsets = numVals <= StackThreshold ? stackalloc int[StackThreshold] : new int[numVals];
        Span<int> valueSizes = numVals <= StackThreshold ? stackalloc int[StackThreshold] : new int[numVals];
        Span<byte> valueTypes = numVals <= StackThreshold ? stackalloc byte[StackThreshold] : new byte[numVals];

        TemplateInstanceHeader hdr = ReadTemplateInstanceData(data, ref pos, binxmlChunkBase,
            valueOffsets, valueSizes, valueTypes);

        ReadOnlySpan<int> offsets = valueOffsets.Slice(0, hdr.NumValues);
        ReadOnlySpan<int> sizes = valueSizes.Slice(0, hdr.NumValues);
        ReadOnlySpan<byte> types = valueTypes.Slice(0, hdr.NumValues);

        int tplBodyFileOffset = _chunkFileOffset + (int)hdr.DefDataOffset + 24;
        bool embedded = (hdr.DataSize != 0) && ((uint)(tplBodyFileOffset + hdr.DataSize) <= (uint)_chunkEnd);

        CompiledTemplate? compiled = null;
        bool compileFailed = false;
        if (embedded && (_compiledCache != null))
        {
            if (!_compiledCache.TryGetValue(hdr.TemplateGuid, out compiled))
            {
                compiled = CompileProgram((int)hdr.DefDataOffset, (int)hdr.DataSize) is { } program
                    ? Lower<TWriter>(program)
                    : null;
                _compiledCache[hdr.TemplateGuid] = compiled;
            }
            compileFailed = compiled == null;
        }

        if ((!embedded || compileFailed) && (_wevtCache?.Get(hdr.TemplateGuid, TWriter.Format) is { } provider))
            compiled = provider;

        if ((compiled != null) && (compiled != CompiledTemplate.DecodeDirectly))
        {
            WriteCompiled<TWriter>(compiled, offsets, sizes, types, binxmlChunkBase, ref vsb, escaped);
            return;
        }

        if (!embedded) return;

        // Not compiled: decode the template body directly with this instance's substitution values
        ReadOnlySpan<byte> tplBody = _fileData.AsSpan(tplBodyFileOffset, (int)hdr.DataSize);
        SubstitutionValues values = new(offsets, sizes, types);
        int tplChunkBase = (int)hdr.DefDataOffset + 24;
        if (!escaped)
        {
            DecodeTemplateBody<TWriter>(tplBody, values, tplChunkBase, ref vsb);
            return;
        }

        ValueUtf8Builder escapeVsb = new(stackalloc byte[512]);
        DecodeTemplateBody<TWriter>(tplBody, values, tplChunkBase, ref escapeVsb);
        BinXmlValueFormatter.AppendJsonEscapedUtf8(ref vsb, escapeVsb.AsSpan());
        escapeVsb.Dispose();
    }

    /// <summary>
    /// Resolves a name from the per-chunk cache, reading from chunk data on cache miss.
    /// </summary>
    /// <param name="chunkRelOffset">Chunk-relative byte offset of the name structure.</param>
    /// <returns>The cached or freshly-read name string.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private string ReadName(uint chunkRelOffset)
    {
        Dictionary<uint, string> cache = _nameCache ??= new Dictionary<uint, string>(64);
        if (cache.TryGetValue(chunkRelOffset, out string? cached))
            return cached;
        string name = ReadNameFromChunk(chunkRelOffset);
        cache[chunkRelOffset] = name;
        return name;
    }

    /// <summary>
    /// Reads a name string directly from the chunk at the given offset.
    /// Name structure layout: 4 unknown + 2 hash + 2 numChars + numChars*2 UTF-16LE string.
    /// </summary>
    /// <param name="chunkRelOffset">Chunk-relative byte offset of the name structure.</param>
    /// <returns>The decoded UTF-16LE name string, or empty string if out of bounds.</returns>
    private string ReadNameFromChunk(uint chunkRelOffset)
    {
        ReadOnlySpan<byte> chunkData = _fileData.AsSpan(_chunkFileOffset, EvtxChunk.ChunkSize);
        int offset = (int)chunkRelOffset;
        if (offset + 8 > chunkData.Length) return string.Empty;
        ushort numChars = MemoryMarshal.Read<ushort>(chunkData[(offset + 6)..]);
        if (offset + 8 + numChars * 2 > chunkData.Length) return string.Empty;
        ReadOnlySpan<char> chars = MemoryMarshal.Cast<byte, char>(chunkData.Slice(offset + 8, numChars * 2));
        return new string(chars);
    }

    /// <summary>
    /// Reads the format-agnostic data from a TemplateInstance token (0x0C): template definition
    /// identity, substitution descriptors, and value metadata. Shared by both XML and JSON paths.
    /// Layout: 1 token + 1 unknown + 4 unknown + 4 defDataOffset [+ 24-byte inline header + body]
    /// + 4 numValues + descriptors + values.
    /// Writes substitution metadata into caller-provided spans to avoid per-record heap allocations.
    /// </summary>
    /// <param name="data">BinXml byte stream.</param>
    /// <param name="pos">Current read position; advanced past the entire template instance data.</param>
    /// <param name="binxmlChunkBase">Chunk-relative base offset of <paramref name="data"/>.</param>
    /// <param name="valueOffsets">Caller-provided buffer; filled with absolute file offsets of each substitution value.</param>
    /// <param name="valueSizes">Caller-provided buffer; filled with byte sizes of each substitution value.</param>
    /// <param name="valueTypes">Caller-provided buffer; filled with BinXml value type codes.</param>
    /// <returns>Parsed template instance header (guid, sizes, value count) for downstream rendering.</returns>
    private TemplateInstanceHeader ReadTemplateInstanceData(
        ReadOnlySpan<byte> data, ref int pos, int binxmlChunkBase,
        Span<int> valueOffsets, Span<int> valueSizes, Span<byte> valueTypes)
    {
        int p = pos;

        // token + unknown1 + unknown2
        p += 6;

        // defDataOffset
        uint defDataOffset =
            BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(p, 4));
        p += 4;

        uint currentChunkRelOffset = (uint)(binxmlChunkBase + p);

        Guid templateGuid = default;
        uint dataSize = 0;

        if (defDataOffset == currentChunkRelOffset)
        {
            // inline
            p += 4; // next def offset

            templateGuid = new Guid(data.Slice(p, 16));
            p += 16;

            dataSize =
                BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(p, 4));
            p += 4;

            p += (int)dataSize;
        }
        else if (_templates.TryGetValue(defDataOffset, out BinXmlTemplateDefinition def))
        {
            templateGuid = def.Guid;
            dataSize = def.DataSize;
        }
        else if (defDataOffset + 24 <= EvtxChunk.ChunkSize)
        {
            // fallback read
            ReadOnlySpan<byte> chunkData =
                _fileData.AsSpan(_chunkFileOffset, EvtxChunk.ChunkSize);

            int baseOffset = (int)defDataOffset;

            templateGuid = new Guid(chunkData.Slice(baseOffset + 4, 16));

            dataSize = BinaryPrimitives.ReadUInt32LittleEndian(
                chunkData.Slice(baseOffset + 20, 4));
        }

        // numValues
        uint numValues =
            BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(p, 4));
        p += 4;

        int numVals = (int)numValues;

        // Descriptor block: numVals × 4-byte descriptors (u16 size + u8 type + u8 padding)
        ReadOnlySpan<SubstitutionDescriptor> descriptors =
            MemoryMarshal.Cast<byte, SubstitutionDescriptor>(data.Slice(p, numVals * 4));
        p += numVals * 4;

        int dataBaseFileOffset = _chunkFileOffset + binxmlChunkBase;

        for (int i = 0; i < numVals; i++)
        {
            SubstitutionDescriptor desc = descriptors[i];

            valueOffsets[i] = dataBaseFileOffset + p;
            valueSizes[i] = desc.Size;
            valueTypes[i] = desc.Type;

            p += desc.Size;
        }

        pos = p;

        return new TemplateInstanceHeader(templateGuid, dataSize, defDataOffset, numVals);
    }

    /// <summary>
    /// Writes a compiled template: its static parts interleaved with this instance's formatted substitution values.
    /// A slot whose value is missing (index past the instance's values) or optional and empty writes nothing,
    /// including its conditional markup.
    /// </summary>
    /// <typeparam name="TWriter">Output format the template was compiled for.</typeparam>
    /// <param name="compiled">Compiled template.</param>
    /// <param name="valueOffsets">File offsets of each substitution value.</param>
    /// <param name="valueSizes">Byte sizes of each substitution value.</param>
    /// <param name="valueTypes">BinXml value type codes for each substitution.</param>
    /// <param name="binxmlChunkBase">Chunk-relative base offset used for embedded BinXml resolution.</param>
    /// <param name="vsb">Output builder.</param>
    /// <param name="escaped">JSON only: write the escaped parts and escape values twice (embedded BinXml context).</param>
    private void WriteCompiled<TWriter>(CompiledTemplate compiled,
                                        scoped ReadOnlySpan<int> valueOffsets, scoped ReadOnlySpan<int> valueSizes,
                                        scoped ReadOnlySpan<byte> valueTypes,
                                        int binxmlChunkBase, ref ValueUtf8Builder vsb, bool escaped)
        where TWriter : struct, IBinXmlWriter
    {
        byte[][] parts = escaped ? compiled.EscapedParts : compiled.Parts;
        SubSlot[] slots = compiled.Slots;
        int valCount = valueOffsets.Length;

        vsb.Append(parts[0]);
        for (int i = 0; i < slots.Length; i++)
        {
            ref readonly SubSlot slot = ref slots[i];
            int subId = slot.SubId;
            if (subId < valCount)
            {
                byte valType = valueTypes[subId];
                int valSize = valueSizes[subId];
                if (!slot.IsOptional || ((valType != BinXmlValueType.Null) && (valSize > 0)))
                {
                    if (slot.Prefix != null)
                        vsb.Append(slot.Prefix);
                    WriteValue<TWriter>(valSize, valType, valueOffsets[subId], binxmlChunkBase, ref vsb, escaped);
                    if (slot.Suffix != null)
                        vsb.Append(slot.Suffix);
                }
            }

            vsb.Append(parts[i + 1]);
        }
    }

    #endregion

    #region Non-Public Fields

    /// <summary>
    /// True to end a corrupt BinXml stream and keep the record's well-formed prefix instead of throwing.
    /// </summary>
    private readonly bool _salvage;

    /// <summary>
    /// First corruption salvaged while rendering the current record, or null if it decoded cleanly.
    /// </summary>
    private string? _salvageDiagnostic;

    /// <summary>
    /// Maximum nesting depth for recursive element parsing to prevent stack overflow on crafted input.
    /// </summary>
    private const int _maxRecursionDepth = 64;

    /// <summary>
    /// Free space (16 KB) requested from the output sink before rendering a record into it. Covers typical rendered
    /// records (XML ~1.2-1.7 KB, JSON ~2.2-2.7 KB on the sample logs); larger records spill to ArrayPool and are copied.
    /// </summary>
    private const int _recordBufferBytes = 16384;

    /// <summary>
    /// Absolute byte offset of this chunk within <see cref="_fileData"/>.
    /// </summary>
    private readonly int _chunkFileOffset;

    /// <summary>
    /// Cache of templates compiled for this parser's output format, keyed by template GUID and shared across the chunks
    /// a worker renders, or null to decode every template body. A null entry marks a corrupt template;
    /// <see cref="CompiledTemplate.DecodeDirectly"/> a usable one that is decoded rather than compiled.
    /// </summary>
    private readonly Dictionary<Guid, CompiledTemplate?>? _compiledCache;

    /// <summary>
    /// Raw EVTX file bytes shared across all chunks and records.
    /// </summary>
    private readonly byte[] _fileData;

    /// <summary>
    /// Provider (WEVT) templates used as a fallback for missing or corrupt embedded templates, or null.
    /// </summary>
    private readonly WevtCache? _wevtCache;

    /// <summary>
    /// Exclusive end of this chunk within <see cref="_fileData"/>: <c>_chunkFileOffset + 65536</c>, clamped to the data
    /// length. Every template body and substitution value must lie before it. A chunk is self-contained, so bytes
    /// past its end belong to the next chunk; bounding reads here makes rendering from the whole file and from a
    /// standalone 64 KB chunk buffer identical, as in the Rust evtx crate, which parses each chunk from its own buffer.
    /// </summary>
    private readonly int _chunkEnd;

    /// <summary>
    /// Per-chunk cache of element/attribute names keyed by chunk-relative offset.
    /// Created on first use: the compiled-template path never resolves names, so warm chunks allocate nothing here.
    /// </summary>
    private Dictionary<uint, string>? _nameCache;

    /// <summary>
    /// Preloaded template definitions keyed by chunk-relative offset.
    /// Populated by following the chained hash table at chunk offset 384.
    /// </summary>
    private readonly Dictionary<uint, BinXmlTemplateDefinition> _templates;

    #endregion

    #region Nested Types

    /// <summary>
    /// Lightweight header from a TemplateInstance token (0x0C). Contains only the template
    /// identity and value count; substitution data is written into caller-provided spans
    /// by <see cref="ReadTemplateInstanceData"/> to avoid per-record heap allocations.
    /// </summary>
    /// <param name="TemplateGuid">Template GUID (compiled cache key).</param>
    /// <param name="DataSize">Template body size in bytes (after the 24-byte definition header).</param>
    /// <param name="DefDataOffset">Chunk-relative offset of the template definition.</param>
    /// <param name="NumValues">Number of substitution values in this instance.</param>
    private readonly record struct TemplateInstanceHeader(
        Guid TemplateGuid,
        uint DataSize,
        uint DefDataOffset,
        int NumValues);

    #endregion
}