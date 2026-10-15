namespace AxoParse.Evtx;

/// <summary>
/// <see href="https://wiki.osdev.org/CRC32">CRC32</see> implementation using the standard IEEE 802.3 polynomial (0xEDB88320, reflected).
/// Used for validating EVTX file header and chunk header/data checksums. 
/// </summary>
internal static class Crc32
{
    #region Public Methods

    /// <summary>
    /// Computes CRC32 over the given data span.
    /// </summary>
    /// <param name="data">Input bytes to checksum.</param>
    /// <returns>The CRC32 value.</returns>
    public static uint Compute(ReadOnlySpan<byte> data) => Update(0xFFFFFFFFu, data) ^ 0xFFFFFFFFu;

    /// <summary>
    /// Computes one CRC32 over two spans as if they were contiguous (the CRC state carries across, so this is not the
    /// same as combining two separate CRCs). Used for the chunk header checksum, which covers bytes 0-120 and 128-512.
    /// </summary>
    /// <param name="first">First range of input bytes.</param>
    /// <param name="second">Second range, checksummed as if it directly followed <paramref name="first"/>.</param>
    /// <returns>The CRC32 value of <paramref name="first"/> followed by <paramref name="second"/>.</returns>
    public static uint Compute(ReadOnlySpan<byte> first, ReadOnlySpan<byte> second) =>
        Update(Update(0xFFFFFFFFu, first), second) ^ 0xFFFFFFFFu;

    #endregion

    #region Non-Public Methods

    /// <summary>
    /// Advances a running (pre-inversion) CRC32 state over <paramref name="data"/>.
    /// </summary>
    /// <param name="crc">Running state; start from 0xFFFFFFFF.</param>
    /// <param name="data">Bytes to fold in.</param>
    /// <returns>The updated running state (XOR with 0xFFFFFFFF to finalise).</returns>
    private static uint Update(uint crc, ReadOnlySpan<byte> data)
    {
        for (int i = 0; i < data.Length; i++)
            crc = _table[(byte)(crc ^ data[i])] ^ (crc >> 8);
        return crc;
    }

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

    #endregion

    #region Non-Public Fields

    /// <summary>
    /// Precomputed 256-entry CRC32 lookup table using the reflected IEEE 802.3 polynomial (0xEDB88320).
    /// </summary>
    private static readonly uint[] _table = InitTable();

    #endregion
}