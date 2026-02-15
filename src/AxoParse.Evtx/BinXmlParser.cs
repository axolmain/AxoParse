using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace AxoParse.Evtx;

/// <summary>
/// Pre-compiled BinXmlTemplate: string parts interleaved with substitution slots.
/// parts[0] + subs[0] + parts[1] + subs[1] + ... + parts[N]
/// </summary>
internal sealed class CompiledTemplate(string[] parts, int[] subIds, bool[] isOptional)
{
    /// <summary>
    /// Static XML string fragments interleaved with substitution slots.
    /// parts[0] precedes the first substitution, parts[N] follows the last.
    /// </summary>
    public readonly string[] Parts = parts;

    /// <summary>
    /// Substitution slot indices corresponding to the gaps between <see cref="Parts"/>.
    /// </summary>
    public readonly int[] SubIds = subIds;

    /// <summary>
    /// Whether each substitution is optional (0x0E OptionalSubstitution vs 0x0D NormalSubstitution).
    /// Optional substitutions are skipped when the value is null or zero-length.
    /// </summary>
    public readonly bool[] IsOptional = isOptional;
}

/// <summary>
/// Core BinXml parser. One instance per chunk. Produces XML strings from BinXml token streams.
/// </summary>
internal sealed class BinXmlParser
{
    /// <summary>
    /// Maximum nesting depth for recursive element parsing to prevent stack overflow on crafted input.
    /// </summary>
    private const int MaxRecursionDepth = 64;

    /// <summary>
    /// Pre-computed lookup table mapping byte values 0x00..0xFF to two-character uppercase hex strings.
    /// </summary>
    private static readonly string[] HexLookup = InitHexLookup();

    /// <summary>
    /// Raw EVTX file bytes shared across all chunks and records.
    /// </summary>
    private readonly byte[] _fileData;

    /// <summary>
    /// Absolute byte offset of this chunk within <see cref="_fileData"/>.
    /// </summary>
    private readonly int _chunkFileOffset;

    /// <summary>
    /// Preloaded template definitions keyed by chunk-relative offset.
    /// Populated by following the chained hash table at chunk offset 384.
    /// </summary>
    private readonly Dictionary<uint, BinXmlTemplateDefinition> _templates;

    /// <summary>
    /// Process-wide cache of compiled templates keyed by template GUID.
    /// Shared across chunks to avoid recompiling identical templates.
    /// </summary>
    private readonly ConcurrentDictionary<Guid, CompiledTemplate?> _compiledCache;

    /// <summary>
    /// Per-chunk cache of element/attribute names keyed by chunk-relative offset.
    /// Pre-populated from the 64-entry common string table at chunk offset 128.
    /// </summary>
    private readonly Dictionary<uint, string> _nameCache;

    /// <summary>
    /// Builds a 256-entry lookup table for fast byte-to-hex conversion.
    /// </summary>
    /// <returns>Array where index <c>i</c> contains the uppercase two-character hex string for byte <c>i</c>.</returns>
    private static string[] InitHexLookup()
    {
        string[] table = new string[256];
        for (int i = 0; i < 256; i++)
            table[i] = i.ToString("X2");
        return table;
    }

    /// <summary>
    /// Initialises a parser scoped to a single 64 KB EVTX chunk.
    /// Pre-populates the name cache from the 64-entry common string offset table at chunk offset 128.
    /// </summary>
    /// <param name="fileData">Complete EVTX file bytes.</param>
    /// <param name="chunkFileOffset">Absolute byte offset of the chunk within <paramref name="fileData"/>.</param>
    /// <param name="templates">Preloaded template definitions for this chunk, keyed by chunk-relative offset.</param>
    /// <param name="compiledCache">Shared cross-chunk cache of compiled templates keyed by GUID.</param>
    public BinXmlParser(
        byte[] fileData,
        int chunkFileOffset,
        Dictionary<uint, BinXmlTemplateDefinition> templates,
        ConcurrentDictionary<Guid, CompiledTemplate?> compiledCache)
    {
        _fileData = fileData;
        _chunkFileOffset = chunkFileOffset;
        _templates = templates;
        _compiledCache = compiledCache;
        _nameCache = new Dictionary<uint, string>(64);

        // Pre-populate name cache from chunk common string offset table (64 uint32 entries at chunk offset 128)
        ReadOnlySpan<byte> chunkData = fileData.AsSpan(chunkFileOffset, EvtxChunk.ChunkSize);
        ReadOnlySpan<uint> commonOffsets = MemoryMarshal.Cast<byte, uint>(chunkData.Slice(128, 256));
        for (int i = 0; i < commonOffsets.Length; i++)
        {
            uint offset = commonOffsets[i];
            if (offset != 0 && offset + 8 < EvtxChunk.ChunkSize && !_nameCache.ContainsKey(offset))
            {
                _nameCache[offset] = ReadNameFromChunk(offset);
            }
        }
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
        ushort numChars = MemoryMarshal.Read<ushort>(chunkData.Slice(offset + 6));
        if (offset + 8 + numChars * 2 > chunkData.Length) return string.Empty;
        ReadOnlySpan<char> chars = MemoryMarshal.Cast<byte, char>(chunkData.Slice(offset + 8, numChars * 2));
        return new string(chars);
    }

    /// <summary>
    /// Resolves a name from the per-chunk cache, reading from chunk data on cache miss.
    /// </summary>
    /// <param name="chunkRelOffset">Chunk-relative byte offset of the name structure.</param>
    /// <returns>The cached or freshly-read name string.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private string ReadName(uint chunkRelOffset)
    {
        if (_nameCache.TryGetValue(chunkRelOffset, out string? cached))
            return cached;
        string name = ReadNameFromChunk(chunkRelOffset);
        _nameCache[chunkRelOffset] = name;
        return name;
    }

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

        ushort numChars = MemoryMarshal.Read<ushort>(data.Slice(pos + 6));
        int inlineNameBytes = 10 + numChars * 2;

        if (pos + inlineNameBytes > data.Length)
            return false;

