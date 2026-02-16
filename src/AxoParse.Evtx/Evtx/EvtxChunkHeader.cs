using System.Buffers.Binary;

namespace AxoParse.Evtx;

/// <summary>
/// The chunk header tracks which event records are contained within, provides fast-lookup caches for common strings
/// and templates, and stores CRC32 checksums. The header checksum covers bytes 0x00–0x77 (first 120 bytes). The event
/// records checksum covers all event record data in the chunk. The common string offset table allows the Binary XML
/// parser to resolve frequently-used element/attribute names without re-reading each record.
/// </summary>
/// <param name="FirstEventRecordNumber">Offset 8, 8 bytes — First event record number in this chunk.</param>
/// <param name="LastEventRecordNumber">Offset 16, 8 bytes — Last event record number in this chunk.</param>
/// <param name="FirstEventRecordId">Offset 24, 8 bytes — First event record identifier in this chunk.</param>
/// <param name="LastEventRecordId">Offset 32, 8 bytes — Last event record identifier in this chunk.</param>
/// <param name="HeaderSize">Offset 40, 4 bytes — Size of the chunk header (always 128).</param>
/// <param name="LastEventRecordDataOffset">Offset 44, 4 bytes — Chunk-relative offset of the last event record's data.</param>
/// <param name="FreeSpaceOffset">Offset 48, 4 bytes — Chunk-relative offset where free space begins (end of record data).</param>
/// <param name="EventRecordsChecksum">Offset 52, 4 bytes — CRC32 checksum over the event records data area (bytes 512..FreeSpaceOffset).</param>
/// <param name="Flags">Offset 120, 4 bytes — Chunk status flags (e.g., Dirty = 0x1, NoCrc32 = 0x4).</param>
/// <param name="Checksum">Offset 124, 4 bytes — CRC32 checksum of the header: CRC32(bytes[0..120]) XOR CRC32(bytes[128..512]).</param>
public readonly record struct EvtxChunkHeader(
    ulong FirstEventRecordNumber,
    ulong LastEventRecordNumber,
    ulong FirstEventRecordId,
    ulong LastEventRecordId,
    uint HeaderSize,
    uint LastEventRecordDataOffset,
    uint FreeSpaceOffset,
    uint EventRecordsChecksum,
    ChunkFlags Flags,
    uint Checksum)
{
    /// <summary>
    /// Parses the 512-byte chunk header, verifying the "ElfChnk\0" signature and reading
    /// record range, offsets, checksums, and flags from their respective positions.
    /// </summary>
    /// <param name="data">At least 512 bytes starting at the chunk boundary.</param>
    /// <returns>A populated <see cref="EvtxChunkHeader"/> value.</returns>
    public static EvtxChunkHeader ParseEvtxChunkHeader(ReadOnlySpan<byte> data)
    {
        if (data.Length < 512)
            throw new InvalidDataException("Chunk header too short");

        if (!data[..8].SequenceEqual("ElfChnk\0"u8))
            throw new InvalidDataException("Invalid Chunk signature");

        return new EvtxChunkHeader(
            FirstEventRecordNumber: BinaryPrimitives.ReadUInt64LittleEndian(data[8..]),
            LastEventRecordNumber: BinaryPrimitives.ReadUInt64LittleEndian(data[16..]),
            FirstEventRecordId: BinaryPrimitives.ReadUInt64LittleEndian(data[24..]),
            LastEventRecordId: BinaryPrimitives.ReadUInt64LittleEndian(data[32..]),
            HeaderSize: BinaryPrimitives.ReadUInt32LittleEndian(data[40..]),
            LastEventRecordDataOffset: BinaryPrimitives.ReadUInt32LittleEndian(data[44..]),
            FreeSpaceOffset: BinaryPrimitives.ReadUInt32LittleEndian(data[48..]),
            EventRecordsChecksum: BinaryPrimitives.ReadUInt32LittleEndian(data[52..]),
            Flags: (ChunkFlags)BinaryPrimitives.ReadUInt32LittleEndian(data[120..]),
            Checksum: BinaryPrimitives.ReadUInt32LittleEndian(data[124..])
        );
    }

    /// <summary>
    /// Validates the chunk header checksum: CRC32(bytes[0..120]) XOR CRC32(bytes[128..512]).
    /// </summary>
    /// <param name="chunkData">Full 64KB chunk data.</param>
    /// <returns>True if the computed checksum matches the stored value.</returns>
    public bool ValidateHeaderChecksum(ReadOnlySpan<byte> chunkData)
    {
        uint crc = Crc32.Compute(chunkData[..120]) ^ Crc32.Compute(chunkData[128..512]);
        return crc == Checksum;
    }

    /// <summary>
    /// Validates the event records data checksum: CRC32(bytes[512..FreeSpaceOffset]).
    /// </summary>
    /// <param name="chunkData">Full 64KB chunk data.</param>
    /// <returns>True if the computed checksum matches the stored value.</returns>
    public bool ValidateDataChecksum(ReadOnlySpan<byte> chunkData)
    {
        uint clampedEnd = Math.Min(FreeSpaceOffset, (uint)chunkData.Length);
        if (clampedEnd <= 512) return EventRecordsChecksum == 0;
        uint crc = Crc32.Compute(chunkData[512..(int)clampedEnd]);
        return crc == EventRecordsChecksum;
    }
}