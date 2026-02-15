namespace AxoParse.Evtx;

/// <summary>
/// <see href="https://wiki.osdev.org/CRC32">CRC32</see> implementation using the standard IEEE 802.3 polynomial (0xEDB88320, reflected).
/// Used for validating EVTX file header and chunk header/data checksums. 
/// </summary>
internal static class Crc32
{
    /// <summary>
    /// Precomputed 256-entry CRC32 lookup table using the reflected IEEE 802.3 polynomial (0xEDB88320).
    /// </summary>
    private static readonly uint[] Table = InitTable();

    /// <summary>
    /// Builds the 256-entry CRC32 lookup table by computing the remainder for each possible byte value.
    /// </summary>
    /// <returns>The populated 256-entry lookup table.</returns>
    private static uint[] InitTable()
    {
        uint[] table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint crc = i;
            for (int j = 0; j < 8; j++)
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            table[i] = crc;
        }
        return table;
    }

    /// <summary>
    /// Computes CRC32 over the given data span.
    /// </summary>
    /// <param name="data">Input bytes to checksum.</param>
    /// <returns>The CRC32 value.</returns>
    public static uint Compute(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFFu;
        for (int i = 0; i < data.Length; i++)
            crc = Table[(byte)(crc ^ data[i])] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFFu;
    }
}