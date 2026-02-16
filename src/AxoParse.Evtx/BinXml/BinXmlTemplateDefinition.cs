using System.Runtime.InteropServices;

namespace AxoParse.Evtx;

/// <summary>
/// Raw template definition header for zero-copy reads via MemoryMarshal.
/// Matches the on-disk little-endian layout at each template pointer location.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal readonly struct TemplateHeaderLayout
{
    /// <summary>
    /// Offset 0, 4 bytes — Next template definition offset in the hash chain. 0 if end of chain.
    /// </summary>
    public readonly uint NextTemplateOffset;

    /// <summary>
    /// Offset 4, 16 bytes — Template identifier GUID.
    /// </summary>
    public readonly Guid Guid;

    /// <summary>
    /// Offset 20, 4 bytes — Size of the template body (fragment header + element tree + EOF token).
    /// </summary>
    public readonly uint DataSize;
}

/// <summary>
/// A template definition as stored in the chunk, pointed to by the template pointer table.
/// The pointer table is a 32-entry chained hash table at chunk offset 384. Each entry is the
/// head of a linked list — follow NextTemplateOffset to find additional definitions in the
/// same hash bucket.
/// </summary>
/// <param name="DefDataOffset">Chunk-relative offset where this definition lives. Used as the cache key.</param>
/// <param name="NextTemplateOffset">Offset 0, 4 bytes — Next template definition offset in hash chain. 0 if end of chain.</param>
/// <param name="Guid">Offset 4, 16 bytes — Template identifier GUID.</param>
/// <param name="DataSize">Offset 20, 4 bytes — Size of the template body (fragment header + element tree + EOF token).</param>
/// <param name="DataFileOffset">Absolute file offset of the template body bytes.</param>
public readonly record struct BinXmlTemplateDefinition(
    uint DefDataOffset,
    uint NextTemplateOffset,
    Guid Guid,
    uint DataSize,
    int DataFileOffset)
{
    /// <summary>
    /// Returns the template body as a span into the original file buffer.
    /// </summary>
    public ReadOnlySpan<byte> GetData(byte[] fileData) =>
        fileData.AsSpan(DataFileOffset, (int)DataSize);

    /// <summary>
    /// Parses a template definition from chunk data at the given chunk-relative offset.
    /// Returns null if the definition header or body extends beyond the chunk boundary.
    /// </summary>
    public static BinXmlTemplateDefinition? ParseAt(ReadOnlySpan<byte> chunkData, uint offset, int chunkFileOffset)
    {
        if (offset + 24 > (uint)chunkData.Length) return null;
        TemplateHeaderLayout header = MemoryMarshal.Read<TemplateHeaderLayout>(chunkData[(int)offset..]);

        // Validate template body fits within chunk
        if (offset + 24 + header.DataSize > (uint)chunkData.Length) return null;

        return new BinXmlTemplateDefinition(
            DefDataOffset: offset,
            NextTemplateOffset: header.NextTemplateOffset,
            Guid: header.Guid,
            DataSize: header.DataSize,
            DataFileOffset: chunkFileOffset + (int)offset + 24
        );
    }

    /// <summary>
    /// Preloads all template definitions from a chunk's template pointer table,
    /// following hash chains. Returns a dictionary keyed by chunk-relative offset.
    /// </summary>
    public static Dictionary<uint, BinXmlTemplateDefinition> PreloadFromChunk(
        ReadOnlySpan<byte> chunkData, ReadOnlySpan<uint> templatePtrs, int chunkFileOffset)
    {
        int nonZeroCount = 0;
        for (int i = 0; i < templatePtrs.Length; i++)
            if (templatePtrs[i] != 0)
                nonZeroCount++;

        Dictionary<uint, BinXmlTemplateDefinition> cache =
            new Dictionary<uint, BinXmlTemplateDefinition>(nonZeroCount * 2);

        for (int i = 0; i < templatePtrs.Length; i++)
        {
            uint tplOffset = templatePtrs[i];

            while (tplOffset != 0)
            {
                if (cache.ContainsKey(tplOffset)) break; // already cached

                BinXmlTemplateDefinition? def = ParseAt(chunkData, tplOffset, chunkFileOffset);
                if (def == null) break;

                cache[tplOffset] = def.Value;
                tplOffset = def.Value.NextTemplateOffset;
            }
        }

        return cache;
    }
}