        pos += inlineNameBytes;
        return true;
    }

    /// <summary>
    /// Parses a single record's BinXml event data into XML.
    /// </summary>
    /// <param name="record">The EVTX record whose BinXml event data will be parsed.</param>
    /// <returns>The rendered XML string for the record's event data.</returns>
    public string ParseRecord(EvtxRecord record)
    {
        ReadOnlySpan<byte> eventData = record.GetEventData(_fileData);
        int binxmlChunkBase = record.EventDataFileOffset - _chunkFileOffset;

        ValueStringBuilder vsb = new(stackalloc char[512]);
        int pos = 0;
        ParseDocument(eventData, ref pos, binxmlChunkBase, ref vsb);
        string result = vsb.ToString();
        vsb.Dispose();
        return result;
    }

    /// <summary>
    /// Parses a top-level BinXml document, consuming fragment headers, processing instructions,
    /// and template instances until EOF. Appends rendered XML to <paramref name="vsb"/>.
    /// </summary>
    /// <param name="data">BinXml byte stream.</param>
    /// <param name="pos">Current read position; advanced past all consumed tokens.</param>
    /// <param name="binxmlChunkBase">Chunk-relative base offset of <paramref name="data"/>.</param>
    /// <param name="vsb">String builder that receives the rendered XML output.</param>
    private void ParseDocument(ReadOnlySpan<byte> data, ref int pos, int binxmlChunkBase, ref ValueStringBuilder vsb)
    {
        while (pos < data.Length)
        {
            byte tok = data[pos];
            byte baseTok = (byte)(tok & ~BinXmlToken.HasMoreDataFlag);

            if (baseTok == BinXmlToken.Eof)
                break;

            if (baseTok == BinXmlToken.FragmentHeader)
            {
                ParseFragment(data, ref pos, binxmlChunkBase, ref vsb);
            }
            else if (baseTok == BinXmlToken.PiTarget)
            {
                pos++; // consume 0x0A
                uint piNameOff = MemoryMarshal.Read<uint>(data.Slice(pos));
                pos += 4;
                string piName = ReadName(piNameOff);
                vsb.Append("<?");
                vsb.Append(piName);

                if (pos < data.Length && data[pos] == BinXmlToken.PiData)
                {
                    pos++; // consume 0x0B
                    string piText = ReadUnicodeTextStringAsString(data, ref pos);
                    if (piText.Length > 0)
                    {
                        vsb.Append(' ');
                        vsb.Append(piText);
                    }
                }

                vsb.Append("?>");
            }
            else
            {
                break;
            }
        }
    }

    /// <summary>
    /// Parses a BinXml fragment: consumes the 4-byte fragment header (token + major + minor + flags)
    /// then dispatches to either a TemplateInstance or a bare element.
    /// A fragment may appear at the top level of a record or embedded via a BinXmlType (0x21)
    /// substitution value, which contains a nested BinXml-encoded XML fragment or TemplateInstance.
    /// The byte length of an embedded fragment includes up to and including its EOF token.
    /// </summary>
    /// <param name="data">The BinXml byte stream to parse.</param>
    /// <param name="pos">Current read position within <paramref name="data"/>; advanced past the fragment on return.</param>
    /// <param name="binxmlChunkBase">Chunk-relative offset of <paramref name="data"/>, used to resolve inline name structures.</param>
    /// <param name="vsb">String builder that receives the rendered XML output.</param>
    private void ParseFragment(ReadOnlySpan<byte> data, ref int pos, int binxmlChunkBase, ref ValueStringBuilder vsb)
    {
        if (pos + 4 > data.Length) return;
        pos += 4; // skip fragment header (token + major + minor + flags)

        if (pos >= data.Length) return;

        byte nextTok = data[pos];
        byte nextBase = (byte)(nextTok & ~BinXmlToken.HasMoreDataFlag);

        if (nextBase == BinXmlToken.TemplateInstance)
            ParseTemplateInstance(data, ref pos, binxmlChunkBase, ref vsb);
        else if (nextBase == BinXmlToken.OpenStartElement)
            ParseElement(data, ref pos, null, null, null, binxmlChunkBase, ref vsb);
    }

    /// <summary>
    /// Parses a TemplateInstance token (0x0C). Reads the template definition offset to determine
    /// inline vs back-reference, then reads substitution descriptors and value data.
    /// Uses the compiled template cache for fast rendering; falls back to full tree walk on cache miss.
    /// Layout: 1 token + 1 unknown + 4 unknown + 4 defDataOffset [+ 24-byte inline header + body] + 4 numValues + descriptors + values.
    /// </summary>
    /// <param name="data">BinXml byte stream.</param>
    /// <param name="pos">Current read position; advanced past the entire template instance on return.</param>
    /// <param name="binxmlChunkBase">Chunk-relative base offset of <paramref name="data"/>.</param>
    /// <param name="vsb">String builder that receives the rendered XML output.</param>
    private void ParseTemplateInstance(ReadOnlySpan<byte> data, ref int pos, int binxmlChunkBase,
                                       ref ValueStringBuilder vsb)
    {
        pos++; // consume 0x0C token
        pos++; // unknown1
        pos += 4; // unknown2
        uint defDataOffset = MemoryMarshal.Read<uint>(data.Slice(pos));
        pos += 4;

        // Determine inline vs back-reference
        uint currentChunkRelOffset = (uint)(binxmlChunkBase + pos);
        bool isInline = defDataOffset == currentChunkRelOffset;

        Guid templateGuid = default;
        uint dataSize = 0;

        if (isInline)
        {
            pos += 4; // next def offset
            templateGuid = MemoryMarshal.Read<Guid>(data.Slice(pos));
            pos += 16;
            dataSize = MemoryMarshal.Read<uint>(data.Slice(pos));
            pos += 4;
            pos += (int)dataSize; // skip template body (already preloaded)
        }
        else
        {
            // Back-reference: look up from preloaded templates
            if (_templates.TryGetValue(defDataOffset, out BinXmlTemplateDefinition def))
            {
                templateGuid = def.Guid;
                dataSize = def.DataSize;
            }
            else if (defDataOffset + 24 <= EvtxChunk.ChunkSize)
            {
                // Fallback: read directly from chunk
                ReadOnlySpan<byte> chunkData = _fileData.AsSpan(_chunkFileOffset, EvtxChunk.ChunkSize);
                templateGuid = MemoryMarshal.Read<Guid>(chunkData.Slice((int)defDataOffset + 4));
                dataSize = MemoryMarshal.Read<uint>(chunkData.Slice((int)defDataOffset + 20));
            }
        }

        // Read substitution descriptors and values
        uint numValues = MemoryMarshal.Read<uint>(data.Slice(pos));
        pos += 4;

        int descStart = pos;
        ReadOnlySpan<SubstitutionDescriptor> descriptors =
            MemoryMarshal.Cast<byte, SubstitutionDescriptor>(data.Slice(descStart, (int)numValues * 4));
        pos += (int)numValues * 4;

        // Use arrays for value metadata (avoids ref safety issues with stackalloc + ref struct)
        int numVals = (int)numValues;
        int[] valueOffsets = new int[numVals];
        int[] valueSizes = new int[numVals];
        byte[] valueTypes = new byte[numVals];

        for (int i = 0; i < numVals; i++)
        {
            valueOffsets[i] = pos;
            valueSizes[i] = descriptors[i].Size;
            valueTypes[i] = descriptors[i].Type;
            pos += descriptors[i].Size;
        }

        // Lookup/compile template
        if (dataSize == 0) return;

        int tplBodyFileOffset = _chunkFileOffset + (int)defDataOffset + 24;
        if (tplBodyFileOffset + (int)dataSize > _fileData.Length) return;

        // Check compiled cache (GetOrAdd may invoke factory concurrently for same key — harmless)
        CompiledTemplate? compiled = _compiledCache.GetOrAdd(templateGuid,
            _ => CompileTemplate((int)defDataOffset, (int)dataSize));

        if (compiled != null)
        {
            RenderCompiled(compiled, valueOffsets, valueSizes, valueTypes, binxmlChunkBase, ref vsb);
        }
        else
        {
            // Fallback: parse template body with substitutions
            ReadOnlySpan<byte> tplBody = _fileData.AsSpan(tplBodyFileOffset, (int)dataSize);
            int tplPos = 0;
            int tplChunkBase = (int)defDataOffset + 24;

            // Skip fragment header
            if (tplBody.Length >= 4 && tplBody[0] == BinXmlToken.FragmentHeader)
                tplPos += 4;

            ParseContent(tplBody, ref tplPos, valueOffsets, valueSizes, valueTypes, tplChunkBase, ref vsb);
        }
    }

    /// <summary>
    /// Parses an OpenStartElement token (0x01/0x41) and its children into XML.
    /// Token layout: 1 token + 2 depId + 4 dataSize + 4 nameOffset [+ inline name] [+ 4 attrListSize + attrs]
    /// followed by a close token (CloseEmpty 0x03, CloseStart 0x02, or EndElement 0x04).
    /// Bit 0x40 on the token indicates attributes are present.
    /// </summary>
    /// <param name="data">BinXml byte stream.</param>
    /// <param name="pos">Current read position; advanced past the entire element on return.</param>
    /// <param name="valueOffsets">File offsets of substitution values, or null if no template context.</param>
    /// <param name="valueSizes">Byte sizes of substitution values.</param>
    /// <param name="valueTypes">BinXml value type codes for each substitution.</param>
    /// <param name="binxmlChunkBase">Chunk-relative base offset of <paramref name="data"/>.</param>
    /// <param name="vsb">String builder that receives the rendered XML output.</param>
    /// <param name="depth">Current recursion depth for stack overflow protection.</param>
    private void ParseElement(ReadOnlySpan<byte> data, ref int pos,
                              int[]? valueOffsets, int[]? valueSizes, byte[]? valueTypes,
                              int binxmlChunkBase, ref ValueStringBuilder vsb, int depth = 0)
    {
        if (depth >= MaxRecursionDepth) return;

        byte tok = data[pos];
        bool hasAttrs = (tok & BinXmlToken.HasMoreDataFlag) != 0;
        pos++; // consume token

        pos += 2; // depId
        pos += 4; // dataSize
        uint nameOffset = MemoryMarshal.Read<uint>(data.Slice(pos));
        pos += 4;

        if (!TrySkipInlineName(data, ref pos, nameOffset, binxmlChunkBase)) return;

        string elemName = ReadName(nameOffset);
        vsb.Append('<');
        vsb.Append(elemName);

        // Parse attribute list if present
        if (hasAttrs)
        {
            uint attrListSize = MemoryMarshal.Read<uint>(data.Slice(pos));
            pos += 4;
            int attrEnd = pos + (int)attrListSize;

            while (pos < attrEnd)
            {
                byte attrTok = data[pos];
                byte attrBase = (byte)(attrTok & ~BinXmlToken.HasMoreDataFlag);
                if (attrBase != BinXmlToken.Attribute) break;

                pos++; // consume attribute token
                uint attrNameOff = MemoryMarshal.Read<uint>(data.Slice(pos));
                pos += 4;

                if (!TrySkipInlineName(data, ref pos, attrNameOff, binxmlChunkBase)) break;

                string attrName = ReadName(attrNameOff);
                vsb.Append(' ');
                vsb.Append(attrName);
                vsb.Append("=\"");
                ParseContent(data, ref pos, valueOffsets, valueSizes, valueTypes, binxmlChunkBase, ref vsb, depth + 1);
                vsb.Append('"');
            }
        }

        // Close token
        if (pos >= data.Length)
        {
            vsb.Append("/>");
            return;
        }

        byte closeTok = data[pos];
        if (closeTok == BinXmlToken.CloseEmptyElement)
        {
            pos++;
            vsb.Append("/>");
        }
        else if (closeTok == BinXmlToken.CloseStartElement)
        {
            pos++;
            vsb.Append('>');
            ParseContent(data, ref pos, valueOffsets, valueSizes, valueTypes, binxmlChunkBase, ref vsb, depth + 1);
            if (pos < data.Length && data[pos] == BinXmlToken.EndElement)
                pos++;
            vsb.Append("</");
            vsb.Append(elemName);
            vsb.Append('>');
        }
        else
        {
            vsb.Append("/>");
        }
    }

    /// <summary>
    /// Parses a sequence of BinXml content tokens (child elements, text values, substitutions,
    /// character/entity references, CDATA sections) until a break token is encountered.
    /// Break tokens: EOF (0x00), CloseStartElement (0x02), CloseEmptyElement (0x03),
    /// EndElement (0x04), Attribute (0x06).
    /// </summary>
    /// <param name="data">BinXml byte stream.</param>
    /// <param name="pos">Current read position; advanced past all consumed content tokens.</param>
    /// <param name="valueOffsets">File offsets of substitution values, or null if no template context.</param>
    /// <param name="valueSizes">Byte sizes of substitution values.</param>
    /// <param name="valueTypes">BinXml value type codes for each substitution.</param>
    /// <param name="binxmlChunkBase">Chunk-relative base offset of <paramref name="data"/>.</param>
    /// <param name="vsb">String builder that receives the rendered XML output.</param>
    /// <param name="depth">Current recursion depth for stack overflow protection.</param>
    private void ParseContent(ReadOnlySpan<byte> data, ref int pos,
                              int[]? valueOffsets, int[]? valueSizes, byte[]? valueTypes,
                              int binxmlChunkBase, ref ValueStringBuilder vsb, int depth = 0)
    {
        while (pos < data.Length)
        {
            byte tok = data[pos];
            byte baseTok = (byte)(tok & ~BinXmlToken.HasMoreDataFlag);

            // Break tokens
            if (baseTok == BinXmlToken.Eof ||
                baseTok == BinXmlToken.CloseStartElement ||
                baseTok == BinXmlToken.CloseEmptyElement ||
                baseTok == BinXmlToken.EndElement ||
                baseTok == BinXmlToken.Attribute)
                break;

            switch (baseTok)
            {
                case BinXmlToken.OpenStartElement:
                    ParseElement(data, ref pos, valueOffsets, valueSizes, valueTypes, binxmlChunkBase, ref vsb, depth + 1);
                    break;
                case BinXmlToken.Value:
                {
                    pos++; // consume token
                    pos++; // value type
                    string str = ReadUnicodeTextStringAsString(data, ref pos);
                    AppendXmlEscaped(ref vsb, str.AsSpan());
                    break;
                }
                case BinXmlToken.NormalSubstitution:
                {
                    pos++; // consume token
                    ushort subId = MemoryMarshal.Read<ushort>(data.Slice(pos));
                    pos += 2;
                    pos++; // subValType
                    if (valueOffsets != null && subId < valueOffsets.Length)
                    {
                        RenderValue(valueSizes![subId], valueTypes![subId], valueOffsets[subId], binxmlChunkBase,
                            ref vsb);
                    }

                    break;
                }
                case BinXmlToken.OptionalSubstitution:
                {
                    pos++; // consume token
                    ushort subId = MemoryMarshal.Read<ushort>(data.Slice(pos));
                    pos += 2;
                    pos++; // subValType
                    if (valueOffsets != null && subId < valueOffsets.Length)
                    {
                        byte valType = valueTypes![subId];
                        int valSize = valueSizes![subId];
                        if (valType != BinXmlValueType.Null && valSize > 0)
                        {
                            RenderValue(valSize, valType, valueOffsets[subId], binxmlChunkBase, ref vsb);
                        }
                    }

                    break;
                }
                case BinXmlToken.CharRef:
                {
                    pos++; // consume token
                    ushort charVal = MemoryMarshal.Read<ushort>(data.Slice(pos));
                    pos += 2;
                    vsb.Append("&#");
                    vsb.AppendFormatted(charVal);
                    vsb.Append(';');
                    break;
                }
                case BinXmlToken.EntityRef:
                {
                    pos++; // consume token
                    uint nameOff = MemoryMarshal.Read<uint>(data.Slice(pos));
                    pos += 4;
                    string entityName = ReadName(nameOff);
                    vsb.Append('&');
                    vsb.Append(entityName);
                    vsb.Append(';');
                    break;
                }
                case BinXmlToken.CDataSection:
                {
                    pos++; // consume token
                    string cdataStr = ReadUnicodeTextStringAsString(data, ref pos);
                    vsb.Append("<![CDATA[");
                    vsb.Append(cdataStr);
                    vsb.Append("]]>");
                    break;
                }
                case BinXmlToken.TemplateInstance:
                    ParseTemplateInstance(data, ref pos, binxmlChunkBase, ref vsb);
                    break;
                case BinXmlToken.FragmentHeader:
                    ParseFragment(data, ref pos, binxmlChunkBase, ref vsb);
                    break;
                default:
                    pos++;
                    break;
            }
        }
    }

    // ---- Template compilation ----

    /// <summary>
    /// Compiles a template body into a <see cref="CompiledTemplate"/> of interleaved string parts
    /// and substitution slot IDs. Returns null if the template contains nested templates or
    /// other constructs that prevent static compilation.
    /// </summary>
    /// <param name="defDataOffset">Chunk-relative offset of the template definition (before the 24-byte header).</param>
    /// <param name="dataSize">Size in bytes of the template body (after the 24-byte header).</param>
    /// <returns>A compiled template, or null if the template cannot be statically compiled.</returns>
    private CompiledTemplate? CompileTemplate(int defDataOffset, int dataSize)
    {
        int tplBodyFileOffset = _chunkFileOffset + defDataOffset + 24;
        if (tplBodyFileOffset + dataSize > _fileData.Length) return null;

        ReadOnlySpan<byte> tplBody = _fileData.AsSpan(tplBodyFileOffset, dataSize);
        int tplChunkBase = defDataOffset + 24;

        List<string> parts = new() { string.Empty };
        List<int> subIds = new();
        List<bool> isOptional = new();
        bool bail = false;

        int pos = 0;
        // Skip fragment header
        if (tplBody.Length >= 4 && tplBody[0] == BinXmlToken.FragmentHeader)
            pos += 4;

        CompileContent(tplBody, ref pos, tplChunkBase, parts, subIds, isOptional, ref bail);

        if (bail) return null;
        return new CompiledTemplate(parts.ToArray(), subIds.ToArray(), isOptional.ToArray());
    }

    /// <summary>
    /// Walks BinXml content tokens, appending static XML text to the last entry in <paramref name="parts"/>
    /// and recording substitution slots. Sets <paramref name="bail"/> to true if a nested template
    /// or unsupported token is encountered (compilation cannot proceed).
    /// </summary>
    /// <param name="data">BinXml byte stream (template body).</param>
    /// <param name="pos">Current read position; advanced past consumed tokens.</param>
    /// <param name="binxmlChunkBase">Chunk-relative base offset of <paramref name="data"/>.</param>
    /// <param name="parts">Accumulator for static XML string fragments.</param>
    /// <param name="subIds">Accumulator for substitution slot indices.</param>
    /// <param name="isOptional">Accumulator for whether each substitution is optional.</param>
    /// <param name="bail">Set to true if compilation must abort.</param>
    /// <param name="depth">Current recursion depth for stack overflow protection.</param>
    private void CompileContent(ReadOnlySpan<byte> data, ref int pos, int binxmlChunkBase,
                                List<string> parts, List<int> subIds, List<bool> isOptional, ref bool bail, int depth = 0)
    {
        while (pos < data.Length)
        {
            if (bail) return;
            byte tok = data[pos];
            byte baseTok = (byte)(tok & ~BinXmlToken.HasMoreDataFlag);

            if (baseTok == BinXmlToken.Eof ||
                baseTok == BinXmlToken.CloseStartElement ||
                baseTok == BinXmlToken.CloseEmptyElement ||
                baseTok == BinXmlToken.EndElement ||
                baseTok == BinXmlToken.Attribute)
                break;

            if (baseTok == BinXmlToken.OpenStartElement)
            {
                CompileElement(data, ref pos, binxmlChunkBase, parts, subIds, isOptional, ref bail, depth + 1);
            }
            else if (baseTok == BinXmlToken.Value)
            {
                pos++; // token
                pos++; // value type
                string str = ReadUnicodeTextStringAsString(data, ref pos);
                parts[^1] += XmlEscapeString(str);
            }
            else if (baseTok == BinXmlToken.NormalSubstitution)
            {
                pos++;
                ushort subId = MemoryMarshal.Read<ushort>(data.Slice(pos));
                pos += 2;
                pos++; // subValType
                subIds.Add(subId);
                isOptional.Add(false);
                parts.Add(string.Empty);
            }
            else if (baseTok == BinXmlToken.OptionalSubstitution)
            {
                pos++;
                ushort subId = MemoryMarshal.Read<ushort>(data.Slice(pos));
                pos += 2;
                pos++; // subValType
                subIds.Add(subId);
                isOptional.Add(true);
                parts.Add(string.Empty);
            }
            else if (baseTok == BinXmlToken.CharRef)
            {
                pos++;
                ushort charVal = MemoryMarshal.Read<ushort>(data.Slice(pos));
                pos += 2;
                parts[^1] += $"&#{charVal};";
            }
            else if (baseTok == BinXmlToken.EntityRef)
            {
                pos++;
                uint nameOff = MemoryMarshal.Read<uint>(data.Slice(pos));
                pos += 4;
                string entityName = ReadName(nameOff);
                parts[^1] += $"&{entityName};";
            }
            else if (baseTok == BinXmlToken.CDataSection)
            {
                pos++;
                string cdataStr = ReadUnicodeTextStringAsString(data, ref pos);
                parts[^1] += $"<![CDATA[{cdataStr}]]>";
            }
            else if (baseTok == BinXmlToken.TemplateInstance || baseTok == BinXmlToken.FragmentHeader)
            {
                bail = true;
                return;
            }
            else
            {
                bail = true;
                return;
            }
        }
    }

    /// <summary>
    /// Compiles a single OpenStartElement and its children into static XML fragments.
    /// Appends opening/closing tags and attribute markup to <paramref name="parts"/>,
    /// recording substitution slots encountered in attributes and child content.
    /// </summary>
    /// <param name="data">BinXml byte stream (template body).</param>
    /// <param name="pos">Current read position; advanced past the entire element.</param>
    /// <param name="binxmlChunkBase">Chunk-relative base offset of <paramref name="data"/>.</param>
    /// <param name="parts">Accumulator for static XML string fragments.</param>
    /// <param name="subIds">Accumulator for substitution slot indices.</param>
    /// <param name="isOptional">Accumulator for whether each substitution is optional.</param>
    /// <param name="bail">Set to true if compilation must abort.</param>
    /// <param name="depth">Current recursion depth for stack overflow protection.</param>
    private void CompileElement(ReadOnlySpan<byte> data, ref int pos, int binxmlChunkBase,
                                List<string> parts, List<int> subIds, List<bool> isOptional, ref bool bail, int depth = 0)
    {
        if (depth >= MaxRecursionDepth)
        {
            bail = true;
            return;
        }

        byte tok = data[pos];
        bool hasAttrs = (tok & BinXmlToken.HasMoreDataFlag) != 0;
        pos++;

        pos += 2; // depId
        pos += 4; // dataSize
        uint nameOffset = MemoryMarshal.Read<uint>(data.Slice(pos));
        pos += 4;

        if (!TrySkipInlineName(data, ref pos, nameOffset, binxmlChunkBase))
        {
            bail = true;
            return;
        }

        string elemName = ReadName(nameOffset);
        parts[^1] += $"<{elemName}";

        if (hasAttrs)
        {
            uint attrListSize = MemoryMarshal.Read<uint>(data.Slice(pos));
            pos += 4;
            int attrEnd = pos + (int)attrListSize;

            while (pos < attrEnd)
            {
                if (bail) return;
                byte attrTok = data[pos];
                byte attrBase = (byte)(attrTok & ~BinXmlToken.HasMoreDataFlag);
                if (attrBase != BinXmlToken.Attribute) break;

                pos++;
                uint attrNameOff = MemoryMarshal.Read<uint>(data.Slice(pos));
                pos += 4;
                if (!TrySkipInlineName(data, ref pos, attrNameOff, binxmlChunkBase))
                {
                    bail = true;
                    return;
                }

                string attrName = ReadName(attrNameOff);
                parts[^1] += $" {attrName}=\"";
                CompileContent(data, ref pos, binxmlChunkBase, parts, subIds, isOptional, ref bail, depth + 1);
                if (bail) return;
                parts[^1] += "\"";
            }
        }

        if (pos >= data.Length)
        {
            parts[^1] += "/>";
            return;
        }

        byte closeTok = data[pos];
        if (closeTok == BinXmlToken.CloseEmptyElement)
        {
            pos++;
            parts[^1] += "/>";
        }
        else if (closeTok == BinXmlToken.CloseStartElement)
        {
            pos++;
            parts[^1] += ">";
            CompileContent(data, ref pos, binxmlChunkBase, parts, subIds, isOptional, ref bail, depth + 1);
            if (bail) return;
            if (pos < data.Length && data[pos] == BinXmlToken.EndElement)
                pos++;
            parts[^1] += $"</{elemName}>";
        }
        else
        {
            parts[^1] += "/>";
        }
    }

    // ---- Compiled template rendering ----

    /// <summary>
    /// Renders a compiled template by interleaving its static XML parts with rendered substitution values.
    /// </summary>
    /// <param name="compiled">Pre-compiled template containing static parts and substitution metadata.</param>
    /// <param name="valueOffsets">File offsets of each substitution value.</param>
    /// <param name="valueSizes">Byte sizes of each substitution value.</param>
    /// <param name="valueTypes">BinXml value type codes for each substitution.</param>
    /// <param name="binxmlChunkBase">Chunk-relative base offset used for embedded BinXml resolution.</param>
    /// <param name="vsb">String builder that receives the rendered XML output.</param>
    private void RenderCompiled(CompiledTemplate compiled,
                                int[] valueOffsets, int[] valueSizes, byte[] valueTypes,
                                int binxmlChunkBase, ref ValueStringBuilder vsb)
    {
        vsb.Append(compiled.Parts[0]);
        for (int i = 0; i < compiled.SubIds.Length; i++)
        {
            int subId = compiled.SubIds[i];
            if (subId < valueOffsets.Length)
            {
                byte valType = valueTypes[subId];
                int valSize = valueSizes[subId];
                if (!compiled.IsOptional[i] || (valType != BinXmlValueType.Null && valSize > 0))
                {
                    RenderValue(valSize, valType, valueOffsets[subId], binxmlChunkBase, ref vsb);
                }
            }

            vsb.Append(compiled.Parts[i + 1]);
        }
    }

    /// <summary>
    /// Renders a single BinXml substitution value as XML text.
    /// Dispatches on value type to produce the appropriate string representation
    /// (numeric, GUID, SID, FILETIME, hex, embedded BinXml, etc.).
    /// </summary>
    /// <param name="size">Byte size of the value data.</param>
    /// <param name="valueType">BinXml value type code (see <see cref="BinXmlValueType"/>). Bit 0x80 indicates an array.</param>
    /// <param name="fileOffset">Absolute byte offset of the value data within <see cref="_fileData"/>.</param>
    /// <param name="binxmlChunkBase">Chunk-relative base offset used for embedded BinXml (type 0x21) resolution.</param>
    /// <param name="vsb">String builder that receives the rendered text.</param>
    private void RenderValue(int size, byte valueType, int fileOffset, int binxmlChunkBase, ref ValueStringBuilder vsb)
    {
        if (size == 0) return;
        ReadOnlySpan<byte> valueBytes = _fileData.AsSpan(fileOffset, size);

        // Array flag
        if ((valueType & BinXmlValueType.ArrayFlag) != 0)
        {
            RenderArray(valueBytes, (byte)(valueType & 0x7F), fileOffset, binxmlChunkBase, ref vsb);
            return;
        }

        switch (valueType)
        {
            case BinXmlValueType.Null:
                break;

            case BinXmlValueType.String:
            {
                ReadOnlySpan<char> chars = MemoryMarshal.Cast<byte, char>(valueBytes);
                // Trim trailing null
                if (chars.Length > 0 && chars[^1] == '\0')
                    chars = chars[..^1];
                AppendXmlEscaped(ref vsb, chars);
                break;
            }

            case BinXmlValueType.AnsiString:
            {
                for (int i = 0; i < valueBytes.Length; i++)
                {
                    byte b = valueBytes[i];
                    if (b == 0) break;
                    if (b == '&') vsb.Append("&amp;");
                    else if (b == '<') vsb.Append("&lt;");
                    else if (b == '>') vsb.Append("&gt;");
                    else if (b == '"') vsb.Append("&quot;");
                    else if (b == '\'') vsb.Append("&apos;");
                    else vsb.Append((char)b);
                }

                break;
            }

            case BinXmlValueType.Int8:
                vsb.AppendFormatted((sbyte)valueBytes[0]);
                break;

            case BinXmlValueType.UInt8:
                vsb.AppendFormatted(valueBytes[0]);
                break;

            case BinXmlValueType.Int16:
                vsb.AppendFormatted(MemoryMarshal.Read<short>(valueBytes));
                break;

            case BinXmlValueType.UInt16:
                vsb.AppendFormatted(MemoryMarshal.Read<ushort>(valueBytes));
                break;

            case BinXmlValueType.Int32:
                vsb.AppendFormatted(MemoryMarshal.Read<int>(valueBytes));
                break;

            case BinXmlValueType.UInt32:
                vsb.AppendFormatted(MemoryMarshal.Read<uint>(valueBytes));
                break;

            case BinXmlValueType.Int64:
                vsb.AppendFormatted(MemoryMarshal.Read<long>(valueBytes));
                break;

            case BinXmlValueType.UInt64:
                vsb.AppendFormatted(MemoryMarshal.Read<ulong>(valueBytes));
                break;

            case BinXmlValueType.Float:
                vsb.AppendFormatted(MemoryMarshal.Read<float>(valueBytes));
                break;

            case BinXmlValueType.Double:
                vsb.AppendFormatted(MemoryMarshal.Read<double>(valueBytes));
                break;

            case BinXmlValueType.Bool:
                vsb.Append(MemoryMarshal.Read<uint>(valueBytes) != 0 ? "true" : "false");
                break;

            case BinXmlValueType.Binary:
                AppendHex(ref vsb, valueBytes);
                break;

            case BinXmlValueType.Guid:
            {
                if (size < 16) break;
                RenderGuid(valueBytes, ref vsb);
                break;
            }

            case BinXmlValueType.SizeT:
            {
                vsb.Append("0x");
                if (size == 8)
                {
                    ulong val = MemoryMarshal.Read<ulong>(valueBytes);
                    vsb.AppendFormatted(val, "x16");
                }
                else
                {
                    uint val = MemoryMarshal.Read<uint>(valueBytes);
                    vsb.AppendFormatted(val, "x8");
                }

                break;
            }

            case BinXmlValueType.FileTime:
            {
                if (size < 8) break;
                long ticks = MemoryMarshal.Read<long>(valueBytes);
                if (ticks == 0) break;
                const long FileTimeEpochDelta = 504911232000000000L;
                DateTime dt = new DateTime(ticks + FileTimeEpochDelta, DateTimeKind.Utc);
                vsb.AppendFormatted(dt, "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'");
                break;
            }

            case BinXmlValueType.SystemTime:
            {
                if (size < 16) break;
                ushort yr = MemoryMarshal.Read<ushort>(valueBytes);
                ushort mo = MemoryMarshal.Read<ushort>(valueBytes.Slice(2));
                ushort dy = MemoryMarshal.Read<ushort>(valueBytes.Slice(6));
                ushort hr = MemoryMarshal.Read<ushort>(valueBytes.Slice(8));
                ushort mn = MemoryMarshal.Read<ushort>(valueBytes.Slice(10));
                ushort sc = MemoryMarshal.Read<ushort>(valueBytes.Slice(12));
                ushort ms = MemoryMarshal.Read<ushort>(valueBytes.Slice(14));
                vsb.AppendFormatted(yr, "D4");
                vsb.Append('-');
                vsb.AppendFormatted(mo, "D2");
                vsb.Append('-');
                vsb.AppendFormatted(dy, "D2");
                vsb.Append('T');
                vsb.AppendFormatted(hr, "D2");
                vsb.Append(':');
                vsb.AppendFormatted(mn, "D2");
                vsb.Append(':');
                vsb.AppendFormatted(sc, "D2");
                vsb.Append('.');
                vsb.AppendFormatted(ms, "D3");
                vsb.Append('Z');
                break;
            }

            case BinXmlValueType.Sid:
            {
                if (size < 8) break;
                byte revision = valueBytes[0];
                byte subCount = valueBytes[1];
                long authority = 0;
                for (int i = 2; i < 8; i++)
                    authority = authority * 256 + valueBytes[i];
                vsb.Append("S-");
                vsb.AppendFormatted(revision);
                vsb.Append('-');
                vsb.AppendFormatted(authority);
                for (int i = 0; i < subCount; i++)
                {
                    int subOff = 8 + i * 4;
                    if (subOff + 4 > size) break;
                    vsb.Append('-');
                    vsb.AppendFormatted(MemoryMarshal.Read<uint>(valueBytes.Slice(subOff)));
                }

                break;
            }

            case BinXmlValueType.HexInt32:
                vsb.Append("0x");
                vsb.AppendFormatted(MemoryMarshal.Read<uint>(valueBytes), "x8");
                break;

            case BinXmlValueType.HexInt64:
                vsb.Append("0x");
                vsb.AppendFormatted(MemoryMarshal.Read<ulong>(valueBytes), "x16");
                break;

            case BinXmlValueType.BinXml:
            {
                int embeddedChunkBase = fileOffset - _chunkFileOffset;
                int embeddedPos = 0;
                ParseDocument(valueBytes, ref embeddedPos, embeddedChunkBase, ref vsb);
                break;
            }

            case BinXmlValueType.EvtHandle:
            case BinXmlValueType.EvtXml:
            default:
                AppendHex(ref vsb, valueBytes);
                break;
        }
    }

    /// <summary>
    /// Renders an array-typed value (type code has bit 0x80 set) as comma-separated XML text.
    /// String arrays (base type 0x01) are null-terminated UTF-16LE concatenated;
    /// fixed-size types are rendered by splitting on element size.
    /// </summary>
    /// <param name="valueBytes">Raw value bytes containing the array data.</param>
    /// <param name="baseType">Base BinXml value type (with array flag 0x80 masked off).</param>
    /// <param name="fileOffset">Absolute byte offset of the value data within <see cref="_fileData"/>.</param>
    /// <param name="binxmlChunkBase">Chunk-relative base offset for nested value rendering.</param>
    /// <param name="vsb">String builder that receives the comma-separated rendered elements.</param>
    private void RenderArray(ReadOnlySpan<byte> valueBytes, byte baseType, int fileOffset,
                             int binxmlChunkBase, ref ValueStringBuilder vsb)
    {
        // String arrays: null-terminated UTF-16LE strings concatenated
        if (baseType == BinXmlValueType.String)
        {
            ReadOnlySpan<char> chars = MemoryMarshal.Cast<byte, char>(valueBytes);
            bool first = true;
            int start = 0;
            for (int i = 0; i <= chars.Length; i++)
            {
                if (i == chars.Length || chars[i] == '\0')
                {
                    if (i > start)
                    {
                        if (!first) vsb.Append(", ");
                        AppendXmlEscaped(ref vsb, chars.Slice(start, i - start));
                        first = false;
                    }

                    start = i + 1;
                }
            }

            return;
        }

        // Fixed-size array types
        int elemSize = GetElementSize(baseType);
        if (elemSize > 0 && valueBytes.Length >= elemSize)
        {
            bool first = true;
            for (int i = 0; i + elemSize <= valueBytes.Length; i += elemSize)
            {
                if (!first) vsb.Append(", ");
                RenderValue(elemSize, baseType, fileOffset + i, binxmlChunkBase, ref vsb);
                first = false;
            }

            return;
        }

        // Fallback: hex
        AppendHex(ref vsb, valueBytes);
    }

    /// <summary>
    /// Returns the fixed byte size of a BinXml value type for array element splitting.
    /// Returns 0 for variable-length or unknown types.
    /// </summary>
    /// <param name="baseType">Base BinXml value type code (without the 0x80 array flag).</param>
    /// <returns>Fixed element size in bytes, or 0 if the type has no fixed size.</returns>
    private static int GetElementSize(byte baseType)
    {
        return baseType switch
        {
            BinXmlValueType.Int8 or BinXmlValueType.UInt8 => 1,
            BinXmlValueType.Int16 or BinXmlValueType.UInt16 => 2,
            BinXmlValueType.Int32 or BinXmlValueType.UInt32 or BinXmlValueType.Float or BinXmlValueType.Bool
                or BinXmlValueType.HexInt32 => 4,
            BinXmlValueType.Int64 or BinXmlValueType.UInt64 or BinXmlValueType.Double or BinXmlValueType.FileTime
                or BinXmlValueType.HexInt64 => 8,
            BinXmlValueType.Guid or BinXmlValueType.SystemTime => 16,
            _ => 0
        };
    }

    /// <summary>
    /// Renders a 16-byte GUID in braced lowercase hex format: {xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx}.
    /// First three components (Data1/Data2/Data3) are little-endian; last 8 bytes are big-endian.
    /// </summary>
    /// <param name="b">16-byte span containing the raw GUID.</param>
    /// <param name="vsb">String builder that receives the formatted GUID.</param>
    private void RenderGuid(ReadOnlySpan<byte> b, ref ValueStringBuilder vsb)
    {
        uint d1 = MemoryMarshal.Read<uint>(b);
        ushort d2 = MemoryMarshal.Read<ushort>(b.Slice(4));
        ushort d3 = MemoryMarshal.Read<ushort>(b.Slice(6));

        vsb.Append('{');
        vsb.AppendFormatted(d1, "x8");
        vsb.Append('-');
        vsb.AppendFormatted(d2, "x4");
        vsb.Append('-');
        vsb.AppendFormatted(d3, "x4");
        vsb.Append('-');
        vsb.Append(HexLookup[b[8]]);
        vsb.Append(HexLookup[b[9]]);
        vsb.Append(HexLookup[b[10]]);
        vsb.Append(HexLookup[b[11]]);
        vsb.Append('-');
        vsb.Append(HexLookup[b[12]]);
        vsb.Append(HexLookup[b[13]]);
        vsb.Append(HexLookup[b[14]]);
        vsb.Append(HexLookup[b[15]]);
        vsb.Append('}');
    }

    // ---- Helpers ----

    /// <summary>
    /// Reads a length-prefixed UTF-16LE string: 2-byte character count followed by numChars * 2 bytes.
    /// </summary>
    /// <param name="data">BinXml byte stream.</param>
    /// <param name="pos">Current read position; advanced past the 2-byte length prefix and string bytes.</param>
    /// <returns>The decoded string.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static string ReadUnicodeTextStringAsString(ReadOnlySpan<byte> data, ref int pos)
    {
        ushort numChars = MemoryMarshal.Read<ushort>(data.Slice(pos));
        pos += 2;
        ReadOnlySpan<char> chars = MemoryMarshal.Cast<byte, char>(data.Slice(pos, numChars * 2));
        pos += numChars * 2;
        return new string(chars);
    }

    /// <summary>
    /// Appends text to the string builder with XML entity escaping (&amp;, &lt;, &gt;, &quot;, &apos;).
    /// Uses a fast path for text containing no special characters. Replaces unpaired surrogates with U+FFFD.
    /// </summary>
    /// <param name="vsb">String builder that receives the escaped text.</param>
    /// <param name="text">Source character span to escape and append.</param>
    private static void AppendXmlEscaped(ref ValueStringBuilder vsb, scoped ReadOnlySpan<char> text)
    {
        // Fast path: no XML-special chars and no surrogates → bulk append
        if (text.IndexOfAny('&', '<', '>') < 0 &&
            text.IndexOfAny('"', '\'') < 0 &&
            text.IndexOfAnyInRange('\uD800', '\uDFFF') < 0)
        {
            vsb.Append(text);
            return;
        }

        // Slow path: XML-escape + replace unpaired surrogates with U+FFFD
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (char.IsHighSurrogate(c))
            {
                if (i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                {
                    vsb.Append(c);
                    vsb.Append(text[++i]);
                }
                else
                {
                    vsb.Append('\uFFFD');
                }
            }
            else if (char.IsLowSurrogate(c))
            {
                vsb.Append('\uFFFD');
            }
            else
            {
                switch (c)
                {
                    case '&': vsb.Append("&amp;"); break;
                    case '<': vsb.Append("&lt;"); break;
                    case '>': vsb.Append("&gt;"); break;
                    case '"': vsb.Append("&quot;"); break;
                    case '\'': vsb.Append("&apos;"); break;
                    default: vsb.Append(c); break;
                }
            }
        }
    }

    /// <summary>
    /// Returns an XML-escaped copy of <paramref name="str"/>. Used during template compilation
    /// where a heap string is needed rather than span-based appending.
    /// </summary>
    /// <param name="str">The string to escape.</param>
    /// <returns>The escaped string, or the original string if no escaping was needed.</returns>
    internal static string XmlEscapeString(string str)
    {
        if (str.AsSpan().IndexOfAny('&', '<', '>') < 0 && str.AsSpan().IndexOfAny('"', '\'') < 0)
            return str;
        return str.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
            .Replace("\"", "&quot;").Replace("'", "&apos;");
    }

    /// <summary>
    /// Appends each byte as a two-character uppercase hex string using the precomputed <see cref="HexLookup"/> table.
    /// </summary>
    /// <param name="vsb">String builder that receives the hex output.</param>
    /// <param name="data">Bytes to convert.</param>
    private static void AppendHex(ref ValueStringBuilder vsb, ReadOnlySpan<byte> data)
    {
        for (int i = 0; i < data.Length; i++)
            vsb.Append(HexLookup[data[i]]);
    }

    // ==== JSON rendering ====

    /// <summary>
    /// Shared JSON writer options with validation skipped for performance.
    /// </summary>
    private static readonly JsonWriterOptions JsonOpts = new() { SkipValidation = true };

    /// <summary>
    /// Parses a single record's BinXml event data into UTF-8 JSON bytes.
    /// </summary>
    public byte[] ParseRecordJson(EvtxRecord record)
    {
        ReadOnlySpan<byte> eventData = record.GetEventData(_fileData);
        int binxmlChunkBase = record.EventDataFileOffset - _chunkFileOffset;

        ArrayBufferWriter<byte> buffer = new(512);
        using Utf8JsonWriter w = new(buffer, JsonOpts);
        int pos = 0;
        ParseDocumentJson(eventData, ref pos, binxmlChunkBase, w);
        w.Flush();
        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>
    /// JSON variant of <see cref="ParseDocument"/>. Consumes fragment headers and skips
    /// processing instructions (no JSON equivalent), then dispatches to fragment parsing.
    /// </summary>
    /// <param name="data">BinXml byte stream.</param>
    /// <param name="pos">Current read position; advanced past consumed tokens.</param>
    /// <param name="binxmlChunkBase">Chunk-relative base offset of <paramref name="data"/>.</param>
    /// <param name="w">UTF-8 JSON writer that receives the output.</param>
    private void ParseDocumentJson(ReadOnlySpan<byte> data, ref int pos, int binxmlChunkBase, Utf8JsonWriter w)
    {
        while (pos < data.Length)
        {
            byte tok = data[pos];
            byte baseTok = (byte)(tok & ~BinXmlToken.HasMoreDataFlag);

            if (baseTok == BinXmlToken.Eof)
                break;

            if (baseTok == BinXmlToken.FragmentHeader)
            {
                ParseFragmentJson(data, ref pos, binxmlChunkBase, w);
            }
            else if (baseTok == BinXmlToken.PiTarget)
            {
                // Skip PI for JSON — no equivalent
                pos++;
                pos += 4; // nameOffset
                if (pos < data.Length && data[pos] == BinXmlToken.PiData)
                {
                    pos++;
                    ushort numChars = MemoryMarshal.Read<ushort>(data.Slice(pos));
                    pos += 2 + numChars * 2;
                }
            }
            else
            {
                break;
            }
        }
    }

    /// <summary>
    /// JSON variant of <see cref="ParseFragment"/>. Skips the 4-byte fragment header, then
    /// dispatches to template instance or bare element JSON rendering.
    /// </summary>
    /// <param name="data">BinXml byte stream.</param>
    /// <param name="pos">Current read position; advanced past the fragment.</param>
    /// <param name="binxmlChunkBase">Chunk-relative base offset of <paramref name="data"/>.</param>
    /// <param name="w">UTF-8 JSON writer that receives the output.</param>
    private void ParseFragmentJson(ReadOnlySpan<byte> data, ref int pos, int binxmlChunkBase, Utf8JsonWriter w)
    {
        if (pos + 4 > data.Length) return;
        pos += 4; // fragment header

        if (pos >= data.Length) return;

        byte nextTok = data[pos];
        byte nextBase = (byte)(nextTok & ~BinXmlToken.HasMoreDataFlag);

        if (nextBase == BinXmlToken.TemplateInstance)
            ParseTemplateInstanceJson(data, ref pos, binxmlChunkBase, w);
        else if (nextBase == BinXmlToken.OpenStartElement)
            RenderElementJson(data, ref pos, null, null, null, binxmlChunkBase, w);
    }

    /// <summary>
    /// JSON variant of <see cref="ParseTemplateInstance"/>. Reads the template definition,
    /// substitution descriptors and values, then walks the template body producing JSON output.
    /// Unlike the XML path, this always does a full tree walk (no compiled template shortcut).
    /// </summary>
    /// <param name="data">BinXml byte stream.</param>
    /// <param name="pos">Current read position; advanced past the entire template instance.</param>
    /// <param name="binxmlChunkBase">Chunk-relative base offset of <paramref name="data"/>.</param>
    /// <param name="w">UTF-8 JSON writer that receives the output.</param>
    private void ParseTemplateInstanceJson(ReadOnlySpan<byte> data, ref int pos, int binxmlChunkBase, Utf8JsonWriter w)
    {
        pos++; // 0x0C token
        pos++; // unknown1
        pos += 4; // unknown2
        uint defDataOffset = MemoryMarshal.Read<uint>(data.Slice(pos));
        pos += 4;

        uint currentChunkRelOffset = (uint)(binxmlChunkBase + pos);
        bool isInline = defDataOffset == currentChunkRelOffset;

        uint dataSize = 0;

        if (isInline)
        {
            pos += 4; // next def offset
            pos += 16; // guid
            dataSize = MemoryMarshal.Read<uint>(data.Slice(pos));
            pos += 4;
            pos += (int)dataSize;
        }
        else
        {
            if (_templates.TryGetValue(defDataOffset, out BinXmlTemplateDefinition def))
                dataSize = def.DataSize;
            else if (defDataOffset + 24 <= EvtxChunk.ChunkSize)
            {
                ReadOnlySpan<byte> chunkData = _fileData.AsSpan(_chunkFileOffset, EvtxChunk.ChunkSize);
                dataSize = MemoryMarshal.Read<uint>(chunkData.Slice((int)defDataOffset + 20));
            }
        }

        // Read substitution descriptors
        uint numValues = MemoryMarshal.Read<uint>(data.Slice(pos));
        pos += 4;

        int descStart = pos;
        ReadOnlySpan<SubstitutionDescriptor> descriptors =
            MemoryMarshal.Cast<byte, SubstitutionDescriptor>(data.Slice(descStart, (int)numValues * 4));
        pos += (int)numValues * 4;

        int numVals = (int)numValues;
        int[] valueOffsets = new int[numVals];
        int[] valueSizes = new int[numVals];
        byte[] valueTypes = new byte[numVals];

        for (int i = 0; i < numVals; i++)
        {
            valueOffsets[i] = pos;
            valueSizes[i] = descriptors[i].Size;
            valueTypes[i] = descriptors[i].Type;
            pos += descriptors[i].Size;
        }

        if (dataSize == 0) return;

        int tplBodyFileOffset = _chunkFileOffset + (int)defDataOffset + 24;
        if (tplBodyFileOffset + (int)dataSize > _fileData.Length) return;

        ReadOnlySpan<byte> tplBody = _fileData.AsSpan(tplBodyFileOffset, (int)dataSize);
        int tplPos = 0;
        int tplChunkBase = (int)defDataOffset + 24;

        if (tplBody.Length >= 4 && tplBody[0] == BinXmlToken.FragmentHeader)
            tplPos += 4;

        ParseContentJson(tplBody, ref tplPos, valueOffsets, valueSizes, valueTypes, tplChunkBase, w);
    }

    /// <summary>
    /// JSON variant of <see cref="ParseContent"/>. Walks content tokens and writes JSON values
    /// for text, substitutions, character/entity references, and CDATA. Dispatches child elements
    /// and nested templates to their respective JSON renderers.
    /// </summary>
    /// <param name="data">BinXml byte stream.</param>
    /// <param name="pos">Current read position; advanced past consumed content tokens.</param>
    /// <param name="valueOffsets">File offsets of substitution values, or null if no template context.</param>
    /// <param name="valueSizes">Byte sizes of substitution values.</param>
    /// <param name="valueTypes">BinXml value type codes for each substitution.</param>
    /// <param name="binxmlChunkBase">Chunk-relative base offset of <paramref name="data"/>.</param>
    /// <param name="w">UTF-8 JSON writer that receives the output.</param>
    /// <param name="depth">Current recursion depth for stack overflow protection.</param>
    private void ParseContentJson(ReadOnlySpan<byte> data, ref int pos,
                                  int[]? valueOffsets, int[]? valueSizes, byte[]? valueTypes,
                                  int binxmlChunkBase, Utf8JsonWriter w, int depth = 0)
    {
        while (pos < data.Length)
        {
            byte tok = data[pos];
            byte baseTok = (byte)(tok & ~BinXmlToken.HasMoreDataFlag);

            if (baseTok == BinXmlToken.Eof ||
                baseTok == BinXmlToken.CloseStartElement ||
                baseTok == BinXmlToken.CloseEmptyElement ||
                baseTok == BinXmlToken.EndElement ||
                baseTok == BinXmlToken.Attribute)
                break;

            switch (baseTok)
            {
                case BinXmlToken.OpenStartElement:
                    RenderElementJson(data, ref pos, valueOffsets, valueSizes, valueTypes, binxmlChunkBase, w, depth + 1);
                    break;
                case BinXmlToken.Value:
                {
                    pos++;
                    pos++; // value type
                    string str = ReadUnicodeTextStringAsString(data, ref pos);
                    w.WriteStringValue(str);
                    break;
                }
                case BinXmlToken.NormalSubstitution:
                {
                    pos++;
                    ushort subId = MemoryMarshal.Read<ushort>(data.Slice(pos));
                    pos += 2;
                    pos++; // subValType
                    if (valueOffsets != null && subId < valueOffsets.Length)
                        RenderValueJson(valueSizes![subId], valueTypes![subId], valueOffsets[subId], binxmlChunkBase,
                            w);
                    break;
                }
                case BinXmlToken.OptionalSubstitution:
                {
                    pos++;
                    ushort subId = MemoryMarshal.Read<ushort>(data.Slice(pos));
                    pos += 2;
                    pos++; // subValType
                    if (valueOffsets != null && subId < valueOffsets.Length)
                    {
                        byte valType = valueTypes![subId];
                        int valSize = valueSizes![subId];
                        if (valType != BinXmlValueType.Null && valSize > 0)
                            RenderValueJson(valSize, valType, valueOffsets[subId], binxmlChunkBase, w);
                    }

                    break;
                }
                case BinXmlToken.CharRef:
                {
                    pos++;
                    ushort charVal = MemoryMarshal.Read<ushort>(data.Slice(pos));
                    pos += 2;
                    w.WriteStringValue(char.ConvertFromUtf32(charVal));
                    break;
                }
                case BinXmlToken.EntityRef:
                {
                    pos++;
                    uint nameOff = MemoryMarshal.Read<uint>(data.Slice(pos));
                    pos += 4;
                    string entityName = ReadName(nameOff);
                    // Resolve common XML entities
                    string resolved = entityName switch
                    {
                        "amp" => "&",
                        "lt" => "<",
                        "gt" => ">",
                        "quot" => "\"",
                        "apos" => "'",
                        _ => $"&{entityName};"
                    };
                    w.WriteStringValue(resolved);
                    break;
                }
                case BinXmlToken.CDataSection:
                {
                    pos++;
                    string cdataStr = ReadUnicodeTextStringAsString(data, ref pos);
                    w.WriteStringValue(cdataStr);
                    break;
                }
                case BinXmlToken.TemplateInstance:
                    ParseTemplateInstanceJson(data, ref pos, binxmlChunkBase, w);
                    break;
                case BinXmlToken.FragmentHeader:
                    ParseFragmentJson(data, ref pos, binxmlChunkBase, w);
                    break;
                default:
                    pos++;
                    break;
            }
        }
    }

    /// <summary>
    /// Classification result for an element's children — determines JSON representation.
    /// </summary>
    private readonly struct ElementClassification
    {
        /// <summary>
        /// True if the element contains at least one child element or embedded BinXml substitution.
        /// </summary>
        public readonly bool HasChildElements;

        /// <summary>
        /// True if the element contains at least one text value, substitution, char/entity ref, or CDATA.
        /// </summary>
        public readonly bool HasText;

        /// <summary>
        /// True if the element has no child elements and no text content.
        /// </summary>
        public readonly bool IsEmpty;

        /// <summary>
        /// Initialises a new classification result.
        /// </summary>
        /// <param name="hasChildElements">Whether child elements are present.</param>
        /// <param name="hasText">Whether text content is present.</param>
        /// <param name="isEmpty">Whether the element is empty.</param>
        public ElementClassification(bool hasChildElements, bool hasText, bool isEmpty)
        {
            HasChildElements = hasChildElements;
            HasText = hasText;
            IsEmpty = isEmpty;
        }
    }

    /// <summary>
    /// Pre-scans element children to classify without consuming position.
    /// </summary>
    /// <param name="data">BinXml byte stream.</param>
    /// <param name="pos">Read position to scan from (not modified — value copy).</param>
    /// <param name="valueOffsets">File offsets of substitution values, or null.</param>
    /// <param name="valueSizes">Byte sizes of substitution values.</param>
    /// <param name="valueTypes">BinXml value type codes for each substitution.</param>
    /// <param name="binxmlChunkBase">Chunk-relative base offset of <paramref name="data"/>.</param>
    /// <param name="depth">Current recursion depth for stack overflow protection.</param>
    /// <returns>Classification indicating whether children contain elements, text, both, or neither.</returns>
    private ElementClassification ClassifyChildren(ReadOnlySpan<byte> data, int pos,
                                                   int[]? valueOffsets, int[]? valueSizes, byte[]? valueTypes, int binxmlChunkBase, int depth = 0)
    {
        bool hasChildElements = false;
        bool hasText = false;

        while (pos < data.Length)
        {
            byte tok = data[pos];
            byte baseTok = (byte)(tok & ~BinXmlToken.HasMoreDataFlag);

            if (baseTok == BinXmlToken.Eof ||
                baseTok == BinXmlToken.EndElement ||
                baseTok == BinXmlToken.CloseStartElement ||
                baseTok == BinXmlToken.CloseEmptyElement ||
                baseTok == BinXmlToken.Attribute)
                break;

            if (baseTok == BinXmlToken.OpenStartElement)
            {
                hasChildElements = true;
                // Skip past this element to continue scanning
                SkipElement(data, ref pos, binxmlChunkBase, depth + 1);
            }
            else if (baseTok == BinXmlToken.Value)
            {
                hasText = true;
                pos++;
                pos++; // value type
                ushort numChars = MemoryMarshal.Read<ushort>(data.Slice(pos));
                pos += 2 + numChars * 2;
            }
            else if (baseTok == BinXmlToken.NormalSubstitution)
            {
                pos++;
                ushort subId = MemoryMarshal.Read<ushort>(data.Slice(pos));
                pos += 2;
                byte subValType = data[pos];
                pos++;
                if (valueOffsets != null && subId < valueOffsets.Length)
                {
                    byte vt = valueTypes![subId];
                    if (vt == BinXmlValueType.BinXml)
                        hasChildElements = true;
                    else if (valueSizes![subId] > 0)
                        hasText = true;
                }
            }
            else if (baseTok == BinXmlToken.OptionalSubstitution)
            {
                pos++;
                ushort subId = MemoryMarshal.Read<ushort>(data.Slice(pos));
                pos += 2;
                pos++; // subValType
                if (valueOffsets != null && subId < valueOffsets.Length)
                {
                    byte vt = valueTypes![subId];
                    int vs = valueSizes![subId];
                    if (vt != BinXmlValueType.Null && vs > 0)
                    {
                        if (vt == BinXmlValueType.BinXml)
                            hasChildElements = true;
                        else
                            hasText = true;
                    }
                }
            }
            else if (baseTok == BinXmlToken.CharRef)
            {
                hasText = true;
                pos += 3;
            }
            else if (baseTok == BinXmlToken.EntityRef)
            {
                hasText = true;
                pos += 5;
            }
            else if (baseTok == BinXmlToken.CDataSection)
            {
                hasText = true;
                pos++;
                ushort numChars = MemoryMarshal.Read<ushort>(data.Slice(pos));
                pos += 2 + numChars * 2;
            }
            else if (baseTok == BinXmlToken.TemplateInstance)
            {
                hasChildElements = true;
                break; // can't easily skip templates
            }
            else
            {
                pos++;
            }
        }

        bool isEmpty = !hasChildElements && !hasText;
        return new ElementClassification(hasChildElements, hasText, isEmpty);
    }

    /// <summary>
    /// Skips an element without producing output (for classification pre-scanning).
    /// </summary>
    /// <param name="data">BinXml byte stream.</param>
    /// <param name="pos">Current read position; advanced past the entire element.</param>
    /// <param name="binxmlChunkBase">Chunk-relative base offset of <paramref name="data"/>.</param>
    /// <param name="depth">Current recursion depth for stack overflow protection.</param>
    private void SkipElement(ReadOnlySpan<byte> data, ref int pos, int binxmlChunkBase, int depth = 0)
    {
        if (depth >= MaxRecursionDepth) return;

        byte tok = data[pos];
        bool hasAttrs = (tok & BinXmlToken.HasMoreDataFlag) != 0;
        pos++;
        pos += 2; // depId
        uint elemDataSize = MemoryMarshal.Read<uint>(data.Slice(pos));
        pos += 4;
        uint nameOffset = MemoryMarshal.Read<uint>(data.Slice(pos));
        pos += 4;

        if (!TrySkipInlineName(data, ref pos, nameOffset, binxmlChunkBase)) return;

        if (hasAttrs)
        {
            uint attrListSize = MemoryMarshal.Read<uint>(data.Slice(pos));
            pos += 4 + (int)attrListSize;
        }

        if (pos >= data.Length) return;
        byte closeTok = data[pos];
        if (closeTok == BinXmlToken.CloseEmptyElement)
        {
            pos++;
        }
        else if (closeTok == BinXmlToken.CloseStartElement)
        {
            pos++;
            // Skip content until EndElement
            SkipContent(data, ref pos, binxmlChunkBase, depth + 1);
            if (pos < data.Length && data[pos] == BinXmlToken.EndElement)
                pos++;
        }
        else
        {
            pos++;
        }
    }

    /// <summary>
    /// Skips content tokens without producing output, advancing <paramref name="pos"/> past
    /// child elements, values, substitutions, char/entity refs, and CDATA sections until a break token.
    /// </summary>
    /// <param name="data">BinXml byte stream.</param>
    /// <param name="pos">Current read position; advanced past all skipped content.</param>
    /// <param name="binxmlChunkBase">Chunk-relative base offset of <paramref name="data"/>.</param>
    /// <param name="depth">Current recursion depth for stack overflow protection.</param>
    private void SkipContent(ReadOnlySpan<byte> data, ref int pos, int binxmlChunkBase, int depth = 0)
    {
        while (pos < data.Length)
        {
            byte tok = data[pos];
            byte baseTok = (byte)(tok & ~BinXmlToken.HasMoreDataFlag);

            if (baseTok == BinXmlToken.Eof ||
                baseTok == BinXmlToken.CloseStartElement ||
                baseTok == BinXmlToken.CloseEmptyElement ||
                baseTok == BinXmlToken.EndElement ||
                baseTok == BinXmlToken.Attribute)
                break;

            if (baseTok == BinXmlToken.OpenStartElement)
            {
                SkipElement(data, ref pos, binxmlChunkBase, depth + 1);
            }
            else if (baseTok == BinXmlToken.Value)
            {
                pos++;
                pos++;
                ushort numChars = MemoryMarshal.Read<ushort>(data.Slice(pos));
                pos += 2 + numChars * 2;
            }
            else if (baseTok == BinXmlToken.NormalSubstitution || baseTok == BinXmlToken.OptionalSubstitution)
            {
                pos += 4; // token + subId(2) + valType(1)
            }
            else if (baseTok == BinXmlToken.CharRef)
            {
                pos += 3;
            }
            else if (baseTok == BinXmlToken.EntityRef)
            {
                pos += 5;
            }
            else if (baseTok == BinXmlToken.CDataSection)
            {
                pos++;
                ushort numChars = MemoryMarshal.Read<ushort>(data.Slice(pos));
                pos += 2 + numChars * 2;
            }
            else
            {
                pos++;
            }
        }
    }

    /// <summary>
    /// Renders a single element as JSON. Core method for JSON output.
    /// Classifies children to decide between null, scalar value, or nested object representation.
    /// Attributes are emitted under a "#attributes" property when present.
    /// </summary>
    /// <param name="data">BinXml byte stream.</param>
    /// <param name="pos">Current read position; advanced past the entire element.</param>
    /// <param name="valueOffsets">File offsets of substitution values, or null.</param>
    /// <param name="valueSizes">Byte sizes of substitution values.</param>
    /// <param name="valueTypes">BinXml value type codes for each substitution.</param>
    /// <param name="binxmlChunkBase">Chunk-relative base offset of <paramref name="data"/>.</param>
    /// <param name="w">UTF-8 JSON writer that receives the output.</param>
    /// <param name="depth">Current recursion depth for stack overflow protection.</param>
    private void RenderElementJson(ReadOnlySpan<byte> data, ref int pos,
                                   int[]? valueOffsets, int[]? valueSizes, byte[]? valueTypes,
                                   int binxmlChunkBase, Utf8JsonWriter w, int depth = 0)
    {
        if (depth >= MaxRecursionDepth)
        {
            w.WriteNullValue();
            return;
        }

        byte tok = data[pos];
        bool hasAttrs = (tok & BinXmlToken.HasMoreDataFlag) != 0;
        pos++;

        pos += 2; // depId
        pos += 4; // dataSize
        uint nameOffset = MemoryMarshal.Read<uint>(data.Slice(pos));
        pos += 4;

        if (!TrySkipInlineName(data, ref pos, nameOffset, binxmlChunkBase)) return;

        string elemName = ReadName(nameOffset);

        // Collect attributes
        List<(string name, string value)>? attrs = null;
        if (hasAttrs)
        {
            uint attrListSize = MemoryMarshal.Read<uint>(data.Slice(pos));
            pos += 4;
            int attrEnd = pos + (int)attrListSize;

            attrs = new List<(string, string)>();
            while (pos < attrEnd)
            {
                byte attrTok = data[pos];
                byte attrBase = (byte)(attrTok & ~BinXmlToken.HasMoreDataFlag);
                if (attrBase != BinXmlToken.Attribute) break;

                pos++;
                uint attrNameOff = MemoryMarshal.Read<uint>(data.Slice(pos));
                pos += 4;
                if (!TrySkipInlineName(data, ref pos, attrNameOff, binxmlChunkBase)) break;

                string attrName = ReadName(attrNameOff);
                string attrValue = RenderContentToString(data, ref pos, valueOffsets, valueSizes, valueTypes,
                    binxmlChunkBase);
                attrs.Add((attrName, attrValue));
            }
        }

        // Close token
        if (pos >= data.Length)
        {
            // Self-closing, no content
            w.WriteNullValue();
            return;
        }

        byte closeTok = data[pos];
        if (closeTok == BinXmlToken.CloseEmptyElement)
        {
            pos++;
            // Check if all attrs are empty — if so, null; else write object
            bool allAttrsEmpty = true;
            if (attrs != null)
            {
                for (int index = 0; index < attrs.Count; index++)
                {
                    var (_, v) = attrs[index];
                    if (v.Length > 0)
                    {
                        allAttrsEmpty = false;
                        break;
                    }
                }
            }

            if (attrs == null || allAttrsEmpty)
            {
                w.WriteNullValue();
            }
            else
            {
                w.WriteStartObject();
                w.WritePropertyName("#attributes");
                w.WriteStartObject();
                for (int index = 0; index < attrs.Count; index++)
                {
                    var (n, v) = attrs[index];
                    w.WriteString(n, v);
                }
                w.WriteEndObject();
                w.WriteEndObject();
            }

            return;
        }

        if (closeTok != BinXmlToken.CloseStartElement)
        {
            w.WriteNullValue();
            return;
        }

        pos++; // consume CloseStartElement

        // Classify children
        ElementClassification cls = ClassifyChildren(data, pos, valueOffsets, valueSizes, valueTypes, binxmlChunkBase, depth + 1);

        bool hasNonEmptyAttrs = false;
        if (attrs != null)
        {
            for (int index = 0; index < attrs.Count; index++)
            {
                var (_, v) = attrs[index];
                if (v.Length > 0)
                {
                    hasNonEmptyAttrs = true;
                    break;
                }
            }
        }

        // Check for EventData/UserData flattening
        bool isDataContainer = elemName == "EventData" || elemName == "UserData";

        if (cls.IsEmpty && !hasNonEmptyAttrs)
        {
            // Empty element — null
            SkipToEndElement(data, ref pos);
            w.WriteNullValue();
        }
        else if (!cls.HasChildElements && !hasNonEmptyAttrs)
        {
            // Scalar element — direct value
            RenderTextContentJson(data, ref pos, valueOffsets, valueSizes, valueTypes, binxmlChunkBase, w);
            if (pos < data.Length && data[pos] == BinXmlToken.EndElement)
                pos++;
        }
        else
        {
            // Object element
            w.WriteStartObject();

            if (hasNonEmptyAttrs)
            {
                w.WritePropertyName("#attributes");
                w.WriteStartObject();
                for (int index = 0; index < attrs!.Count; index++)
                {
                    var (n, v) = attrs![index];
                    w.WriteString(n, v);
                }
                w.WriteEndObject();
            }

            if (cls.HasText && cls.HasChildElements)
            {
                // Mixed content — capture text as #text
                string textVal =
                    RenderContentToString(data, ref pos, valueOffsets, valueSizes, valueTypes, binxmlChunkBase);
                if (textVal.Length > 0)
                    w.WriteString("#text", textVal);
            }
            else
            {
                // Render child elements as properties
                RenderChildElementsJson(data, ref pos, valueOffsets, valueSizes, valueTypes, binxmlChunkBase, w,
                    isDataContainer, depth + 1);
            }

            if (pos < data.Length && data[pos] == BinXmlToken.EndElement)
                pos++;

            w.WriteEndObject();
        }
    }

    /// <summary>
    /// Renders text-only content as a JSON value (string or typed primitive).
    /// When a single typed substitution is the only content, renders it as a native JSON type
    /// (number, boolean, etc.) rather than a string for schema fidelity.
    /// </summary>
    /// <param name="data">BinXml byte stream.</param>
    /// <param name="pos">Current read position; advanced past consumed content tokens.</param>
    /// <param name="valueOffsets">File offsets of substitution values, or null.</param>
    /// <param name="valueSizes">Byte sizes of substitution values.</param>
    /// <param name="valueTypes">BinXml value type codes for each substitution.</param>
    /// <param name="binxmlChunkBase">Chunk-relative base offset of <paramref name="data"/>.</param>
    /// <param name="w">UTF-8 JSON writer that receives the output.</param>
    private void RenderTextContentJson(ReadOnlySpan<byte> data, ref int pos,
                                       int[]? valueOffsets, int[]? valueSizes, byte[]? valueTypes,
                                       int binxmlChunkBase, Utf8JsonWriter w)
    {
        // Check if there's a single substitution — render as typed value
        int savedPos = pos;
        int subCount = 0;
        int firstSubId = -1;
        bool hasOtherContent = false;

        while (savedPos < data.Length)
        {
            byte tok = data[savedPos];
            byte baseTok = (byte)(tok & ~BinXmlToken.HasMoreDataFlag);

            if (baseTok == BinXmlToken.Eof || baseTok == BinXmlToken.EndElement ||
                baseTok == BinXmlToken.Attribute) break;

            if (baseTok == BinXmlToken.NormalSubstitution || baseTok == BinXmlToken.OptionalSubstitution)
            {
                savedPos++;
                ushort subId = MemoryMarshal.Read<ushort>(data.Slice(savedPos));
                savedPos += 2;
                savedPos++; // valType
                if (subCount == 0) firstSubId = subId;
                subCount++;
            }
            else if (baseTok == BinXmlToken.Value || baseTok == BinXmlToken.CharRef ||
                     baseTok == BinXmlToken.EntityRef || baseTok == BinXmlToken.CDataSection)
            {
                hasOtherContent = true;
                break;
            }
            else break;
        }

        if (subCount == 1 && !hasOtherContent && valueOffsets != null && firstSubId >= 0 &&
            firstSubId < valueOffsets.Length)
        {
            // Single typed substitution — render as JSON primitive
            byte valType = valueTypes![firstSubId];
            int valSize = valueSizes![firstSubId];

            // Consume the substitution token
            pos++; // token
            pos += 2; // subId
            pos++; // valType

            if (valType == BinXmlValueType.Null || valSize == 0)
            {
                w.WriteNullValue();
                return;
            }

            RenderValueJson(valSize, valType, valueOffsets[firstSubId], binxmlChunkBase, w);
            return;
        }

        // Multiple subs or mixed content — render as concatenated string
        string text = RenderContentToString(data, ref pos, valueOffsets, valueSizes, valueTypes, binxmlChunkBase);
        w.WriteStringValue(text);
    }

    /// <summary>
    /// Renders child elements as JSON properties. Handles duplicate name suffixing and EventData/UserData flattening.
    /// For EventData/UserData containers, Data elements with a Name= attribute are flattened to
    /// direct key-value pairs (e.g., &lt;Data Name="Foo"&gt;bar&lt;/Data&gt; becomes "Foo": "bar").
    /// Duplicate element names receive _N suffixes (e.g., "Data_1", "Data_2").
    /// </summary>
    /// <param name="data">BinXml byte stream.</param>
    /// <param name="pos">Current read position; advanced past consumed child content.</param>
    /// <param name="valueOffsets">File offsets of substitution values, or null.</param>
    /// <param name="valueSizes">Byte sizes of substitution values.</param>
    /// <param name="valueTypes">BinXml value type codes for each substitution.</param>
    /// <param name="binxmlChunkBase">Chunk-relative base offset of <paramref name="data"/>.</param>
    /// <param name="w">UTF-8 JSON writer that receives the output.</param>
    /// <param name="isDataContainer">True if the parent element is EventData or UserData, enabling Name= flattening.</param>
    /// <param name="depth">Current recursion depth for stack overflow protection.</param>
    private void RenderChildElementsJson(ReadOnlySpan<byte> data, ref int pos,
                                         int[]? valueOffsets, int[]? valueSizes, byte[]? valueTypes,
                                         int binxmlChunkBase, Utf8JsonWriter w, bool isDataContainer, int depth = 0)
    {
        Dictionary<string, int>? nameCounts = null;

        while (pos < data.Length)
        {
            byte tok = data[pos];
            byte baseTok = (byte)(tok & ~BinXmlToken.HasMoreDataFlag);

            if (baseTok == BinXmlToken.Eof || baseTok == BinXmlToken.EndElement ||
                baseTok == BinXmlToken.Attribute) break;

            if (baseTok == BinXmlToken.OpenStartElement)
            {
                // Peek at element name without consuming
                int peekPos = pos;
                peekPos++; // token
                peekPos += 2; // depId
                peekPos += 4; // dataSize
                uint nameOff = MemoryMarshal.Read<uint>(data.Slice(peekPos));

                string childName = ReadName(nameOff);

                // For EventData/UserData container: check for Name= attribute
                if (isDataContainer && childName == "Data")
                {
                    string? namedKey = PeekDataNameAttribute(data, pos, binxmlChunkBase, valueOffsets, valueSizes,
                        valueTypes);
                    if (namedKey != null)
                    {
                        // Named data: <Data Name="Foo">bar</Data> → "Foo": "bar"
                        w.WritePropertyName(namedKey);
                        RenderDataElementValueJson(data, ref pos, valueOffsets, valueSizes, valueTypes, binxmlChunkBase,
                            w);
                        continue;
                    }
                }

                // Handle duplicate names with _N suffixing
                nameCounts ??= new Dictionary<string, int>();
                if (nameCounts.TryGetValue(childName, out int count))
                {
                    nameCounts[childName] = count + 1;
                    w.WritePropertyName($"{childName}_{count}");
                }
                else
                {
                    nameCounts[childName] = 1;
                    w.WritePropertyName(childName);
                }

                RenderElementJson(data, ref pos, valueOffsets, valueSizes, valueTypes, binxmlChunkBase, w, depth + 1);
            }
            else if (baseTok == BinXmlToken.Value)
            {
                pos++;
                pos++; // value type
                string str = ReadUnicodeTextStringAsString(data, ref pos);
                if (str.Length > 0)
                    w.WriteString("#text", str);
            }
            else if (baseTok == BinXmlToken.NormalSubstitution || baseTok == BinXmlToken.OptionalSubstitution)
            {
                pos++;
                ushort subId = MemoryMarshal.Read<ushort>(data.Slice(pos));
                pos += 2;
                pos++; // subValType
                if (valueOffsets != null && subId < valueOffsets.Length)
                {
                    byte valType = valueTypes![subId];
                    int valSize = valueSizes![subId];
                    bool skip = baseTok == BinXmlToken.OptionalSubstitution &&
                                (valType == BinXmlValueType.Null || valSize == 0);
                    if (!skip && valType == BinXmlValueType.BinXml && valSize > 0)
                    {
                        // Embedded BinXml — render inline
                        ReadOnlySpan<byte> embeddedData = _fileData.AsSpan(valueOffsets[subId], valSize);
                        int embeddedChunkBase = valueOffsets[subId] - _chunkFileOffset;
                        int embeddedPos = 0;
                        ParseDocumentJson(embeddedData, ref embeddedPos, embeddedChunkBase, w);
                    }
                    else if (!skip && valSize > 0)
                    {
                        w.WritePropertyName("#text");
                        RenderValueJson(valSize, valType, valueOffsets[subId], binxmlChunkBase, w);
                    }
                }
            }
            else if (baseTok == BinXmlToken.TemplateInstance)
            {
                ParseTemplateInstanceJson(data, ref pos, binxmlChunkBase, w);
            }
            else if (baseTok == BinXmlToken.FragmentHeader)
            {
                ParseFragmentJson(data, ref pos, binxmlChunkBase, w);
            }
            else
            {
                pos++;
            }
        }
    }

    /// <summary>
    /// Peeks at a Data element to check for Name="..." attribute, returns the value or null.
    /// Does not advance the caller's position (uses a local copy of <paramref name="pos"/>).
    /// </summary>
    /// <param name="data">BinXml byte stream.</param>
    /// <param name="pos">Element start position (not modified — value copy).</param>
    /// <param name="binxmlChunkBase">Chunk-relative base offset of <paramref name="data"/>.</param>
    /// <param name="valueOffsets">File offsets of substitution values, or null.</param>
    /// <param name="valueSizes">Byte sizes of substitution values.</param>
    /// <param name="valueTypes">BinXml value type codes for each substitution.</param>
    /// <returns>The Name attribute value if present and non-empty; otherwise null.</returns>
    private string? PeekDataNameAttribute(ReadOnlySpan<byte> data, int pos,
                                          int binxmlChunkBase, int[]? valueOffsets, int[]? valueSizes, byte[]? valueTypes)
    {
        byte tok = data[pos];
        bool hasAttrs = (tok & BinXmlToken.HasMoreDataFlag) != 0;
        if (!hasAttrs) return null;

        pos++; // token
        pos += 2; // depId
        pos += 4; // dataSize
        uint nameOffset = MemoryMarshal.Read<uint>(data.Slice(pos));
        pos += 4;

        if (!TrySkipInlineName(data, ref pos, nameOffset, binxmlChunkBase)) return null;

        // Now at attribute list
        if (pos + 4 > data.Length) return null;
        uint attrListSize = MemoryMarshal.Read<uint>(data.Slice(pos));
        pos += 4;
        int attrEnd = pos + (int)attrListSize;

        while (pos < attrEnd)
        {
            byte attrTok = data[pos];
            byte attrBase = (byte)(attrTok & ~BinXmlToken.HasMoreDataFlag);
            if (attrBase != BinXmlToken.Attribute) break;

            pos++;
            uint attrNameOff = MemoryMarshal.Read<uint>(data.Slice(pos));
            pos += 4;
            if (!TrySkipInlineName(data, ref pos, attrNameOff, binxmlChunkBase)) break;

            string attrName = ReadName(attrNameOff);
            if (attrName == "Name")
            {
                string val =
                    RenderContentToString(data, ref pos, valueOffsets, valueSizes, valueTypes, binxmlChunkBase);
                return val.Length > 0 ? val : null;
            }
            else
            {
                // Skip attribute value content
                SkipContent(data, ref pos, binxmlChunkBase);
            }
        }

        return null;
    }

    /// <summary>
    /// Renders a Data element's text value as JSON, skipping the element structure.
    /// Used for EventData/UserData Name= flattening.
    /// </summary>
    /// <param name="data">BinXml byte stream.</param>
    /// <param name="pos">Current read position at the Data element's OpenStartElement token; advanced past the entire element.</param>
    /// <param name="valueOffsets">File offsets of substitution values, or null.</param>
    /// <param name="valueSizes">Byte sizes of substitution values.</param>
    /// <param name="valueTypes">BinXml value type codes for each substitution.</param>
    /// <param name="binxmlChunkBase">Chunk-relative base offset of <paramref name="data"/>.</param>
    /// <param name="w">UTF-8 JSON writer that receives the output.</param>
    private void RenderDataElementValueJson(ReadOnlySpan<byte> data, ref int pos,
                                            int[]? valueOffsets, int[]? valueSizes, byte[]? valueTypes,
                                            int binxmlChunkBase, Utf8JsonWriter w)
    {
        byte tok = data[pos];
        bool hasAttrs = (tok & BinXmlToken.HasMoreDataFlag) != 0;
        pos++;
        pos += 2; // depId
        pos += 4; // dataSize
        uint nameOffset = MemoryMarshal.Read<uint>(data.Slice(pos));
        pos += 4;

        if (!TrySkipInlineName(data, ref pos, nameOffset, binxmlChunkBase))
        {
            w.WriteNullValue();
            return;
        }

        if (hasAttrs)
        {
            uint attrListSize = MemoryMarshal.Read<uint>(data.Slice(pos));
            pos += 4;
            int attrEnd = pos + (int)attrListSize;
            // Skip attributes (we already peeked Name=)
            while (pos < attrEnd)
            {
                byte attrTok = data[pos];
                byte attrBase = (byte)(attrTok & ~BinXmlToken.HasMoreDataFlag);
                if (attrBase != BinXmlToken.Attribute) break;
                pos++;
                uint attrNameOff = MemoryMarshal.Read<uint>(data.Slice(pos));
                pos += 4;
                if (!TrySkipInlineName(data, ref pos, attrNameOff, binxmlChunkBase)) break;

                SkipContent(data, ref pos, binxmlChunkBase);
            }
        }

        if (pos >= data.Length)
        {
            w.WriteNullValue();
            return;
        }

        byte closeTok = data[pos];
        if (closeTok == BinXmlToken.CloseEmptyElement)
        {
            pos++;
            w.WriteNullValue();
            return;
        }

        if (closeTok == BinXmlToken.CloseStartElement)
        {
            pos++;
            RenderTextContentJson(data, ref pos, valueOffsets, valueSizes, valueTypes, binxmlChunkBase, w);
            if (pos < data.Length && data[pos] == BinXmlToken.EndElement)
                pos++;
        }
        else
        {
            w.WriteNullValue();
        }
    }

    /// <summary>
    /// Renders content tokens as a plain string (for attribute values and text content in JSON mode).
    /// </summary>
    /// <param name="data">BinXml byte stream.</param>
    /// <param name="pos">Current read position; advanced past consumed content tokens.</param>
    /// <param name="valueOffsets">File offsets of substitution values, or null.</param>
    /// <param name="valueSizes">Byte sizes of substitution values.</param>
    /// <param name="valueTypes">BinXml value type codes for each substitution.</param>
    /// <param name="binxmlChunkBase">Chunk-relative base offset of <paramref name="data"/>.</param>
    /// <returns>The concatenated plain-text string of all content tokens.</returns>
    private string RenderContentToString(ReadOnlySpan<byte> data, ref int pos,
                                         int[]? valueOffsets, int[]? valueSizes, byte[]? valueTypes, int binxmlChunkBase)
    {
        ValueStringBuilder vsb = new(stackalloc char[128]);

        while (pos < data.Length)
        {
            byte tok = data[pos];
            byte baseTok = (byte)(tok & ~BinXmlToken.HasMoreDataFlag);

            if (baseTok == BinXmlToken.Eof ||
                baseTok == BinXmlToken.CloseStartElement ||
                baseTok == BinXmlToken.CloseEmptyElement ||
                baseTok == BinXmlToken.EndElement ||
                baseTok == BinXmlToken.Attribute)
                break;

            switch (baseTok)
            {
                case BinXmlToken.Value:
                    pos++;
                    pos++;
                    vsb.Append(ReadUnicodeTextStringAsString(data, ref pos));
                    break;
                case BinXmlToken.NormalSubstitution:
                {
                    pos++;
                    ushort subId = MemoryMarshal.Read<ushort>(data.Slice(pos));
                    pos += 2;
                    pos++;
                    if (valueOffsets != null && subId < valueOffsets.Length)
                        RenderValue(valueSizes![subId], valueTypes![subId], valueOffsets[subId], binxmlChunkBase,
                            ref vsb);
                    break;
                }
                case BinXmlToken.OptionalSubstitution:
                {
                    pos++;
                    ushort subId = MemoryMarshal.Read<ushort>(data.Slice(pos));
                    pos += 2;
                    pos++;
                    if (valueOffsets != null && subId < valueOffsets.Length)
                    {
                        byte vt = valueTypes![subId];
                        int vs = valueSizes![subId];
                        if (vt != BinXmlValueType.Null && vs > 0)
                            RenderValue(vs, vt, valueOffsets[subId], binxmlChunkBase, ref vsb);
                    }

                    break;
                }
                case BinXmlToken.CharRef:
                {
                    pos++;
                    ushort charVal = MemoryMarshal.Read<ushort>(data.Slice(pos));
                    pos += 2;
                    vsb.Append((char)charVal);
                    break;
                }
                case BinXmlToken.EntityRef:
                {
                    pos++;
                    uint nameOff = MemoryMarshal.Read<uint>(data.Slice(pos));
                    pos += 4;
                    string entityName = ReadName(nameOff);
                    string resolved = entityName switch
                    {
                        "amp" => "&",
                        "lt" => "<",
                        "gt" => ">",
                        "quot" => "\"",
                        "apos" => "'",
                        _ => $"&{entityName};"
                    };
                    vsb.Append(resolved);
                    break;
                }
                case BinXmlToken.CDataSection:
                    pos++;
                    vsb.Append(ReadUnicodeTextStringAsString(data, ref pos));
                    break;
                default:
                    pos++;
                    break;
            }
        }

        string result = vsb.ToString();
        vsb.Dispose();
        return result;
    }

    /// <summary>
    /// Skips all content tokens and consumes the trailing EndElement (0x04) token if present.
    /// Used to discard the body of an empty or unneeded element.
    /// </summary>
    /// <param name="data">BinXml byte stream.</param>
    /// <param name="pos">Current read position; advanced past content and the EndElement token.</param>
    private void SkipToEndElement(ReadOnlySpan<byte> data, ref int pos)
    {
        SkipContent(data, ref pos, 0);
        if (pos < data.Length && data[pos] == BinXmlToken.EndElement)
            pos++;
    }

    /// <summary>
    /// Renders a typed value as a JSON value.
    /// Numeric types are written as JSON numbers; strings, GUIDs, SIDs, timestamps, and hex values
    /// are written as JSON strings; booleans as JSON booleans; embedded BinXml is recursively parsed.
    /// </summary>
    /// <param name="size">Byte size of the value data.</param>
    /// <param name="valueType">BinXml value type code. Bit 0x80 indicates an array.</param>
    /// <param name="fileOffset">Absolute byte offset of the value data within <see cref="_fileData"/>.</param>
    /// <param name="binxmlChunkBase">Chunk-relative base offset for embedded BinXml (type 0x21) resolution.</param>
    /// <param name="w">UTF-8 JSON writer that receives the output.</param>
    private void RenderValueJson(int size, byte valueType, int fileOffset, int binxmlChunkBase, Utf8JsonWriter w)
    {
        if (size == 0)
        {
            w.WriteNullValue();
            return;
        }

        ReadOnlySpan<byte> valueBytes = _fileData.AsSpan(fileOffset, size);

        // Array flag
        if ((valueType & BinXmlValueType.ArrayFlag) != 0)
        {
            RenderArrayJson(valueBytes, (byte)(valueType & 0x7F), fileOffset, binxmlChunkBase, w);
            return;
        }

        switch (valueType)
        {
            case BinXmlValueType.Null:
                w.WriteNullValue();
                break;

            case BinXmlValueType.String:
            {
                ReadOnlySpan<char> chars = MemoryMarshal.Cast<byte, char>(valueBytes);
                if (chars.Length > 0 && chars[^1] == '\0')
                    chars = chars[..^1];
                w.WriteStringValue(chars);
                break;
            }

            case BinXmlValueType.AnsiString:
            {
                // Convert byte-by-byte to string
                int len = valueBytes.IndexOf((byte)0);
                if (len < 0) len = valueBytes.Length;
                Span<char> ansiChars = stackalloc char[len];
                for (int i = 0; i < len; i++) ansiChars[i] = (char)valueBytes[i];
                w.WriteStringValue(ansiChars);
                break;
            }

            case BinXmlValueType.Int8:
                w.WriteNumberValue((sbyte)valueBytes[0]);
                break;

            case BinXmlValueType.UInt8:
                w.WriteNumberValue(valueBytes[0]);
                break;

            case BinXmlValueType.Int16:
                w.WriteNumberValue(MemoryMarshal.Read<short>(valueBytes));
                break;

            case BinXmlValueType.UInt16:
                w.WriteNumberValue(MemoryMarshal.Read<ushort>(valueBytes));
                break;

            case BinXmlValueType.Int32:
                w.WriteNumberValue(MemoryMarshal.Read<int>(valueBytes));
                break;

            case BinXmlValueType.UInt32:
                w.WriteNumberValue(MemoryMarshal.Read<uint>(valueBytes));
                break;

            case BinXmlValueType.Int64:
                w.WriteNumberValue(MemoryMarshal.Read<long>(valueBytes));
                break;

            case BinXmlValueType.UInt64:
                w.WriteNumberValue(MemoryMarshal.Read<ulong>(valueBytes));
                break;

            case BinXmlValueType.Float:
                w.WriteNumberValue(MemoryMarshal.Read<float>(valueBytes));
                break;

            case BinXmlValueType.Double:
                w.WriteNumberValue(MemoryMarshal.Read<double>(valueBytes));
                break;

            case BinXmlValueType.Bool:
                w.WriteBooleanValue(MemoryMarshal.Read<uint>(valueBytes) != 0);
                break;

            case BinXmlValueType.Binary:
            {
                ValueStringBuilder vsb = new(stackalloc char[size * 2]);
                AppendHex(ref vsb, valueBytes);
                w.WriteStringValue(vsb.AsSpan());
                vsb.Dispose();
                break;
            }

            case BinXmlValueType.Guid:
            {
                if (size < 16)
                {
                    w.WriteNullValue();
                    break;
                }

                ValueStringBuilder vsb = new(stackalloc char[38]);
                RenderGuid(valueBytes, ref vsb);
                w.WriteStringValue(vsb.AsSpan());
                vsb.Dispose();
                break;
            }

            case BinXmlValueType.SizeT:
            {
                ValueStringBuilder vsb = new(stackalloc char[18]);
                vsb.Append("0x");
                if (size == 8)
                    vsb.AppendFormatted(MemoryMarshal.Read<ulong>(valueBytes), "x16");
                else
                    vsb.AppendFormatted(MemoryMarshal.Read<uint>(valueBytes), "x8");
                w.WriteStringValue(vsb.AsSpan());
                vsb.Dispose();
                break;
            }

            case BinXmlValueType.FileTime:
            {
                if (size < 8)
                {
                    w.WriteNullValue();
                    break;
                }

                long ticks = MemoryMarshal.Read<long>(valueBytes);
                if (ticks == 0)
                {
                    w.WriteStringValue("");
                    break;
                }

                const long FileTimeEpochDelta = 504911232000000000L;
                DateTime dt = new DateTime(ticks + FileTimeEpochDelta, DateTimeKind.Utc);
                ValueStringBuilder vsb = new(stackalloc char[28]);
                vsb.AppendFormatted(dt, "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'");
                w.WriteStringValue(vsb.AsSpan());
                vsb.Dispose();
                break;
            }

            case BinXmlValueType.SystemTime:
            {
                if (size < 16)
                {
                    w.WriteNullValue();
                    break;
                }

                ushort yr = MemoryMarshal.Read<ushort>(valueBytes);
                ushort mo = MemoryMarshal.Read<ushort>(valueBytes.Slice(2));
                ushort dy = MemoryMarshal.Read<ushort>(valueBytes.Slice(6));
                ushort hr = MemoryMarshal.Read<ushort>(valueBytes.Slice(8));
                ushort mn = MemoryMarshal.Read<ushort>(valueBytes.Slice(10));
                ushort sc = MemoryMarshal.Read<ushort>(valueBytes.Slice(12));
                ushort ms = MemoryMarshal.Read<ushort>(valueBytes.Slice(14));
                ValueStringBuilder vsb = new(stackalloc char[24]);
                vsb.AppendFormatted(yr, "D4");
                vsb.Append('-');
                vsb.AppendFormatted(mo, "D2");
                vsb.Append('-');
                vsb.AppendFormatted(dy, "D2");
                vsb.Append('T');
                vsb.AppendFormatted(hr, "D2");
                vsb.Append(':');
                vsb.AppendFormatted(mn, "D2");
                vsb.Append(':');
                vsb.AppendFormatted(sc, "D2");
                vsb.Append('.');
                vsb.AppendFormatted(ms, "D3");
                vsb.Append('Z');
                w.WriteStringValue(vsb.AsSpan());
                vsb.Dispose();
                break;
            }

            case BinXmlValueType.Sid:
            {
                if (size < 8)
                {
                    w.WriteNullValue();
                    break;
                }

                ValueStringBuilder vsb = new(stackalloc char[64]);
                byte revision = valueBytes[0];
                byte subCount = valueBytes[1];
                long authority = 0;
                for (int i = 2; i < 8; i++)
                    authority = authority * 256 + valueBytes[i];
                vsb.Append("S-");
                vsb.AppendFormatted(revision);
                vsb.Append('-');
                vsb.AppendFormatted(authority);
                for (int i = 0; i < subCount; i++)
                {
                    int subOff = 8 + i * 4;
                    if (subOff + 4 > size) break;
                    vsb.Append('-');
                    vsb.AppendFormatted(MemoryMarshal.Read<uint>(valueBytes.Slice(subOff)));
                }

                w.WriteStringValue(vsb.AsSpan());
                vsb.Dispose();
                break;
            }

            case BinXmlValueType.HexInt32:
            {
                ValueStringBuilder vsb = new(stackalloc char[10]);
                vsb.Append("0x");
                vsb.AppendFormatted(MemoryMarshal.Read<uint>(valueBytes), "x8");
                w.WriteStringValue(vsb.AsSpan());
                vsb.Dispose();
                break;
            }

            case BinXmlValueType.HexInt64:
            {
                ValueStringBuilder vsb = new(stackalloc char[18]);
                vsb.Append("0x");
                vsb.AppendFormatted(MemoryMarshal.Read<ulong>(valueBytes), "x16");
                w.WriteStringValue(vsb.AsSpan());
                vsb.Dispose();
                break;
            }

            case BinXmlValueType.BinXml:
            {
                int embeddedChunkBase = fileOffset - _chunkFileOffset;
                int embeddedPos = 0;
                ParseDocumentJson(valueBytes, ref embeddedPos, embeddedChunkBase, w);
                break;
            }

            default:
            {
                ValueStringBuilder vsb = new(stackalloc char[size * 2]);
                AppendHex(ref vsb, valueBytes);
                w.WriteStringValue(vsb.AsSpan());
                vsb.Dispose();
                break;
            }
        }
    }

    /// <summary>
    /// Renders an array-typed value as a JSON array. String arrays are split on null terminators;
    /// fixed-size types are split by element size. Falls back to a single hex string for unknown types.
    /// </summary>
    /// <param name="valueBytes">Raw value bytes containing the array data.</param>
    /// <param name="baseType">Base BinXml value type (with array flag 0x80 masked off).</param>
    /// <param name="fileOffset">Absolute byte offset of the value data within <see cref="_fileData"/>.</param>
    /// <param name="binxmlChunkBase">Chunk-relative base offset for nested value rendering.</param>
    /// <param name="w">UTF-8 JSON writer that receives the output.</param>
    private void RenderArrayJson(ReadOnlySpan<byte> valueBytes, byte baseType, int fileOffset,
                                 int binxmlChunkBase, Utf8JsonWriter w)
    {
        w.WriteStartArray();

        if (baseType == BinXmlValueType.String)
        {
            ReadOnlySpan<char> chars = MemoryMarshal.Cast<byte, char>(valueBytes);
            int start = 0;
            for (int i = 0; i <= chars.Length; i++)
            {
                if (i == chars.Length || chars[i] == '\0')
                {
                    if (i > start)
                        w.WriteStringValue(chars.Slice(start, i - start));
                    start = i + 1;
                }
            }
        }
        else
        {
            int elemSize = GetElementSize(baseType);
            if (elemSize > 0 && valueBytes.Length >= elemSize)
            {
                for (int i = 0; i + elemSize <= valueBytes.Length; i += elemSize)
                    RenderValueJson(elemSize, baseType, fileOffset + i, binxmlChunkBase, w);
            }
            else
            {
                ValueStringBuilder vsb = new(stackalloc char[valueBytes.Length * 2]);
                AppendHex(ref vsb, valueBytes);
                w.WriteStringValue(vsb.AsSpan());
                vsb.Dispose();
            }
        }

        w.WriteEndArray();
    }
}