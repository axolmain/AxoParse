using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace AxoParse.Evtx;

/// <summary>
/// Core BinXml parser. One instance per chunk. Produces XML strings from BinXml token streams.
/// </summary>
internal sealed partial class BinXmlParser
{
    /// <summary>
    /// Maximum nesting depth for recursive element parsing to prevent stack overflow on crafted input.
    /// </summary>
    private const int MaxRecursionDepth = 64;

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
        ushort numChars = MemoryMarshal.Read<ushort>(chunkData[(offset + 6)..]);
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

        ushort numChars = MemoryMarshal.Read<ushort>(data[(pos + 6)..]);
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
                uint piNameOff = MemoryMarshal.Read<uint>(data[pos..]);
                pos += 4;
                string piName = ReadName(piNameOff);
                vsb.Append("<?");
                vsb.Append(piName);

                if (pos < data.Length && data[pos] == BinXmlToken.PiData)
                {
                    pos++; // consume 0x0B
                    string piText = BinXmlValueFormatter.ReadUnicodeTextStringAsString(data, ref pos);
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
        uint defDataOffset = MemoryMarshal.Read<uint>(data[pos..]);
        pos += 4;

        // Determine inline vs back-reference
        uint currentChunkRelOffset = (uint)(binxmlChunkBase + pos);
        bool isInline = defDataOffset == currentChunkRelOffset;

        Guid templateGuid = default;
        uint dataSize = 0;

        if (isInline)
        {
            pos += 4; // next def offset
            templateGuid = MemoryMarshal.Read<Guid>(data[pos..]);
            pos += 16;
            dataSize = MemoryMarshal.Read<uint>(data[pos..]);
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
                templateGuid = MemoryMarshal.Read<Guid>(chunkData[((int)defDataOffset + 4)..]);
                dataSize = MemoryMarshal.Read<uint>(chunkData[((int)defDataOffset + 20)..]);
            }
        }

        // Read substitution descriptors and values
        uint numValues = MemoryMarshal.Read<uint>(data[pos..]);
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

        // Convert span-relative pos to absolute file offset for RenderValue
        int dataBaseFileOffset = _chunkFileOffset + binxmlChunkBase;
        for (int i = 0; i < numVals; i++)
        {
            valueOffsets[i] = dataBaseFileOffset + pos;
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
        uint nameOffset = MemoryMarshal.Read<uint>(data[pos..]);
        pos += 4;

        if (!TrySkipInlineName(data, ref pos, nameOffset, binxmlChunkBase)) return;

        string elemName = ReadName(nameOffset);
        vsb.Append('<');
        vsb.Append(elemName);

        // Parse attribute list if present
        if (hasAttrs)
        {
            uint attrListSize = MemoryMarshal.Read<uint>(data[pos..]);
            pos += 4;
            int attrEnd = pos + (int)attrListSize;

            while (pos < attrEnd)
            {
                byte attrTok = data[pos];
                byte attrBase = (byte)(attrTok & ~BinXmlToken.HasMoreDataFlag);
                if (attrBase != BinXmlToken.Attribute) break;

                pos++; // consume attribute token
                uint attrNameOff = MemoryMarshal.Read<uint>(data[pos..]);
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
                    string str = BinXmlValueFormatter.ReadUnicodeTextStringAsString(data, ref pos);
                    BinXmlValueFormatter.AppendXmlEscaped(ref vsb, str.AsSpan());
                    break;
                }
                case BinXmlToken.NormalSubstitution:
                {
                    pos++; // consume token
                    ushort subId = MemoryMarshal.Read<ushort>(data[pos..]);
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
                    ushort subId = MemoryMarshal.Read<ushort>(data[pos..]);
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
                    ushort charVal = MemoryMarshal.Read<ushort>(data[pos..]);
                    pos += 2;
                    vsb.Append("&#");
                    vsb.AppendFormatted(charVal);
                    vsb.Append(';');
                    break;
                }
                case BinXmlToken.EntityRef:
                {
                    pos++; // consume token
                    uint nameOff = MemoryMarshal.Read<uint>(data[pos..]);
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
                    string cdataStr = BinXmlValueFormatter.ReadUnicodeTextStringAsString(data, ref pos);
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
                BinXmlValueFormatter.AppendXmlEscaped(ref vsb, chars);
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
                BinXmlValueFormatter.AppendHex(ref vsb, valueBytes);
                break;

            case BinXmlValueType.Guid:
            {
                if (size < 16) break;
                BinXmlValueFormatter.RenderGuid(valueBytes, ref vsb);
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
                BinXmlValueFormatter.AppendFileTime(valueBytes, ref vsb);
                break;
            }

            case BinXmlValueType.SystemTime:
            {
                if (size < 16) break;
                BinXmlValueFormatter.AppendSystemTime(valueBytes, ref vsb);
                break;
            }

            case BinXmlValueType.Sid:
            {
                if (size < 8) break;
                BinXmlValueFormatter.AppendSid(valueBytes, size, ref vsb);
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
                BinXmlValueFormatter.AppendHex(ref vsb, valueBytes);
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
                        BinXmlValueFormatter.AppendXmlEscaped(ref vsb, chars.Slice(start, i - start));
                        first = false;
                    }

                    start = i + 1;
                }
            }

            return;
        }

        // Fixed-size array types
        int elemSize = BinXmlValueFormatter.GetElementSize(baseType);
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
        BinXmlValueFormatter.AppendHex(ref vsb, valueBytes);
    }
}
