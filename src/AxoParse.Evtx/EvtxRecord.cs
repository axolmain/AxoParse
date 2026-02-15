using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace AxoParse.Evtx;

/// <summary>
/// Raw on-disk layout of the first 24 bytes of an EVTX event record header.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal readonly struct RecordHeaderLayout
{
    /// <summary>
    /// Record header magic signature (0x00002A2A)
    /// </summary>
    public readonly uint Signature;

    /// <summary>
    /// Total size of the event record in bytes (including header and trailing size copy)
    /// </summary>
    public readonly uint Size;

    /// <summary>
    /// Event record identifier
    /// </summary>
    public readonly ulong EventRecordId;

    /// <summary>
    /// FILETIME value representing when the record was written (i.e. logged)
    /// </summary>
    public readonly ulong WrittenTime;
}

/// <summary>
/// Parsed representation of a single EVTX event record.
/// </summary>
/// <param name="Size">Total record size in bytes (including header and trailing size copy).</param>
/// <param name="EventRecordId">Monotonically increasing event record identifier.</param>
/// <param name="WrittenTime">FILETIME timestamp indicating when the record was written.</param>
/// <param name="EventDataFileOffset">Absolute byte offset of the BinXml event data within the file buffer.</param>
/// <param name="EventDataLength">Length in bytes of the BinXml event data payload.</param>
/// <param name="SizeCopy">Trailing copy of <paramref name="Size"/> used for backward traversal integrity checks.</param>
public readonly record struct EvtxRecord(
    uint Size,
    ulong EventRecordId,
    ulong WrittenTime,
    int EventDataFileOffset,
    int EventDataLength,
    uint SizeCopy)
{
    /// <summary>
    /// Returns the event data as a span into the original file buffer.
    /// </summary>
    /// <param name="fileData">The complete EVTX file bytes.</param>
    /// <returns>A read-only span covering the BinXml event data for this record.</returns>
    public ReadOnlySpan<byte> GetEventData(byte[] fileData) => fileData.AsSpan(EventDataFileOffset, EventDataLength);

    /// <summary>
    /// Parses a record from a span into chunk data. Caller must verify record magic beforehand.
    /// Returns null if the record has an invalid size (corrupted/zero-data).
    /// </summary>
    /// <param name="data">Span starting at the record header within chunk data.</param>
    /// <param name="fileOffset">Absolute file offset corresponding to the start of <paramref name="data"/>.</param>
    /// <returns>A parsed record, or <c>null</c> if the record size is invalid.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static EvtxRecord? ParseEvtxRecord(ReadOnlySpan<byte> data, int fileOffset)
    {
        RecordHeaderLayout header = MemoryMarshal.Read<RecordHeaderLayout>(data);

        if (header.Size < 28 || header.Size > (uint)data.Length)
            return null;

        // Trailing size copy must match header size (EVTX integrity check)
        uint sizeCopy = MemoryMarshal.Read<uint>(data[(int)(header.Size - 4)..]);
        if (sizeCopy != header.Size)
            return null;

        int eventDataLength = (int)(header.Size - 28);

        return new EvtxRecord(
            Size: header.Size,
            EventRecordId: header.EventRecordId,
            WrittenTime: header.WrittenTime,
            EventDataFileOffset: fileOffset + 24,
            EventDataLength: eventDataLength,
            SizeCopy: sizeCopy
        );
    }
}