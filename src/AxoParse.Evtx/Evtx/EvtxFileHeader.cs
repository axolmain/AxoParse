using System.Buffers.Binary;

namespace AxoParse.Evtx;

/// <summary>
/// Bit flags stored at offset 120 of the EVTX file header (4 bytes).
/// The file header indicates file state and integrity-check availability.
/// </summary>
[Flags]
public enum HeaderFlags : uint
{
    /// <summary>
    /// No specific flag — file is clean and not full.
    /// </summary>
    None = 0x0,

    /// <summary>
    /// Log was not closed cleanly (dirty shutdown/crash). Existing records are valid, 
    /// but the log may be truncated at the end.
    /// </summary>
    Dirty = 0x1,

    /// <summary>
    /// Log has reached its configured maximum size.
    /// </summary>
    Full = 0x2,

    /// <summary>
    /// Header CRC32 checksum is not present or should not be validated.
    /// </summary>
    NoCrc32 = 0x4,
}

/// <summary>
/// The file header occupies the first 4,096 bytes. It identifies the file as EVTX, tracks which chunks
/// and record IDs are in use, and stores a CRC32 checksum of the first 120 bytes (0x00–0x77) for
/// integrity validation. The Dirty flag indicates unclean shutdown; the Full flag indicates the log
/// reached its maximum size.
/// </summary>
/// <param name="FirstChunkNumber">Offset 8, 8 bytes. Number of the oldest chunk in the file.</param>
/// <param name="LastChunkNumber">Offset 16, 8 bytes. Number of the most recent chunk in the file.</param>
/// <param name="NextRecordIdentifier">Offset 24, 8 bytes. Next event record identifier to be assigned.</param>
/// <param name="HeaderSize">Offset 32, 4 bytes. Always 128.</param>
/// <param name="MinorFormatVersion">Offset 36, 2 bytes. Minor format version (e.g. 1 for v3.1).</param>
/// <param name="MajorFormatVersion">Offset 38, 2 bytes. Major format version (e.g. 3 for v3.1).</param>
/// <param name="HeaderBlockSize">Offset 40, 2 bytes. Always 4096 (chunk data offset).</param>
/// <param name="NumberOfChunks">Offset 42, 2 bytes. Number of chunks in the file.</param>
/// <param name="FileFlags">Offset 120, 4 bytes. Dirty/Full/NoCrc32 flags.</param>
/// <param name="Checksum">Offset 124, 4 bytes. CRC32 of the first 120 bytes of the header.</param>
public readonly record struct EvtxFileHeader(
    ulong FirstChunkNumber,
    ulong LastChunkNumber,
    ulong NextRecordIdentifier,
    uint HeaderSize,
    ushort MinorFormatVersion,
    ushort MajorFormatVersion,
    ushort HeaderBlockSize,
    ushort NumberOfChunks,
    HeaderFlags FileFlags,
    uint Checksum)
{
    /// <summary>
    /// Parses the first 128 bytes of a raw EVTX file into an <see cref="EvtxFileHeader"/>.
    /// Validates the 8-byte "ElfFile\0" magic signature at offset 0.
    /// </summary>
    /// <param name="data">Raw file bytes; must be at least 128 bytes long.</param>
    /// <returns>A populated <see cref="EvtxFileHeader"/> with all header fields.</returns>
    public static EvtxFileHeader ParseEvtxFileHeader(ReadOnlySpan<byte> data)
    {
        if (data.Length < 128)
            throw new InvalidDataException("EVTX header too short");

        if (!data[..8].SequenceEqual("ElfFile\0"u8))
            throw new InvalidDataException("Invalid EVTX signature");

        return new EvtxFileHeader(
            FirstChunkNumber: BinaryPrimitives.ReadUInt64LittleEndian(data[8..]),
            LastChunkNumber: BinaryPrimitives.ReadUInt64LittleEndian(data[16..]),
            NextRecordIdentifier: BinaryPrimitives.ReadUInt64LittleEndian(data[24..]),
            HeaderSize: BinaryPrimitives.ReadUInt32LittleEndian(data[32..]),
            MinorFormatVersion: BinaryPrimitives.ReadUInt16LittleEndian(data[36..]),
            MajorFormatVersion: BinaryPrimitives.ReadUInt16LittleEndian(data[38..]),
            HeaderBlockSize: BinaryPrimitives.ReadUInt16LittleEndian(data[40..]),
            NumberOfChunks: BinaryPrimitives.ReadUInt16LittleEndian(data[42..]),
            FileFlags: (HeaderFlags)BinaryPrimitives.ReadUInt32LittleEndian(data[120..]),
            Checksum: BinaryPrimitives.ReadUInt32LittleEndian(data[124..])
        );
    }
}