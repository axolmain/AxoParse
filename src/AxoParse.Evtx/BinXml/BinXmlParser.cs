using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

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
    /// <param name="compiledCache">Cross-chunk cache of templates compiled for the output format this parser renders, keyed by
    /// GUID, or null to decode every template body. Never share a cache between formats.</param>
    /// <param name="wevtCache">Provider templates used when a record's embedded template is missing or corrupt, or null.</param>
    /// <param name="salvage">True to keep the well-formed prefix of a record whose BinXml is corrupt instead of failing it.</param>
    /// <param name="readsFields">True if typed fields are read: templates compiled into <paramref name="compiledCache"/>
    /// then keep their program, which their <see cref="CompiledTemplate.Fields"/> are built from. Otherwise the
    /// programs are dropped once compiled (keeping them grows every worker's live heap, which slows parallel parses).</param>
    public BinXmlParser(
        byte[] fileData,
        int chunkFileOffset,
        Dictionary<Guid, CompiledTemplate?>? compiledCache,
        WevtCache? wevtCache = null,
        bool salvage = false,
        bool readsFields = false)
    {
        _readsFields = readsFields;
        _salvage = salvage;
        _wevtCache = wevtCache;
        _fileData = fileData;
        _chunkFileOffset = chunkFileOffset;
        _chunkEnd = Math.Min(chunkFileOffset + EvtxChunk.ChunkSize, fileData.Length);
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
    /// <param name="lineFeed">True to follow non-empty output with a line feed (<c>WriteTo</c> framing).</param>
    internal void AppendRecordUtf8(EvtxRecord record, OutputFormat format, ArrayBufferWriter<byte> sink, bool lineFeed = false)
    {
        // Render straight into the sink's free space; only a record larger than it is copied from a pooled buffer
        Span<byte> free = sink.GetSpan(_recordBufferBytes);
        ValueUtf8Builder vub = new(free);
        RenderRecord(record, format, ref vub);
        if (lineFeed && (vub.Length > 0))
            vub.Append((byte)'\n');
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
        if (format == OutputFormat.Xml)
            DecodeTopLevel<XmlWriter>(eventData, ref pos, binxmlChunkBase, ref vub, recordLevel: true, escaped: false);
        else if (format == OutputFormat.JsonStructured)
            DecodeTopLevel<JsonWriter>(eventData, ref pos, binxmlChunkBase, ref vub, recordLevel: true, escaped: false);
        else
            RenderFlatJson(record, ref vub);
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
    /// Renders a TemplateInstance token (0x0C): 1 token + 1 unknown + 4 template ID + 4 definition offset
    /// [+ inline definition: 4 next-offset + 16 GUID + 4 data size + body] + 4 value count + 4-byte value descriptors
    /// (u16 size, u8 type, u8 padding) + the values back to back. The definition is resolved once per chunk
    /// (<see cref="ResolveTemplate{TWriter}"/>); the instance then writes its values through the compiled template, or
    /// decodes the template body with them.
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
        int p = pos + 6; // token + unknown + template ID
        uint defDataOffset = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(p, 4));
        p += 4;
        // The definition is inline when it starts right here, i.e. this is its first use in the chunk
        bool inline = defDataOffset == (uint)(binxmlChunkBase + p);
        ref ResolvedTemplate resolved = ref ResolveTemplate<TWriter>(defDataOffset, inline ? data[p..] : default);
        if (inline)
            p += 24 + (int)resolved.DataSize;

        int numVals = ReadSubstitutionCount(data, p);
        p += 4;

        // Sizes and types are read in place from the descriptors; only the values' offsets are computed.
        // stackalloc for the common case (≤ 64 substitutions); heap fallback for rare large templates
        const int StackThreshold = 64;
        Span<int> valueOffsets = numVals <= StackThreshold ? stackalloc int[StackThreshold] : new int[numVals];
        ReadOnlySpan<SubstitutionDescriptor> descriptors =
            MemoryMarshal.Cast<byte, SubstitutionDescriptor>(data.Slice(p, numVals * 4));
        p += numVals * 4;
        int fileOffset = _chunkFileOffset + binxmlChunkBase + p;
        for (int i = 0; i < descriptors.Length; i++)
        {
            valueOffsets[i] = fileOffset;
            fileOffset += descriptors[i].Size;
        }
        pos = fileOffset - _chunkFileOffset - binxmlChunkBase;

        SubstitutionValues values = new(valueOffsets[..numVals], descriptors);
        if (resolved.Compiled != null)
        {
            WriteCompiled<TWriter>(resolved.Compiled, values, binxmlChunkBase, ref vsb, escaped);
            return;
        }

        if (!resolved.DecodeBody)
            return;

        ReadOnlySpan<byte> tplBody = _fileData.AsSpan(_chunkFileOffset + (int)resolved.DefDataOffset + 24, (int)resolved.DataSize);
        int tplChunkBase = (int)resolved.DefDataOffset + 24;
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
    /// Resolves the template definition at <paramref name="defDataOffset"/> for this output format, memoised per chunk.
    /// The definition's GUID and body size come from the inline definition or the chunk bytes at that offset (the same
    /// bytes the chunk's template table describes, so the table itself isn't needed to render). A template whose body is in the chunk is compiled on first use (cached by GUID); one that is
    /// missing, runs past its chunk or is corrupt falls back to the provider template with the same GUID, as in the Rust
    /// evtx crate (a usable embedded template is never replaced); a usable one that isn't compiled is decoded directly.
    /// </summary>
    /// <typeparam name="TWriter">Output format.</typeparam>
    /// <param name="defDataOffset">Chunk-relative offset of the template definition.</param>
    /// <param name="inlineDefinition">The inline definition's bytes when the instance defines the template, else empty.</param>
    /// <returns>The memo entry for the definition.</returns>
    private ref ResolvedTemplate ResolveTemplate<TWriter>(uint defDataOffset, ReadOnlySpan<byte> inlineDefinition)
        where TWriter : struct, IBinXmlWriter
    {
        ref ResolvedTemplate entry = ref _resolvedTemplates[(int)((defDataOffset ^ (defDataOffset >> 7)) & (_resolvedTemplateSlots - 1))];
        if (entry.IsSet && (entry.DefDataOffset == defDataOffset) && (entry.Format == TWriter.Format))
            return ref entry;

        Guid templateGuid = default;
        uint dataSize = 0;
        if (!inlineDefinition.IsEmpty)
        {
            templateGuid = new Guid(inlineDefinition.Slice(4, 16));
            dataSize = BinaryPrimitives.ReadUInt32LittleEndian(inlineDefinition.Slice(20, 4));
        }
        else if (defDataOffset + 24 <= EvtxChunk.ChunkSize)
        {
            ReadOnlySpan<byte> chunkData = _fileData.AsSpan(_chunkFileOffset, EvtxChunk.ChunkSize);
            int baseOffset = (int)defDataOffset;
            templateGuid = new Guid(chunkData.Slice(baseOffset + 4, 16));
            dataSize = BinaryPrimitives.ReadUInt32LittleEndian(chunkData.Slice(baseOffset + 20, 4));
        }

        int tplBodyFileOffset = _chunkFileOffset + (int)defDataOffset + 24;
        bool embedded = (dataSize != 0) && ((uint)(tplBodyFileOffset + dataSize) <= (uint)_chunkEnd);

        CompiledTemplate? compiled = null;
        bool compileFailed = false;
        if (embedded && (_compiledCache != null))
        {
            if (!_compiledCache.TryGetValue(templateGuid, out compiled))
            {
                compiled = CompileProgram((int)defDataOffset, (int)dataSize) is { } program
                    ? Lower<TWriter>(program, _readsFields)
                    : null;
                _compiledCache[templateGuid] = compiled;
            }
            compileFailed = compiled == null;
        }

        if ((!embedded || compileFailed) && (_wevtCache?.Get(templateGuid, TWriter.Format) is { } provider))
            compiled = provider;

        CompiledTemplate? fieldSource = compiled;
        if (compiled is { DecodesBody: true })
            compiled = null;

        entry = new ResolvedTemplate
        {
            DefDataOffset = defDataOffset,
            DataSize = dataSize,
            IsSet = true,
            Format = TWriter.Format,
            DecodeBody = embedded && (compiled == null),
            Compiled = compiled,
            FieldSource = fieldSource
        };
        return ref entry;
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
    /// Writes a compiled template: its static parts interleaved with this instance's formatted substitution values.
    /// A slot whose value is missing (index past the instance's values) or optional and empty writes nothing,
    /// including its conditional markup.
    /// </summary>
    /// <typeparam name="TWriter">Output format the template was compiled for.</typeparam>
    /// <param name="compiled">Compiled template.</param>
    /// <param name="values">The instance's substitution values.</param>
    /// <param name="binxmlChunkBase">Chunk-relative base offset used for embedded BinXml resolution.</param>
    /// <param name="vsb">Output builder.</param>
    /// <param name="escaped">JSON only: write the escaped parts and escape values twice (embedded BinXml context).</param>
    private void WriteCompiled<TWriter>(CompiledTemplate compiled, scoped SubstitutionValues values, int binxmlChunkBase,
                                        ref ValueUtf8Builder vsb, bool escaped)
        where TWriter : struct, IBinXmlWriter
    {
        byte[][] parts = escaped ? compiled.EscapedParts : compiled.Parts;
        SubSlot[] slots = compiled.Slots;
        ReadOnlySpan<int> valueOffsets = values.Offsets;
        ReadOnlySpan<SubstitutionDescriptor> descriptors = values.Descriptors;
        int valCount = valueOffsets.Length;

        vsb.Append(parts[0]);
        for (int i = 0; i < slots.Length; i++)
        {
            ref readonly SubSlot slot = ref slots[i];
            int subId = slot.SubId;
            if (subId < valCount)
            {
                byte valType = descriptors[subId].Type;
                int valSize = descriptors[subId].Size;
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
    /// True if typed fields are read, so compiled templates keep their programs (see the constructor).
    /// </summary>
    private readonly bool _readsFields;

    /// <summary>
    /// First corruption salvaged while rendering the current record, or null if it decoded cleanly.
    /// </summary>
    private string? _salvageDiagnostic;


    /// <summary>
    /// Number of <see cref="ResolvedTemplateMemo"/> slots (a power of two).
    /// </summary>
    private const int _resolvedTemplateSlots = 16;

    /// <summary>
    /// Template definitions resolved so far in this chunk, by definition offset.
    /// </summary>
    private ResolvedTemplateMemo _resolvedTemplates;

    /// <summary>
    /// Calendar date of the last FILETIME this parser formatted: a chunk's records are in time order, so consecutive
    /// timestamps nearly always fall on the same day.
    /// </summary>
    private BinXmlValueFormatter.DateMemo _lastDate;

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
    /// one that <see cref="CompiledTemplate.DecodesBody"/> a usable one that is decoded rather than compiled.
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


    #endregion

    #region Nested Types

    /// <summary>
    /// A template definition resolved for this chunk and output format: what every template instance pointing at the
    /// definition renders through. Built on the first instance at <see cref="DefDataOffset"/> and reused after.
    /// </summary>
    private struct ResolvedTemplate
    {
        /// <summary>
        /// Chunk-relative offset of the template definition (4 next-offset + 16 GUID + 4 data size, then the body).
        /// </summary>
        public uint DefDataOffset;

        /// <summary>
        /// Template body size in bytes; an inline definition's body is skipped by this much.
        /// </summary>
        public uint DataSize;

        /// <summary>
        /// True once this entry holds a resolved definition.
        /// </summary>
        public bool IsSet;

        /// <summary>
        /// Output format <see cref="Compiled"/> was compiled for.
        /// </summary>
        public OutputFormat Format;

        /// <summary>
        /// True to decode the definition's body directly (it is usable but not compiled).
        /// </summary>
        public bool DecodeBody;

        /// <summary>
        /// The compiled template to write (the embedded one or a provider fallback), or null.
        /// </summary>
        public CompiledTemplate? Compiled;

        /// <summary>
        /// The template that renders (compiled or decoded directly), whose <see cref="CompiledTemplate.Fields"/> are the
        /// instance's fields, or null if it is unavailable or corrupt.
        /// </summary>
        public CompiledTemplate? FieldSource;
    }

    /// <summary>
    /// Direct-mapped memo of <see cref="ResolvedTemplate"/>s by definition offset. A chunk typically references a
    /// handful of templates, so instances almost always hit and skip both the definition lookup by offset and the
    /// compiled-template lookup by GUID.
    /// </summary>
    [InlineArray(_resolvedTemplateSlots)]
    private struct ResolvedTemplateMemo
    {
        /// <summary>
        /// First element of the inline array.
        /// </summary>
        private ResolvedTemplate _element;
    }

    #endregion
}