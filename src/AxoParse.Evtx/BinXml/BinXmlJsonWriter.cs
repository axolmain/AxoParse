using System.Runtime.InteropServices;
using System.Text;
using AxoParse.Evtx.Evtx;

namespace AxoParse.Evtx.BinXml;

/// <summary>
/// Partial class containing compiled-template JSON writing methods.
/// Converts BinXml token streams to structural JSON output using the format:
/// {"#name":"...","#attrs":{...},"#content":[...]}.
/// All substitution values are rendered as JSON-escaped strings.
/// Uses compiled templates for fast output when possible, with a full tree-walk fallback.
/// </summary>
internal sealed partial class BinXmlParser
{
    #region Public Methods

    /// <summary>
    /// Parses a single record's BinXml event data into UTF-8 JSON bytes using the structural format.
    /// </summary>
    /// <param name="record">The EVTX record whose BinXml event data will be parsed.</param>
    /// <returns>UTF-8 encoded JSON bytes.</returns>
    public byte[] ParseRecordJson(EvtxRecord record)
    {
        ReadOnlySpan<byte> eventData = record.GetEventData(_fileData);
        int binxmlChunkBase = record.EventDataFileOffset - _chunkFileOffset;

        ValueStringBuilder vsb = new(stackalloc char[512]);
        int pos = 0;
        ParseTopLevelJson(eventData, ref pos, binxmlChunkBase, ref vsb);
        ReadOnlySpan<char> chars = vsb.AsSpan();
        int byteCount = Encoding.UTF8.GetByteCount(chars);
        byte[] result = new byte[byteCount];
        Encoding.UTF8.GetBytes(chars, result);
        vsb.Dispose();

        return result;
    }

    #endregion

    #region Non-Public Methods

