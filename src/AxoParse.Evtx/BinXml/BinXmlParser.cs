using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using AxoParse.Evtx.Evtx;

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
    /// <param name="compiledCache">Shared cross-chunk cache of compiled XML templates keyed by GUID. Null when using JSON output.</param>
    /// <param name="compiledJsonCache">Shared cross-chunk cache of compiled JSON templates keyed by GUID. Null when using XML output.</param>
    public BinXmlParser(
        byte[] fileData,
        int chunkFileOffset,
        Dictionary<uint, BinXmlTemplateDefinition> templates,
        Dictionary<Guid, CompiledTemplate?>? compiledCache,
        Dictionary<Guid, CompiledJsonTemplate?>? compiledJsonCache = null)
    {
        _fileData = fileData;
        _chunkFileOffset = chunkFileOffset;
        _templates = templates;
        _compiledCache = compiledCache;
        _compiledJsonCache = compiledJsonCache;

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
        if (format == OutputFormat.Json)
            ParseTopLevelJson(eventData, ref pos, binxmlChunkBase, ref vub);
        else
            ParseTopLevel(eventData, ref pos, binxmlChunkBase, ref vub, handlePiTarget: true);
    }

    #endregion

    #region Non-Public Methods

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
    /// Parses a sequence of BinXml content tokens (child elements, text values, substitutions,
    /// character/entity references, CDATA sections) until a break token is encountered.
    /// Break tokens: EOF (0x00), CloseStartElement (0x02), CloseEmptyElement (0x03),
    /// EndElement (0x04), Attribute (0x06).
    /// When <paramref name="resolveEntities"/> is true, produces plain text output suitable for
    /// JSON: character references emit the literal character, entity references are resolved to
    /// their character values, Value tokens are not XML-escaped, and CDATA sections emit raw text.
    /// </summary>
    /// <param name="data">BinXml byte stream.</param>
    /// <param name="pos">Current read position; advanced past all consumed content tokens.</param>
    /// <param name="valueOffsets">File offsets of substitution values, or null if no template context.</param>
    /// <param name="valueSizes">Byte sizes of substitution values.</param>
    /// <param name="valueTypes">BinXml value type codes for each substitution.</param>
    /// <param name="binxmlChunkBase">Chunk-relative base offset of <paramref name="data"/>.</param>
    /// <param name="vsb">String builder that receives the output.</param>
    /// <param name="depth">Current recursion depth for stack overflow protection.</param>
    /// <param name="resolveEntities">When true, produces plain text (no XML escaping/wrapping).</param>
    private void ParseContent(ReadOnlySpan<byte> data, ref int pos,
                              int[]? valueOffsets, int[]? valueSizes, byte[]? valueTypes,
                              int binxmlChunkBase, ref ValueUtf8Builder vsb, int depth = 0,
                              bool resolveEntities = false)
    {
        while (pos < data.Length)
        {
            byte tok = data[pos];
            byte baseTok = (byte)(tok & ~BinXmlToken.HasMoreDataFlag);

            // Break tokens
            if ((baseTok == BinXmlToken.Eof) ||
                (baseTok == BinXmlToken.CloseStartElement) ||
                (baseTok == BinXmlToken.CloseEmptyElement) ||
                (baseTok == BinXmlToken.EndElement) ||
                (baseTok == BinXmlToken.Attribute))
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
                    ReadOnlySpan<char> text = BinXmlValueFormatter.ReadUnicodeTextString(data, ref pos);
                    if (resolveEntities)
                        vsb.AppendUtf16(text);
                    else
                        BinXmlValueFormatter.AppendXmlEscaped(ref vsb, text);
                    break;
                }
                case BinXmlToken.NormalSubstitution:
                {
                    pos++; // consume token
                    ushort subId = MemoryMarshal.Read<ushort>(data[pos..]);
                    pos += 2;
                    pos++; // subValType
                    if ((valueOffsets != null) && (subId < valueOffsets.Length))
                    {
                        WriteBinXmlValue(valueSizes![subId], valueTypes![subId], valueOffsets[subId], binxmlChunkBase,
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
                    if ((valueOffsets != null) && (subId < valueOffsets.Length))
                    {
                        byte valType = valueTypes![subId];
                        int valSize = valueSizes![subId];
                        if ((valType != BinXmlValueType.Null) && (valSize > 0))
                        {
                            WriteBinXmlValue(valSize, valType, valueOffsets[subId], binxmlChunkBase, ref vsb);
                        }
                    }

                    break;
                }
                case BinXmlToken.CharRef:
                {
                    pos++; // consume token
                    ushort charVal = MemoryMarshal.Read<ushort>(data[pos..]);
                    pos += 2;
                    if (resolveEntities)
                    {
                        char resolvedChar = (char)charVal;
                        vsb.AppendUtf16(new ReadOnlySpan<char>(in resolvedChar));
                    }
                    else
                    {
                        vsb.Append("&#"u8);
                        vsb.AppendFormatted(charVal);
                        vsb.Append((byte)';');
                    }
                    break;
                }
                case BinXmlToken.EntityRef:
                {
                    pos++; // consume token
                    uint nameOff = MemoryMarshal.Read<uint>(data[pos..]);
                    pos += 4;
                    string entityName = ReadName(nameOff);
                    if (resolveEntities)
                    {
                        string resolved = entityName switch
                        {
                            "amp" => "&",
                            "lt" => "<",
                            "gt" => ">",
                            "quot" => "\"",
                            "apos" => "'",
                            _ => $"&{entityName};"
                        };
                        vsb.AppendUtf16(resolved);
                    }
                    else
                    {
                        vsb.Append((byte)'&');
                        vsb.AppendUtf16(entityName);
                        vsb.Append((byte)';');
                    }
                    break;
                }
                case BinXmlToken.CDataSection:
                {
                    pos++; // consume token
                    ReadOnlySpan<char> cdataChars = BinXmlValueFormatter.ReadUnicodeTextString(data, ref pos);
                    if (resolveEntities)
                    {
                        vsb.AppendUtf16(cdataChars);
                    }
                    else
                    {
                        vsb.Append("<![CDATA["u8);
                        vsb.AppendUtf16(cdataChars);
                        vsb.Append("]]>"u8);
                    }
                    break;
                }
                case BinXmlToken.TemplateInstance:
                    ParseTemplateInstance(data, ref pos, binxmlChunkBase, ref vsb);
                    break;
                case BinXmlToken.FragmentHeader:
                    ParseTopLevel(data, ref pos, binxmlChunkBase, ref vsb);
                    break;
                default:
                    pos++;
                    break;
            }
        }
    }

    /// <summary>
    /// Unified top-level BinXml dispatcher. Loops over fragment headers, template instances,
    /// bare elements, and optionally processing instructions until EOF or an unrecognized token.
    /// Replaces the former ParseDocument / ParseFragment / ParseEmbeddedBinXml methods.
    /// </summary>
    /// <param name="data">BinXml byte stream.</param>
    /// <param name="pos">Current read position; advanced past consumed tokens.</param>
    /// <param name="binxmlChunkBase">Chunk-relative base offset of <paramref name="data"/>.</param>
    /// <param name="vsb">String builder that receives the rendered XML output.</param>
    /// <param name="handlePiTarget">True for record-level parsing (processing instructions are valid); false for embedded BinXml.</param>
    private void ParseTopLevel(ReadOnlySpan<byte> data, ref int pos, int binxmlChunkBase,
                               ref ValueUtf8Builder vsb, bool handlePiTarget = false)
    {
        while (pos < data.Length)
        {
            byte baseTok = (byte)(data[pos] & ~BinXmlToken.HasMoreDataFlag);

            switch (baseTok)
            {
                case BinXmlToken.Eof:
                    return;

                case BinXmlToken.FragmentHeader:
                    pos += 4; // token + major + minor + flags
                    break;

                case BinXmlToken.TemplateInstance:
                    ParseTemplateInstance(data, ref pos, binxmlChunkBase, ref vsb);
                    break;

                case BinXmlToken.OpenStartElement:
                    ParseElement(data, ref pos, null, null, null, binxmlChunkBase, ref vsb);
                    break;

                case BinXmlToken.PiTarget when handlePiTarget:
                    pos++; // consume 0x0A
                    uint piNameOff = MemoryMarshal.Read<uint>(data[pos..]);
                    pos += 4;
                    string piName = ReadName(piNameOff);
                    vsb.Append("<?"u8);
                    vsb.AppendUtf16(piName);

                    if ((pos < data.Length) && (data[pos] == BinXmlToken.PiData))
                    {
                        pos++; // consume 0x0B
                        ReadOnlySpan<char> piText = BinXmlValueFormatter.ReadUnicodeTextString(data, ref pos);
                        if (piText.Length > 0)
                        {
                            vsb.Append((byte)' ');
                            vsb.AppendUtf16(piText);
                        }
                    }

                    vsb.Append("?>"u8);
                    break;

                default:
                    return;
            }
        }
    }

    /// <summary>
    /// Parses an OpenStartElement token (0x01/0x41) and its children into XML.
    /// Token layout: 1 token [+ 2 depId] + 4 dataSize + 4 nameOffset [+ inline name] [+ 4 attrListSize + attrs]
    /// followed by a close token (CloseEmpty 0x03, CloseStart 0x02, or EndElement 0x04).
    /// Bit 0x40 on the token indicates attributes are present.
    /// The 2-byte depId is present when <see cref="_insideTemplateBody"/> is true (chunk-level
    /// template definitions) but absent in top-level records and embedded BinXml fragments.
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
                              int binxmlChunkBase, ref ValueUtf8Builder vsb, int depth = 0)
    {
        if (depth >= _maxRecursionDepth) return;

        byte tok = data[pos];
        bool hasAttrs = (tok & BinXmlToken.HasMoreDataFlag) != 0;
        pos++; // consume token

        if (_insideTemplateBody)
            pos += 2; // depId — present only in template body elements
        pos += 4; // dataSize
        uint nameOffset = MemoryMarshal.Read<uint>(data[pos..]);
        pos += 4;

        if (!TrySkipInlineName(data, ref pos, nameOffset, binxmlChunkBase)) return;

        string elemName = ReadName(nameOffset);
        vsb.Append((byte)'<');
        vsb.AppendUtf16(elemName);

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

                // Peek ahead: if the attribute content is a single optional substitution
                // with null/empty value, omit the entire attribute
                if ((pos < data.Length) &&
                    ((data[pos] & ~BinXmlToken.HasMoreDataFlag) == BinXmlToken.OptionalSubstitution) &&
                    (valueOffsets != null))
                {
                    int peekPos = pos + 1;
                    ushort peekSubId = MemoryMarshal.Read<ushort>(data[peekPos..]);
                    peekPos += 2;
                    byte peekSubValType = data[peekPos];
                    peekPos++;

                    // Check what follows — if it's a break token, this is a single-sub attribute
                    byte peekNext = peekPos < data.Length ? (byte)(data[peekPos] & ~BinXmlToken.HasMoreDataFlag) : BinXmlToken.Eof;
                    bool isSingleSub = (peekNext == BinXmlToken.Eof) ||
                                       (peekNext == BinXmlToken.CloseStartElement) ||
                                       (peekNext == BinXmlToken.CloseEmptyElement) ||
                                       (peekNext == BinXmlToken.EndElement) ||
                                       (peekNext == BinXmlToken.Attribute);

                    if (isSingleSub && (peekSubId < valueOffsets.Length))
                    {
                        byte valType = valueTypes![peekSubId];
                        int valSize = valueSizes![peekSubId];
                        if ((valType == BinXmlValueType.Null) || (valSize == 0))
                        {
                            // Skip the substitution token, omit the attribute entirely
                            pos = peekPos;
                            continue;
                        }
                    }
                }

                string attrName = ReadName(attrNameOff);
                vsb.Append((byte)' ');
                vsb.AppendUtf16(attrName);
                vsb.Append("=\""u8);
                ParseContent(data, ref pos, valueOffsets, valueSizes, valueTypes, binxmlChunkBase, ref vsb, depth + 1);
                vsb.Append((byte)'"');
            }
        }

        // Close token
        if (pos >= data.Length)
        {
            vsb.Append("></"u8);
            vsb.AppendUtf16(elemName);
            vsb.Append((byte)'>');
            return;
        }

        byte closeTok = data[pos];
        if (closeTok == BinXmlToken.CloseEmptyElement)
        {
            pos++;
            vsb.Append("></"u8);
            vsb.AppendUtf16(elemName);
            vsb.Append((byte)'>');
        }
        else if (closeTok == BinXmlToken.CloseStartElement)
        {
            pos++;
            vsb.Append((byte)'>');
            ParseContent(data, ref pos, valueOffsets, valueSizes, valueTypes, binxmlChunkBase, ref vsb, depth + 1);
            if ((pos < data.Length) && (data[pos] == BinXmlToken.EndElement))
                pos++;
            vsb.Append("</"u8);
            vsb.AppendUtf16(elemName);
            vsb.Append((byte)'>');
        }
        else
        {
            vsb.Append("></"u8);
            vsb.AppendUtf16(elemName);
            vsb.Append((byte)'>');
        }
    }

    /// <summary>
    /// Parses a TemplateInstance token (0x0C) into XML output.
    /// Uses the compiled template cache for fast writing; falls back to full tree walk on cache miss.
    /// </summary>
    /// <param name="data">BinXml byte stream.</param>
    /// <param name="pos">Current read position; advanced past the entire template instance on return.</param>
    /// <param name="binxmlChunkBase">Chunk-relative base offset of <paramref name="data"/>.</param>
    /// <param name="vsb">String builder that receives the rendered XML output.</param>
    private void ParseTemplateInstance(ReadOnlySpan<byte> data, ref int pos, int binxmlChunkBase,
                                       ref ValueUtf8Builder vsb)
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
        int numVals = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(peekPos, 4));

        // stackalloc for the common case (≤ 64 substitutions); heap fallback for rare large templates
        const int StackThreshold = 64;
        Span<int> valueOffsets = numVals <= StackThreshold ? stackalloc int[StackThreshold] : new int[numVals];
        Span<int> valueSizes = numVals <= StackThreshold ? stackalloc int[StackThreshold] : new int[numVals];
        Span<byte> valueTypes = numVals <= StackThreshold ? stackalloc byte[StackThreshold] : new byte[numVals];

        TemplateInstanceHeader hdr = ReadTemplateInstanceData(data, ref pos, binxmlChunkBase,
            valueOffsets, valueSizes, valueTypes);

        if (hdr.DataSize == 0) return;

        int tplBodyFileOffset = _chunkFileOffset + (int)hdr.DefDataOffset + 24;
        if (tplBodyFileOffset + (int)hdr.DataSize > _fileData.Length) return;

        // Slice spans to actual value count
        ReadOnlySpan<int> offsets = valueOffsets.Slice(0, hdr.NumValues);
        ReadOnlySpan<int> sizes = valueSizes.Slice(0, hdr.NumValues);
        ReadOnlySpan<byte> types = valueTypes.Slice(0, hdr.NumValues);

        // Check compiled cache
        CompiledTemplate? compiled = null;
        if (_compiledCache != null)
        {
            if (!_compiledCache.TryGetValue(hdr.TemplateGuid, out compiled))
            {
                compiled = CompileTemplate((int)hdr.DefDataOffset, (int)hdr.DataSize);
                _compiledCache[hdr.TemplateGuid] = compiled;
            }
        }

        if (compiled != null)
        {
            WriteCompiled(compiled, offsets, sizes, types, binxmlChunkBase, ref vsb);
        }
        else
        {
            // Fallback: parse template body with substitutions (requires arrays for recursive ParseContent)
            ReadOnlySpan<byte> tplBody = _fileData.AsSpan(tplBodyFileOffset, (int)hdr.DataSize);
            int tplPos = 0;
            int tplChunkBase = (int)hdr.DefDataOffset + 24;

            // Skip fragment header
            if ((tplBody.Length >= 4) && (tplBody[0] == BinXmlToken.FragmentHeader))
                tplPos += 4;

            bool saved = _insideTemplateBody;
            _insideTemplateBody = true;
            ParseContent(tplBody, ref tplPos, offsets.ToArray(), sizes.ToArray(), types.ToArray(), tplChunkBase, ref vsb);
            _insideTemplateBody = saved;
        }
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
    /// Writes an array-typed value (type code has bit 0x80 set) as comma-separated XML text.
    /// String arrays (base type 0x01) are null-terminated UTF-16LE concatenated;
    /// fixed-size types are rendered by splitting on element size.
    /// </summary>
    /// <param name="valueBytes">Raw value bytes containing the array data.</param>
    /// <param name="baseType">Base BinXml value type (with array flag 0x80 masked off).</param>
    /// <param name="fileOffset">Absolute byte offset of the value data within <see cref="_fileData"/>.</param>
    /// <param name="binxmlChunkBase">Chunk-relative base offset for nested value rendering.</param>
    /// <param name="vsb">String builder that receives the comma-separated rendered elements.</param>
    private void WriteArray(ReadOnlySpan<byte> valueBytes, byte baseType, int fileOffset,
                            int binxmlChunkBase, ref ValueUtf8Builder vsb)
    {
        // String arrays: null-terminated UTF-16LE strings concatenated
        if (baseType == BinXmlValueType.String)
        {
            ReadOnlySpan<char> chars = MemoryMarshal.Cast<byte, char>(valueBytes);
            bool first = true;
            int start = 0;
            for (int i = 0; i <= chars.Length; i++)
            {
                if ((i == chars.Length) || (chars[i] == '\0'))
                {
                    if (i > start)
                    {
                        if (!first) vsb.Append(", "u8);
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
        if ((elemSize > 0) && (valueBytes.Length >= elemSize))
        {
            bool first = true;
            for (int i = 0; i + elemSize <= valueBytes.Length; i += elemSize)
            {
                if (!first) vsb.Append(", "u8);
                WriteBinXmlValue(elemSize, baseType, fileOffset + i, binxmlChunkBase, ref vsb);
                first = false;
            }

            return;
        }

        // Fallback: hex
        BinXmlValueFormatter.AppendHex(ref vsb, valueBytes);
    }

    /// <summary>
    /// Writes a single BinXml substitution value as XML text.
    /// Dispatches on value type to produce the appropriate string representation
    /// (numeric, GUID, SID, FILETIME, hex, embedded BinXml, etc.).
    /// </summary>
    /// <param name="size">Byte size of the value data.</param>
    /// <param name="valueType">BinXml value type code (see <see cref="BinXmlValueType"/>). Bit 0x80 indicates an array.</param>
    /// <param name="fileOffset">Absolute byte offset of the value data within <see cref="_fileData"/>.</param>
    /// <param name="binxmlChunkBase">Chunk-relative base offset used for embedded BinXml (type 0x21) resolution.</param>
    /// <param name="vsb">String builder that receives the rendered text.</param>
    private void WriteBinXmlValue(int size, byte valueType, int fileOffset, int binxmlChunkBase, ref ValueUtf8Builder vsb)
    {
        if (size == 0) return;
        ReadOnlySpan<byte> valueBytes = _fileData.AsSpan(fileOffset, size);

        // Array flag
        if ((valueType & BinXmlValueType.ArrayFlag) != 0)
        {
            WriteArray(valueBytes, (byte)(valueType & 0x7F), fileOffset, binxmlChunkBase, ref vsb);
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
                if ((chars.Length > 0) && (chars[^1] == '\0'))
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
                    if (b == '&') vsb.Append("&amp;"u8);
                    else if (b == '<') vsb.Append("&lt;"u8);
                    else if (b == '>') vsb.Append("&gt;"u8);
                    else if (b == '"') vsb.Append("&quot;"u8);
                    else if (b == '\'') vsb.Append("&apos;"u8);
                    else if (b < 0x80) vsb.Append(b);
                    else BinXmlValueFormatter.AppendLatin1(ref vsb, (char)b);
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
                vsb.Append(MemoryMarshal.Read<uint>(valueBytes) != 0 ? "true"u8 : "false"u8);
                break;

            case BinXmlValueType.Binary:
                BinXmlValueFormatter.AppendHex(ref vsb, valueBytes);
                break;

            case BinXmlValueType.Guid:
            {
                if (size < 16) break;
                BinXmlValueFormatter.FormatGuid(valueBytes, ref vsb);
                break;
            }

            case BinXmlValueType.SizeT:
            {
                vsb.Append("0x"u8);
                if (size == 8)
                    BinXmlValueFormatter.AppendHexUInt64Min(ref vsb, MemoryMarshal.Read<ulong>(valueBytes));
                else
                    BinXmlValueFormatter.AppendHexUInt32Min(ref vsb, MemoryMarshal.Read<uint>(valueBytes));
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
                vsb.Append("0x"u8);
                BinXmlValueFormatter.AppendHexUInt32Min(ref vsb, MemoryMarshal.Read<uint>(valueBytes));
                break;

            case BinXmlValueType.HexInt64:
                vsb.Append("0x"u8);
                BinXmlValueFormatter.AppendHexUInt64Min(ref vsb, MemoryMarshal.Read<ulong>(valueBytes));
                break;

            case BinXmlValueType.BinXml:
            {
                bool saved = _insideTemplateBody;
                _insideTemplateBody = false;
                int embeddedChunkBase = fileOffset - _chunkFileOffset;
                int embeddedPos = 0;
                ParseTopLevel(valueBytes, ref embeddedPos, embeddedChunkBase, ref vsb);
                _insideTemplateBody = saved;
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
    /// Writes a compiled template by filling in its static XML parts with formatted substitution values.
    /// </summary>
    /// <param name="compiled">Pre-compiled template containing static parts and substitution metadata.</param>
    /// <param name="valueOffsets">File offsets of each substitution value.</param>
    /// <param name="valueSizes">Byte sizes of each substitution value.</param>
    /// <param name="valueTypes">BinXml value type codes for each substitution.</param>
    /// <param name="binxmlChunkBase">Chunk-relative base offset used for embedded BinXml resolution.</param>
    /// <param name="vsb">String builder that receives the rendered XML output.</param>
    private void WriteCompiled(CompiledTemplate compiled,
                               scoped ReadOnlySpan<int> valueOffsets, scoped ReadOnlySpan<int> valueSizes,
                               scoped ReadOnlySpan<byte> valueTypes,
                               int binxmlChunkBase, ref ValueUtf8Builder vsb)
    {
        string[] parts = compiled.Parts;
        byte[]?[] utf8Parts = compiled.Utf8Parts;
        SubSlot[] slots = compiled.Slots;
        int slotCount = slots.Length;
        int valCount = valueOffsets.Length;

        CompiledTemplate.AppendPart(ref vsb, utf8Parts[0], parts[0]);
        for (int i = 0; i < slotCount; i++)
        {
            ref readonly SubSlot slot = ref slots[i];
            int subId = slot.SubId;
            if (subId < valCount)
            {
                byte valType = valueTypes[subId];
                int valSize = valueSizes[subId];
                if (!slot.IsOptional || ((valType != BinXmlValueType.Null) && (valSize > 0)))
                {
                    if (slot.AttrPrefix != null)
                        CompiledTemplate.AppendPart(ref vsb, slot.Utf8AttrPrefix, slot.AttrPrefix);
                    WriteBinXmlValue(valSize, valType, valueOffsets[subId], binxmlChunkBase, ref vsb);
                    if (slot.AttrSuffix != null)
                        CompiledTemplate.AppendPart(ref vsb, slot.Utf8AttrSuffix, slot.AttrSuffix);
                }
            }

            CompiledTemplate.AppendPart(ref vsb, utf8Parts[i + 1], parts[i + 1]);
        }
    }

    #endregion

    #region Non-Public Fields

    /// <summary>
    /// True when parsing inside a template body (elements have 2-byte dependency IDs);
    /// false at the top level or inside embedded BinXml (elements lack dependency IDs).
    /// Save/restore around transitions: set true in ParseTemplateInstance fallback,
    /// set false in WriteBinXmlValue(BinXml) for embedded fragments.
    /// </summary>
    private bool _insideTemplateBody;

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
    /// Process-wide cache of compiled XML templates keyed by template GUID.
    /// Shared across chunks to avoid recompiling identical templates. Null when using JSON output.
    /// </summary>
    private readonly Dictionary<Guid, CompiledTemplate?>? _compiledCache;

    /// <summary>
    /// Process-wide cache of compiled JSON templates keyed by template GUID.
    /// Shared across chunks to avoid recompiling identical templates. Null when using XML output.
    /// </summary>
    private readonly Dictionary<Guid, CompiledJsonTemplate?>? _compiledJsonCache;

    /// <summary>
    /// Raw EVTX file bytes shared across all chunks and records.
    /// </summary>
    private readonly byte[] _fileData;

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