using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using AxoParse.Evtx.Evtx;

namespace AxoParse.Evtx.BinXml;

/// <summary>
/// Partial class containing compiled-template JSON writing methods.
/// Converts BinXml token streams to structural JSON output using the format:
/// {"#name":"...","#attrs":{...},"#content":[...]}.
/// All substitution values are rendered as JSON-escaped strings.
/// Uses compiled templates for fast output when possible; other content goes through the shared decoder with
/// <see cref="JsonWriter"/>.
/// </summary>
internal sealed partial class BinXmlParser
{
    #region Non-Public Methods

    /// <summary>
    /// JSON variant of <see cref="ParseTemplateInstance"/>. Uses <see cref="ReadTemplateInstanceData"/>
    /// for shared data extraction, then checks the compiled JSON cache for fast output.
    /// A template that can't be compiled is decoded directly by <see cref="DecodeContent{TWriter}"/>.
    /// </summary>
    /// <param name="data">BinXml byte stream.</param>
    /// <param name="pos">Current read position; advanced past the entire template instance.</param>
    /// <param name="binxmlChunkBase">Chunk-relative base offset of <paramref name="data"/>.</param>
    /// <param name="vsb">String builder that receives the JSON output.</param>
    /// <param name="escaped">When true, emit the instance JSON-string-escaped (embedded BinXml 0x21 context).</param>
    private void ParseTemplateInstanceJson(ReadOnlySpan<byte> data, ref int pos, int binxmlChunkBase,
                                           ref ValueUtf8Builder vsb, bool escaped = false)
    {
        // Peek at numValues to size the stackalloc buffers
        int peekPos = pos + 6;
        uint defDataOffset = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(peekPos, 4));
        peekPos += 4;
        uint peekChunkRel = (uint)(binxmlChunkBase + peekPos);
        if (defDataOffset == peekChunkRel)
        {
            uint inlineDataSize = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(peekPos + 20, 4));
            peekPos += 24 + (int)inlineDataSize;
        }
        int numVals = ReadSubstitutionCount(data, peekPos);

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

        // Check compiled JSON cache
        CompiledJsonTemplate? compiled = null;
        bool compileFailed = false;
        if (embedded && (_compiledJsonCache != null))
        {
            if (!_compiledJsonCache.TryGetValue(hdr.TemplateGuid, out compiled))
            {
                compiled = CompileProgram((int)hdr.DefDataOffset, (int)hdr.DataSize)?.LowerToJson();
                _compiledJsonCache[hdr.TemplateGuid] = compiled;
            }
            compileFailed = compiled == null;
        }

        // An embedded template that is missing, runs past its chunk, or won't compile falls back to the provider
        // template with the same GUID, as in the Rust evtx crate; a usable embedded template is never replaced
        if ((!embedded || compileFailed) && (_wevtCache?.GetJson(hdr.TemplateGuid) is { } provider))
            compiled = provider;

        if (compiled != null)
        {
            if (!escaped)
            {
                WriteCompiledJson(compiled, offsets, sizes, types, binxmlChunkBase, ref vsb);
            }
            else if (compiled.EscapedUtf8Parts != null)
            {
                WriteCompiledJson(compiled, offsets, sizes, types, binxmlChunkBase, ref vsb, escaped: true);
            }
            else
            {
                // Parts contain surrogates, so per-part escaping is not exact: render, then escape the whole
                ValueUtf8Builder tempVsb = new(stackalloc byte[512]);
                WriteCompiledJson(compiled, offsets, sizes, types, binxmlChunkBase, ref tempVsb);
                BinXmlValueFormatter.AppendJsonEscapedUtf8(ref vsb, tempVsb.AsSpan());
                tempVsb.Dispose();
            }
            return;
        }

        if (!embedded) return;

        // Not compilable: decode the template body directly with this instance's substitution values
        ReadOnlySpan<byte> tplBody = _fileData.AsSpan(tplBodyFileOffset, (int)hdr.DataSize);
        int tplPos = 0;
        int tplChunkBase = (int)hdr.DefDataOffset + 24;

        if ((tplBody.Length >= 4) && (tplBody[0] == BinXmlToken.FragmentHeader))
            tplPos += 4;

        JsonWriter writer = default;
        SubstitutionValues values = new(offsets, sizes, types);
        if (!escaped)
        {
            DecodeContent(tplBody, ref tplPos, values, tplChunkBase, ref vsb, ref writer, hasDepId: true, depth: 0,
                inAttr: false);
            return;
        }

        ValueUtf8Builder escapeVsb = new(stackalloc byte[512]);
        DecodeContent(tplBody, ref tplPos, values, tplChunkBase, ref escapeVsb, ref writer, hasDepId: true, depth: 0,
            inAttr: false);
        BinXmlValueFormatter.AppendJsonEscapedUtf8(ref vsb, escapeVsb.AsSpan());
        escapeVsb.Dispose();
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
    /// <param name="escaped">
    /// When true, emit <see cref="CompiledJsonTemplate.EscapedUtf8Parts"/> (must be non-null) and double-escaped values
    /// (embedded BinXml 0x21 context).
    /// </param>
    private void WriteCompiledJson(CompiledJsonTemplate compiled,
                                   scoped ReadOnlySpan<int> valueOffsets, scoped ReadOnlySpan<int> valueSizes,
                                   scoped ReadOnlySpan<byte> valueTypes,
                                   int binxmlChunkBase, ref ValueUtf8Builder vsb, bool escaped = false)
    {
        string[] parts = compiled.Parts;
        byte[]?[] utf8Parts = escaped ? compiled.EscapedUtf8Parts! : compiled.Utf8Parts;
        CompiledTemplate.AppendPart(ref vsb, utf8Parts[0], parts[0]);
        for (int i = 0; i < compiled.SubIds.Length; i++)
        {
            int subId = compiled.SubIds[i];
            if (subId < valueOffsets.Length)
            {
                byte valType = valueTypes[subId];
                int valSize = valueSizes[subId];
                if (!compiled.IsOptional[i] || ((valType != BinXmlValueType.Null) && (valSize > 0)))
                {
                    WriteJsonValue(valSize, valType, valueOffsets[subId], binxmlChunkBase, ref vsb, escaped);
                }
            }

            CompiledTemplate.AppendPart(ref vsb, utf8Parts[i + 1], parts[i + 1]);
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
    /// <param name="escaped">When true, string elements are escaped twice (embedded BinXml 0x21 context).</param>
    private void WriteJsonArray(ReadOnlySpan<byte> valueBytes, byte baseType, int fileOffset,
                                int binxmlChunkBase, ref ValueUtf8Builder vsb, bool escaped)
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
                        if (!first) vsb.Append(", "u8);
                        if (escaped)
                            BinXmlValueFormatter.AppendJsonEscapedTwice(ref vsb, chars.Slice(start, i - start));
                        else
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
                if (!first) vsb.Append(", "u8);
                WriteJsonValue(elemSize, baseType, fileOffset + i, binxmlChunkBase, ref vsb, escaped);
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
    /// For embedded BinXml (0x21), recursively renders via <see cref="DecodeTopLevel{TWriter}"/> and
    /// JSON-escapes the result.
    /// </summary>
    /// <param name="size">Byte size of the value data.</param>
    /// <param name="valueType">BinXml value type code. Bit 0x80 indicates an array.</param>
    /// <param name="fileOffset">Absolute byte offset of the value data within <see cref="_fileData"/>.</param>
    /// <param name="binxmlChunkBase">Chunk-relative base offset for embedded BinXml (type 0x21) resolution.</param>
    /// <param name="vsb">String builder that receives the rendered text (no surrounding quotes).</param>
    /// <param name="escaped">
    /// When true, the value sits inside an embedded BinXml (0x21) fragment that is itself emitted as a JSON string,
    /// so text values are escaped twice. Numeric, GUID, SID, time and hex output contains no JSON-special
    /// characters and is identical in both modes.
    /// </param>
    private void WriteJsonValue(int size, byte valueType, int fileOffset, int binxmlChunkBase, ref ValueUtf8Builder vsb,
                                bool escaped = false)
    {
        if (size == 0) return;
        ReadOnlySpan<byte> valueBytes = ChunkBytes(fileOffset, size);

        // Array flag — render as comma-separated values
        if ((valueType & BinXmlValueType.ArrayFlag) != 0)
        {
            WriteJsonArray(valueBytes, (byte)(valueType & 0x7F), fileOffset, binxmlChunkBase, ref vsb, escaped);
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
                if (escaped)
                    BinXmlValueFormatter.AppendJsonEscapedTwice(ref vsb, chars);
                else
                    BinXmlValueFormatter.AppendJsonEscaped(ref vsb, chars);
                break;
            }

            case BinXmlValueType.AnsiString when escaped:
            {
                // ANSI bytes map 1:1 to U+0000..U+00FF (no surrogates), so double escaping is per character
                for (int i = 0; i < valueBytes.Length; i++)
                {
                    byte b = valueBytes[i];
                    if (b == 0) break;
                    BinXmlValueFormatter.AppendJsonEscapedTwiceChar(ref vsb, (char)b);
                }
                break;
            }

            case BinXmlValueType.AnsiString:
                BinXmlValueFormatter.AppendAnsiJsonEscaped(ref vsb, valueBytes);
                break;

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
                BinXmlValueFormatter.AppendHexUInt32Padded(ref vsb, MemoryMarshal.Read<uint>(valueBytes));
                break;

            case BinXmlValueType.HexInt64:
                vsb.Append("0x"u8);
                BinXmlValueFormatter.AppendHexUInt64Padded(ref vsb, MemoryMarshal.Read<ulong>(valueBytes));
                break;

            case BinXmlValueType.BinXml:
            {
                // The fragment's JSON is emitted as a JSON string. Escaped mode writes it pre-escaped straight
                // into vsb (compiled templates use EscapedParts) instead of rendering to a temp buffer and escaping.
                int embeddedChunkBase = fileOffset - _chunkFileOffset;
                int embeddedPos = 0;
                if (!escaped)
                {
                    DecodeTopLevel<JsonWriter>(valueBytes, ref embeddedPos, embeddedChunkBase, ref vsb,
                        recordLevel: false, escaped: true);
                    break;
                }

                // Already one escape level deep: render the first level into a temp buffer, then escape again
                ValueUtf8Builder escapeVsb = new(stackalloc byte[512]);
                DecodeTopLevel<JsonWriter>(valueBytes, ref embeddedPos, embeddedChunkBase, ref escapeVsb,
                    recordLevel: false, escaped: true);
                BinXmlValueFormatter.AppendJsonEscapedUtf8(ref vsb, escapeVsb.AsSpan());
                escapeVsb.Dispose();
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