    /// <summary>
    /// Fallback JSON content parser for uncompilable templates. Walks BinXml content tokens
    /// directly and produces the structural JSON format.
    /// </summary>
    /// <param name="data">BinXml byte stream.</param>
    /// <param name="pos">Current read position; advanced past consumed content tokens.</param>
    /// <param name="valueOffsets">File offsets of substitution values, or null if no template context.</param>
    /// <param name="valueSizes">Byte sizes of substitution values.</param>
    /// <param name="valueTypes">BinXml value type codes for each substitution.</param>
    /// <param name="binxmlChunkBase">Chunk-relative base offset of <paramref name="data"/>.</param>
    /// <param name="vsb">String builder that receives the JSON output.</param>
    /// <param name="depth">Current recursion depth for stack overflow protection.</param>
    /// <param name="needsComma">Ref tracking whether a comma is needed before the next array item.</param>
    private void ParseContentJson(ReadOnlySpan<byte> data, ref int pos,
                                  int[]? valueOffsets, int[]? valueSizes, byte[]? valueTypes,
                                  int binxmlChunkBase, ref ValueStringBuilder vsb, int depth,
                                  ref bool needsComma)
    {
        while (pos < data.Length)
        {
            byte tok = data[pos];
            byte baseTok = (byte)(tok & ~BinXmlToken.HasMoreDataFlag);

            if ((baseTok == BinXmlToken.Eof) ||
                (baseTok == BinXmlToken.CloseStartElement) ||
                (baseTok == BinXmlToken.CloseEmptyElement) ||
                (baseTok == BinXmlToken.EndElement) ||
                (baseTok == BinXmlToken.Attribute))
                break;

            switch (baseTok)
            {
                case BinXmlToken.OpenStartElement:
                    if (needsComma) vsb.Append(',');
                    needsComma = true;
                    ParseElementJson(data, ref pos, valueOffsets, valueSizes, valueTypes, binxmlChunkBase, ref vsb, depth + 1);
                    break;

                case BinXmlToken.Value:
                {
                    pos++; // token
                    pos++; // value type
                    ReadOnlySpan<char> chars = BinXmlValueFormatter.ReadUnicodeTextString(data, ref pos);
                    if (needsComma) vsb.Append(',');
                    needsComma = true;
                    vsb.Append('"');
                    BinXmlValueFormatter.AppendJsonEscaped(ref vsb, chars);
                    vsb.Append('"');
                    break;
                }

                case BinXmlToken.NormalSubstitution:
                case BinXmlToken.OptionalSubstitution:
                {
                    bool optional = baseTok == BinXmlToken.OptionalSubstitution;
                    pos++;
                    ushort subId = MemoryMarshal.Read<ushort>(data[pos..]);
                    pos += 2;
                    pos++; // subValType

                    if ((valueOffsets != null) && (subId < valueOffsets.Length))
                    {
                        int valSize = valueSizes![subId];
                        byte valType = valueTypes![subId];

                        if (!optional || ((valType != BinXmlValueType.Null) && (valSize > 0)))
                        {
                            if (needsComma) vsb.Append(',');
                            needsComma = true;
                            vsb.Append('"');
                            WriteJsonValue(valSize, valType, valueOffsets[subId], binxmlChunkBase, ref vsb);
                            vsb.Append('"');
                        }
                        else
                        {
                            // Optional null/empty: emit empty string to keep structure static
                            if (needsComma) vsb.Append(',');
                            needsComma = true;
                            vsb.Append("\"\"");
                        }
                    }
                    break;
                }

                case BinXmlToken.CharRef:
                {
                    pos++;
                    ushort charVal = MemoryMarshal.Read<ushort>(data[pos..]);
                    pos += 2;
                    if (needsComma) vsb.Append(',');
                    needsComma = true;
                    vsb.Append('"');
                    char ch = (char)charVal;
                    BinXmlValueFormatter.AppendJsonEscaped(ref vsb, new ReadOnlySpan<char>(in ch));
                    vsb.Append('"');
                    break;
                }

                case BinXmlToken.EntityRef:
                {
                    pos++;
                    uint nameOff = MemoryMarshal.Read<uint>(data[pos..]);
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
                    if (needsComma) vsb.Append(',');
                    needsComma = true;
                    vsb.Append('"');
                    BinXmlValueFormatter.AppendJsonEscaped(ref vsb, resolved.AsSpan());
                    vsb.Append('"');
                    break;
                }

                case BinXmlToken.CDataSection:
                {
                    pos++;
                    ReadOnlySpan<char> cdataChars = BinXmlValueFormatter.ReadUnicodeTextString(data, ref pos);
                    if (needsComma) vsb.Append(',');
                    needsComma = true;
                    vsb.Append('"');
                    BinXmlValueFormatter.AppendJsonEscaped(ref vsb, cdataChars);
                    vsb.Append('"');
                    break;
                }

                case BinXmlToken.TemplateInstance:
                    if (needsComma) vsb.Append(',');
                    needsComma = true;
                    ParseTemplateInstanceJson(data, ref pos, binxmlChunkBase, ref vsb);
                    break;

                case BinXmlToken.FragmentHeader:
                    // Skip the 4-byte header; the loop handles the subsequent token
                    // (TemplateInstance or OpenStartElement) with proper comma tracking.
                    pos += 4;
                    break;

                default:
                    pos++;
                    break;
            }
        }
    }

    /// <summary>
    /// Unified top-level BinXml dispatcher for JSON output. Loops over fragment headers,
    /// template instances, and bare elements, skipping processing instructions (no JSON equivalent).
    /// Replaces the former ParseDocumentJson / ParseFragmentJson methods.
    /// </summary>
    /// <param name="data">BinXml byte stream.</param>
    /// <param name="pos">Current read position; advanced past consumed tokens.</param>
    /// <param name="binxmlChunkBase">Chunk-relative base offset of <paramref name="data"/>.</param>
    /// <param name="vsb">String builder that receives the JSON output.</param>
    private void ParseTopLevelJson(ReadOnlySpan<byte> data, ref int pos, int binxmlChunkBase, ref ValueStringBuilder vsb)
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
                    ParseTemplateInstanceJson(data, ref pos, binxmlChunkBase, ref vsb);
                    break;

                case BinXmlToken.OpenStartElement:
                    ParseElementJson(data, ref pos, null, null, null, binxmlChunkBase, ref vsb);
                    break;

                case BinXmlToken.PiTarget:
                    // Skip PI for JSON — no equivalent
                    pos++;
                    pos += 4; // nameOffset
                    if ((pos < data.Length) && (data[pos] == BinXmlToken.PiData))
                    {
                        pos++;
                        ushort numChars = MemoryMarshal.Read<ushort>(data[pos..]);
                        pos += 2 + numChars * 2;
                    }
                    break;

