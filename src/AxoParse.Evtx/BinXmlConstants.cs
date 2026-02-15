using System.Runtime.InteropServices;

namespace AxoParse.Evtx;

/// <summary>
/// BinXml token types. Const bytes instead of enums to avoid cast overhead.
/// </summary>
internal static class BinXmlToken
{
    /// <summary>
    /// End of file.
    /// </summary>
    public const byte Eof = 0x00;

    /// <summary>
    /// Open start element tag.
    /// Indicates the start of a start element, correlates to '&lt;' in '&lt;Event&gt;'.
    /// </summary>
    public const byte OpenStartElement = 0x01;

    /// <summary>
    /// Close start element tag.
    /// Indicates the end of a start element, correlates to '&gt;' in '&lt;Event&gt;'.
    /// </summary>
    public const byte CloseStartElement = 0x02;

    /// <summary>
    /// Close empty element tag.
    /// Indicates the end of a start element, correlates to '/&gt;' in '&lt;Event/&gt;'.
    /// </summary>
    public const byte CloseEmptyElement = 0x03;

    /// <summary>
    /// Close end element tag.
    /// Indicates the end of element, correlates to '&lt;/Event&gt;'.
    /// </summary>
    public const byte EndElement = 0x04;

    /// <summary>
    /// Value (0x05 / 0x45 with has-more-data flag).
    /// </summary>
    public const byte Value = 0x05;

    /// <summary>
    /// Attribute (0x06 / 0x46 with has-more-data flag).
    /// </summary>
    public const byte Attribute = 0x06;

    /// <summary>
    /// CDATA section (0x07 / 0x47 with has-more-data flag).
    /// </summary>
    public const byte CDataSection = 0x07;

    /// <summary>
    /// Character entity reference (0x08 / 0x48 with has-more-data flag).
    /// </summary>
    public const byte CharRef = 0x08;

    /// <summary>
    /// Entity reference (0x09 / 0x49 with has-more-data flag).
    /// </summary>
    public const byte EntityRef = 0x09;

    /// <summary>
    /// Processing instructions (PI) target.
    /// XML processing instructions.
    /// </summary>
    public const byte PiTarget = 0x0A;

    /// <summary>
    /// Processing instructions (PI) data.
    /// XML processing instructions.
    /// </summary>
    public const byte PiData = 0x0B;

    /// <summary>
    /// Template instance.
    /// </summary>
    public const byte TemplateInstance = 0x0C;

    /// <summary>
    /// Normal substitution.
    /// </summary>
    public const byte NormalSubstitution = 0x0D;

    /// <summary>
    /// Optional substitution.
    /// </summary>
    public const byte OptionalSubstitution = 0x0E;

    /// <summary>
    /// Fragment header token.
    /// </summary>
    public const byte FragmentHeader = 0x0F;

    /// <summary>
    /// Flag OR'd with base token to indicate has-more-data variant (0x40).
    /// </summary>
    public const byte HasMoreDataFlag = 0x40;
}

/// <summary>
/// BinXml value types for substitution values.
/// </summary>
internal static class BinXmlValueType
{
    /// <summary>
    /// NULL or empty.
    /// </summary>
    public const byte Null = 0x00;

    /// <summary>
    /// Unicode string.
    /// Stored as UTF-16 little-endian without an end-of-string character.
    /// </summary>
    public const byte String = 0x01;

    /// <summary>
    /// ASCII string.
    /// Stored using a codepage without an end-of-string character.
    /// </summary>
    public const byte AnsiString = 0x02;

    /// <summary>
    /// 8-bit integer signed.
    /// </summary>
    public const byte Int8 = 0x03;

    /// <summary>
    /// 8-bit integer unsigned.
    /// </summary>
    public const byte UInt8 = 0x04;

    /// <summary>
    /// 16-bit integer signed.
    /// </summary>
    public const byte Int16 = 0x05;

    /// <summary>
    /// 16-bit integer unsigned.
    /// </summary>
    public const byte UInt16 = 0x06;

    /// <summary>
    /// 32-bit integer signed.
    /// </summary>
    public const byte Int32 = 0x07;

    /// <summary>
    /// 32-bit integer unsigned.
    /// </summary>
    public const byte UInt32 = 0x08;

    /// <summary>
    /// 64-bit integer signed.
    /// </summary>
    public const byte Int64 = 0x09;

    /// <summary>
    /// 64-bit integer unsigned.
    /// </summary>
    public const byte UInt64 = 0x0A;

    /// <summary>
    /// Floating point 32-bit (single precision).
    /// </summary>
    public const byte Float = 0x0B;

    /// <summary>
    /// Floating point 64-bit (double precision).
    /// </summary>
    public const byte Double = 0x0C;

    /// <summary>
    /// Boolean.
    /// A 32-bit integer that MUST be 0x00 or 0x01.
    /// </summary>
    public const byte Bool = 0x0D;

    /// <summary>
    /// Binary data.
    /// </summary>
    public const byte Binary = 0x0E;

    /// <summary>
    /// GUID.
    /// Stored in little-endian.
    /// </summary>
    public const byte Guid = 0x0F;

    /// <summary>
    /// Size type.
    /// Either 32 or 64-bits. Should be paired with HexInt32 or HexInt64.
    /// </summary>
    public const byte SizeT = 0x10;

    /// <summary>
    /// FILETIME (64-bit).
    /// Stored in little-endian.
    /// </summary>
    public const byte FileTime = 0x11;

    /// <summary>
    /// System time (128-bit).
    /// Stored in little-endian.
    /// </summary>
    public const byte SystemTime = 0x12;

    /// <summary>
    /// NT Security Identifier (SID).
    /// </summary>
    public const byte Sid = 0x13;

    /// <summary>
    /// 32-bit integer hexadecimal.
    /// 32-bit unsigned integer represented in hexadecimal notation.
    /// </summary>
    public const byte HexInt32 = 0x14;

    /// <summary>
    /// 64-bit integer hexadecimal.
    /// 64-bit unsigned integer represented in hexadecimal notation.
    /// </summary>
    public const byte HexInt64 = 0x15;

    /// <summary>
    /// EvtHandle. Usage unknown.
    /// </summary>
    public const byte EvtHandle = 0x20;

    /// <summary>
    /// Binary XML fragment.
    /// </summary>
    public const byte BinXml = 0x21;

    /// <summary>
    /// EvtXml. Usage unknown.
    /// </summary>
    public const byte EvtXml = 0x23;

    /// <summary>
    /// Flag OR'd with base type to indicate an array (0x80).
    /// Binary data and binary XML fragment types are not supported as arrays.
    /// </summary>
    public const byte ArrayFlag = 0x80;
}

/// <summary>
/// On-disk substitution descriptor layout: 2-byte size, 1-byte type, 1-byte padding.
/// Zero-copy readable via MemoryMarshal.Cast.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal readonly struct SubstitutionDescriptor
{
    public readonly ushort Size;
    public readonly byte Type;
    public readonly byte Padding;
}