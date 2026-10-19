using System.Runtime.InteropServices;

namespace AxoParse.Evtx.BinXml;

/// <summary>
/// Partial class containing the substitution value writer shared by every output format. A value's text is the same
/// in XML and JSON except for how strings are escaped, whether hex integers are zero-padded, and how embedded BinXml
/// is rendered; the format's writer (<see cref="IBinXmlWriter"/>) supplies those, so the type switch exists once.
/// </summary>
internal sealed partial class BinXmlParser
{
    #region Non-Public Methods

    /// <summary>
    /// Writes a substitution value's text (no surrounding quotes: JSON quotes are part of the static output).
    /// Dispatches on the BinXml value type (see <see cref="BinXmlValueType"/>); bit 0x80 marks an array.
    /// </summary>
    /// <typeparam name="TFormat">Output format.</typeparam>
    /// <param name="size">Byte size of the value data.</param>
    /// <param name="valueType">BinXml value type code.</param>
    /// <param name="fileOffset">Absolute byte offset of the value data within <see cref="_fileData"/>.</param>
    /// <param name="binxmlChunkBase">Chunk-relative base offset of the enclosing BinXml.</param>
    /// <param name="vsb">Output builder.</param>
    /// <param name="escaped">
    /// JSON only: the value sits inside an embedded BinXml (0x21) fragment that is itself emitted as a JSON string, so
    /// text values are escaped twice. Numeric, GUID, SID, time and hex output contains no JSON-special characters and is
    /// identical in both modes.
    /// </param>
    private void WriteValue<TFormat>(int size, byte valueType, int fileOffset, int binxmlChunkBase, ref ValueUtf8Builder vsb,
                                     bool escaped)
        where TFormat : struct, IBinXmlWriter
    {
        if (size == 0) return;
        ReadOnlySpan<byte> valueBytes = ChunkBytes(fileOffset, size);

        if ((valueType & BinXmlValueType.ArrayFlag) != 0)
        {
            WriteArray<TFormat>(valueBytes, (byte)(valueType & 0x7F), fileOffset, binxmlChunkBase, ref vsb, escaped);
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
                TFormat.AppendString(ref vsb, chars, escaped);
                break;
            }

            case BinXmlValueType.AnsiString:
                TFormat.AppendAnsiString(ref vsb, valueBytes, escaped);
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
                if (TFormat.PadsHexIntegers)
                    BinXmlValueFormatter.AppendHexUInt32Padded(ref vsb, MemoryMarshal.Read<uint>(valueBytes));
                else
                    BinXmlValueFormatter.AppendHexUInt32Min(ref vsb, MemoryMarshal.Read<uint>(valueBytes));
                break;

            case BinXmlValueType.HexInt64:
                vsb.Append("0x"u8);
                if (TFormat.PadsHexIntegers)
                    BinXmlValueFormatter.AppendHexUInt64Padded(ref vsb, MemoryMarshal.Read<ulong>(valueBytes));
                else
                    BinXmlValueFormatter.AppendHexUInt64Min(ref vsb, MemoryMarshal.Read<ulong>(valueBytes));
                break;

            case BinXmlValueType.BinXml:
                TFormat.WriteEmbeddedBinXml(this, valueBytes, fileOffset - _chunkFileOffset, ref vsb, escaped);
                break;

            case BinXmlValueType.EvtHandle:
            case BinXmlValueType.EvtXml:
            default:
                BinXmlValueFormatter.AppendHex(ref vsb, valueBytes);
                break;
        }
    }

    /// <summary>
    /// Writes an array-typed value (type code has bit 0x80 set) as <c>", "</c>-separated elements. String arrays
    /// (base type 0x01) are NUL-terminated UTF-16LE strings back to back, empty strings skipped; fixed-size types are
    /// split by element size; anything else is written as hex.
    /// </summary>
    /// <typeparam name="TFormat">Output format.</typeparam>
    /// <param name="valueBytes">Raw value bytes containing the array data.</param>
    /// <param name="baseType">Base BinXml value type (with array flag 0x80 masked off).</param>
    /// <param name="fileOffset">Absolute byte offset of the value data within <see cref="_fileData"/>.</param>
    /// <param name="binxmlChunkBase">Chunk-relative base offset for nested value rendering.</param>
    /// <param name="vsb">Output builder.</param>
    /// <param name="escaped">JSON only: escape string elements twice (see <see cref="WriteValue{TFormat}"/>).</param>
    private void WriteArray<TFormat>(ReadOnlySpan<byte> valueBytes, byte baseType, int fileOffset, int binxmlChunkBase,
                                     ref ValueUtf8Builder vsb, bool escaped)
        where TFormat : struct, IBinXmlWriter
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
                        TFormat.AppendString(ref vsb, chars.Slice(start, i - start), escaped);
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
                WriteValue<TFormat>(elemSize, baseType, fileOffset + i, binxmlChunkBase, ref vsb, escaped);
                first = false;
            }
            return;
        }

        BinXmlValueFormatter.AppendHex(ref vsb, valueBytes);
    }

    #endregion
}