                default:
                    return;
            }
        }
    }

    /// <summary>
    /// Fallback JSON element renderer for uncompilable templates. Produces the structural format
    /// {"#name":"...","#attrs":{...},"#content":[...]} by walking BinXml tokens directly.
    /// </summary>
    /// <param name="data">BinXml byte stream.</param>
    /// <param name="pos">Current read position; advanced past the entire element.</param>
    /// <param name="valueOffsets">File offsets of substitution values, or null.</param>
    /// <param name="valueSizes">Byte sizes of substitution values.</param>
    /// <param name="valueTypes">BinXml value type codes for each substitution.</param>
    /// <param name="binxmlChunkBase">Chunk-relative base offset of <paramref name="data"/>.</param>
    /// <param name="vsb">String builder that receives the JSON output.</param>
    /// <param name="depth">Current recursion depth for stack overflow protection.</param>
    private void ParseElementJson(ReadOnlySpan<byte> data, ref int pos,
                                  int[]? valueOffsets, int[]? valueSizes, byte[]? valueTypes,
                                  int binxmlChunkBase, ref ValueStringBuilder vsb, int depth = 0)
    {
        if (depth >= _maxRecursionDepth) return;

        byte tok = data[pos];
        bool hasAttrs = (tok & BinXmlToken.HasMoreDataFlag) != 0;
        pos++;

        if (_insideTemplateBody)
            pos += 2; // depId — present only in template body elements
        pos += 4; // dataSize
        uint nameOffset = MemoryMarshal.Read<uint>(data[pos..]);
        pos += 4;

        if (!TrySkipInlineName(data, ref pos, nameOffset, binxmlChunkBase)) return;

        string elemName = ReadName(nameOffset);
        vsb.Append("{\"#name\":\"");
        BinXmlValueFormatter.AppendJsonEscaped(ref vsb, elemName.AsSpan());
        vsb.Append('"');

        // Parse attributes
        if (hasAttrs)
        {
            uint attrListSize = MemoryMarshal.Read<uint>(data[pos..]);
            pos += 4;
            int attrEnd = pos + (int)attrListSize;

            vsb.Append(",\"#attrs\":{");
            bool firstAttr = true;

            while (pos < attrEnd)
            {
                byte attrTok = data[pos];
                byte attrBase = (byte)(attrTok & ~BinXmlToken.HasMoreDataFlag);
                if (attrBase != BinXmlToken.Attribute) break;

                pos++;
                uint attrNameOff = MemoryMarshal.Read<uint>(data[pos..]);
                pos += 4;
                if (!TrySkipInlineName(data, ref pos, attrNameOff, binxmlChunkBase)) break;

                string attrName = ReadName(attrNameOff);
                if (!firstAttr) vsb.Append(',');
                firstAttr = false;
                vsb.Append('"');
                BinXmlValueFormatter.AppendJsonEscaped(ref vsb, attrName.AsSpan());
                vsb.Append("\":\"");

                // Render attribute value content — reuse ParseContent with resolveEntities for plain text,
                // then JSON-escape into the string. Heap-allocate to avoid stackalloc inside loop.
                ValueStringBuilder attrVsb = new(new char[64]);
                ParseContent(data, ref pos, valueOffsets, valueSizes, valueTypes, binxmlChunkBase, ref attrVsb, depth + 1,
                    resolveEntities: true);
                BinXmlValueFormatter.AppendJsonEscaped(ref vsb, attrVsb.AsSpan());
                attrVsb.Dispose();
                vsb.Append('"');
            }

            vsb.Append('}');
        }

        // Close token
        if (pos >= data.Length)
        {
            vsb.Append('}');
            return;
        }

        byte closeTok = data[pos];
        if (closeTok == BinXmlToken.CloseEmptyElement)
        {
            pos++;
            vsb.Append('}');
        }
        else if (closeTok == BinXmlToken.CloseStartElement)
        {
            pos++;
            vsb.Append(",\"#content\":[");
            bool contentNeedsComma = false;
            ParseContentJson(data, ref pos, valueOffsets, valueSizes, valueTypes, binxmlChunkBase, ref vsb, depth + 1,
                needsComma: ref contentNeedsComma);
            if ((pos < data.Length) && (data[pos] == BinXmlToken.EndElement))
                pos++;
            vsb.Append("]}");
        }
        else
        {
            vsb.Append('}');
        }
    }

    /// <summary>
    /// JSON variant of <see cref="ParseTemplateInstance"/>. Uses <see cref="ReadTemplateInstanceData"/>
    /// for shared data extraction, then checks the compiled JSON cache for fast output.
    /// Falls back to full tree walk if the template cannot be compiled.
    /// </summary>
    /// <param name="data">BinXml byte stream.</param>
    /// <param name="pos">Current read position; advanced past the entire template instance.</param>
    /// <param name="binxmlChunkBase">Chunk-relative base offset of <paramref name="data"/>.</param>
    /// <param name="vsb">String builder that receives the JSON output.</param>
    private void ParseTemplateInstanceJson(ReadOnlySpan<byte> data, ref int pos, int binxmlChunkBase,
                                           ref ValueStringBuilder vsb)
    {
        TemplateInstanceData tid = ReadTemplateInstanceData(data, ref pos, binxmlChunkBase);

        if (tid.DataSize == 0) return;

        int tplBodyFileOffset = _chunkFileOffset + (int)tid.DefDataOffset + 24;
        if ((uint)(tplBodyFileOffset + tid.DataSize) > (uint)_fileData.Length) return;

        // Check compiled JSON cache
        if (_compiledJsonCache != null)
        {
            if (!_compiledJsonCache.TryGetValue(tid.TemplateGuid, out CompiledJsonTemplate? compiled))
            {
                compiled = CompileJsonTemplate((int)tid.DefDataOffset, (int)tid.DataSize);
                _compiledJsonCache[tid.TemplateGuid] = compiled;
            }

            if (compiled != null)
            {
                WriteCompiledJson(compiled, tid.ValueOffsets, tid.ValueSizes, tid.ValueTypes, binxmlChunkBase, ref vsb);
                return;
            }
        }

        // Fallback: parse template body with substitutions
        ReadOnlySpan<byte> tplBody = _fileData.AsSpan(tplBodyFileOffset, (int)tid.DataSize);
        int tplPos = 0;
        int tplChunkBase = (int)tid.DefDataOffset + 24;

        if ((tplBody.Length >= 4) && (tplBody[0] == BinXmlToken.FragmentHeader))
            tplPos += 4;

        bool saved = _insideTemplateBody;
        _insideTemplateBody = true;
        bool needsComma = false;
        ParseContentJson(tplBody, ref tplPos, tid.ValueOffsets, tid.ValueSizes, tid.ValueTypes, tplChunkBase,
            ref vsb, 0, needsComma: ref needsComma);
        _insideTemplateBody = saved;
    }

    /// <summary>
    /// Writes a compiled JSON template by filling in its static parts with formatted substitution values.
    /// </summary>
    /// <param name="compiled">Pre-compiled JSON template containing static parts and substitution metadata.</param>
    /// <param name="valueOffsets">File offsets of each substitution value.</param>
    /// <param name="valueSizes">Byte sizes of each substitution value.</param>
    /// <param name="valueTypes">BinXml value type codes for each substitution.</param>
    /// <param name="binxmlChunkBase">Chunk-relative base offset used for embedded BinXml resolution.</param>
    /// <param name="vsb">String builder that receives the rendered JSON output.</param>
    private void WriteCompiledJson(CompiledJsonTemplate compiled,
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
                if (!compiled.IsOptional[i] || ((valType != BinXmlValueType.Null) && (valSize > 0)))
                {
                    WriteJsonValue(valSize, valType, valueOffsets[subId], binxmlChunkBase, ref vsb);
                }
            }

            vsb.Append(compiled.Parts[i + 1]);
        }
    }

    /// <summary>
    /// Writes an array-typed value as comma-separated JSON-escaped string elements.
    /// String arrays are split on null terminators; fixed-size types are split by element size.
    /// </summary>
    /// <param name="valueBytes">Raw value bytes containing the array data.</param>
    /// <param name="baseType">Base BinXml value type (with array flag 0x80 masked off).</param>
    /// <param name="fileOffset">Absolute byte offset of the value data within <see cref="_fileData"/>.</param>
    /// <param name="binxmlChunkBase">Chunk-relative base offset for nested value rendering.</param>
    /// <param name="vsb">String builder that receives the comma-separated rendered elements.</param>
    private void WriteJsonArray(ReadOnlySpan<byte> valueBytes, byte baseType, int fileOffset,
                                int binxmlChunkBase, ref ValueStringBuilder vsb)
    {
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
                        if (!first) vsb.Append(", ");
                        BinXmlValueFormatter.AppendJsonEscaped(ref vsb, chars.Slice(start, i - start));
                        first = false;
                    }
                    start = i + 1;
                }
            }
            return;
        }

        int elemSize = BinXmlValueFormatter.GetElementSize(baseType);
        if ((elemSize > 0) && (valueBytes.Length >= elemSize))
        {
            bool first = true;
            for (int i = 0; i + elemSize <= valueBytes.Length; i += elemSize)
            {
                if (!first) vsb.Append(", ");
                WriteJsonValue(elemSize, baseType, fileOffset + i, binxmlChunkBase, ref vsb);
                first = false;
            }
            return;
        }

        // Fallback: hex
        BinXmlValueFormatter.AppendHex(ref vsb, valueBytes);
    }

    /// <summary>
    /// Formats a substitution value as a JSON-escaped string (no surrounding quotes — the quotes
    /// are baked into the compiled Parts). Dispatches on value type for correct formatting.
    /// For embedded BinXml (0x21), recursively renders via <see cref="ParseTopLevelJson"/> and
    /// JSON-escapes the result.
    /// </summary>
    /// <param name="size">Byte size of the value data.</param>
    /// <param name="valueType">BinXml value type code. Bit 0x80 indicates an array.</param>
    /// <param name="fileOffset">Absolute byte offset of the value data within <see cref="_fileData"/>.</param>
    /// <param name="binxmlChunkBase">Chunk-relative base offset for embedded BinXml (type 0x21) resolution.</param>
    /// <param name="vsb">String builder that receives the rendered text (no surrounding quotes).</param>
    private void WriteJsonValue(int size, byte valueType, int fileOffset, int binxmlChunkBase, ref ValueStringBuilder vsb)
    {
        if (size == 0) return;
        ReadOnlySpan<byte> valueBytes = _fileData.AsSpan(fileOffset, size);

        // Array flag — render as comma-separated values
        if ((valueType & BinXmlValueType.ArrayFlag) != 0)
        {
            WriteJsonArray(valueBytes, (byte)(valueType & 0x7F), fileOffset, binxmlChunkBase, ref vsb);
            return;
        }

        switch (valueType)
        {
            case BinXmlValueType.Null:
                break;

            case BinXmlValueType.String:
            {
                ReadOnlySpan<char> chars = MemoryMarshal.Cast<byte, char>(valueBytes);
                if ((chars.Length > 0) && (chars[^1] == '\0'))
                    chars = chars[..^1];
                BinXmlValueFormatter.AppendJsonEscaped(ref vsb, chars);
                break;
            }

            case BinXmlValueType.AnsiString:
            {
                for (int i = 0; i < valueBytes.Length; i++)
                {
                    byte b = valueBytes[i];
                    if (b == 0) break;
                    char c = (char)b;
                    if ((c == '\\') || (c == '"'))
                    {
                        vsb.Append('\\');
                        vsb.Append(c);
                    }
                    else if (c < '\u0020')
                    {
                        switch (c)
                        {
                            case '\n': vsb.Append("\\n"); break;
                            case '\r': vsb.Append("\\r"); break;
                            case '\t': vsb.Append("\\t"); break;
                            case '\b': vsb.Append("\\b"); break;
                            case '\f': vsb.Append("\\f"); break;
                            default:
                                vsb.Append("\\u00");
                                vsb.Append(BinXmlValueFormatter.HexLookup[b]);
                                break;
                        }
                    }
                    else
                    {
                        vsb.Append(c);
                    }
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
                BinXmlValueFormatter.FormatGuid(valueBytes, ref vsb);
                break;
            }

            case BinXmlValueType.SizeT:
            {
                vsb.Append("0x");
                if (size == 8)
                    vsb.AppendFormatted(MemoryMarshal.Read<ulong>(valueBytes), "x16");
                else
                    vsb.AppendFormatted(MemoryMarshal.Read<uint>(valueBytes), "x8");
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
                // Render embedded BinXml into a temp VSB, then JSON-escape the result
                bool saved = _insideTemplateBody;
                _insideTemplateBody = false;
                ValueStringBuilder tempVsb = new(stackalloc char[256]);
                int embeddedChunkBase = fileOffset - _chunkFileOffset;
                int embeddedPos = 0;
                ParseTopLevelJson(valueBytes, ref embeddedPos, embeddedChunkBase, ref tempVsb);
                BinXmlValueFormatter.AppendJsonEscaped(ref vsb, tempVsb.AsSpan());
                tempVsb.Dispose();
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

    #endregion
}