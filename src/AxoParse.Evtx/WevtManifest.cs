using System.Runtime.InteropServices;

namespace AxoParse.Evtx;

/// <summary>
/// Extracted WEVT template: the template GUID and its raw BinXML fragment bytes.
/// The BinXML uses WEVT name encoding (no nameOffset field, names always inline).
/// </summary>
/// <param name="Guid">Template identifier GUID from the TEMP header (offset 24, 16 bytes).</param>
/// <param name="BinXmlData">Raw BinXML bytes starting at offset 40 within the TEMP entry.</param>
internal readonly record struct WevtTemplate(Guid Guid, byte[] BinXmlData);

/// <summary>
/// Parses CRIM (instrumentation manifest) blobs extracted from PE WEVT_TEMPLATE resources.
/// Navigation: CRIM → provider descriptors → WEVT blocks → element descriptors → TTBL → TEMP entries.
/// All offsets within the blob are absolute (relative to CRIM start).
/// </summary>
internal static class WevtManifest
{
    /// <summary>
    /// CRIM header: 16 bytes.
    /// Offset 0: "CRIM" magic (4), size (4), major_version (2), minor_version (2), provider_count (4).
    /// </summary>
    private const int CrimHeaderSize = 16;

    /// <summary>
    /// Provider descriptor: 20 bytes.
    /// GUID (16) + data offset from CRIM start (4).
    /// </summary>
    private const int ProviderDescriptorSize = 20;

    /// <summary>
    /// WEVT provider header: 20 bytes.
    /// "WEVT" magic (4), size (4), message_id (4), descriptor_count (4), unknown2_count (4).
    /// </summary>
    private const int WevtHeaderSize = 20;

    /// <summary>
    /// Element descriptor within WEVT: 8 bytes.
    /// element_offset (4) + unknown (4).
    /// </summary>
    private const int ElementDescriptorSize = 8;

    /// <summary>
    /// TTBL header: 12 bytes.
    /// "TTBL" magic (4), size (4), count (4).
    /// </summary>
    private const int TtblHeaderSize = 12;

    /// <summary>
    /// TEMP header: 40 bytes.
    /// "TEMP" magic (4), size (4), item_descriptor_count (4), item_name_count (4),
    /// template_items_offset (4), event_type (4), GUID (16).
    /// BinXML starts at offset 40.
    /// </summary>
    private const int TempHeaderSize = 40;

    /// <summary>
    /// Parses a CRIM manifest blob and extracts all TEMP template definitions.
    /// Navigates: CRIM → providers → WEVT → element descriptors → TTBL → TEMP entries.
    /// </summary>
    /// <param name="crimData">Raw CRIM manifest bytes (from PE WEVT_TEMPLATE resource).</param>
    /// <returns>List of extracted templates with their GUIDs and BinXML data.</returns>
    internal static List<WevtTemplate> ParseCrimManifest(ReadOnlySpan<byte> crimData)
    {
        List<WevtTemplate> templates = new();

        if (crimData.Length < CrimHeaderSize)
            return templates;

        if (!crimData.Slice(0, 4).SequenceEqual("CRIM"u8))
            return templates;

        uint providerCount = MemoryMarshal.Read<uint>(crimData.Slice(12));

        int providerDescStart = CrimHeaderSize;
        for (uint p = 0; p < providerCount; p++)
        {
            int descOffset = providerDescStart + (int)(p * ProviderDescriptorSize);
            if (descOffset + ProviderDescriptorSize > crimData.Length)
                break;

            // Provider data offset is at bytes 16..20 of the descriptor (after the 16-byte GUID)
            uint wevtOffset = MemoryMarshal.Read<uint>(crimData.Slice(descOffset + 16));
            ParseWevtProvider(crimData, (int)wevtOffset, templates);
        }

        return templates;
    }

    /// <summary>
    /// Parses a WEVT provider block, scanning its element descriptors for TTBL entries.
    /// </summary>
    /// <param name="data">Full CRIM blob.</param>
    /// <param name="offset">Absolute offset of the WEVT header within <paramref name="data"/>.</param>
    /// <param name="templates">Accumulator for extracted templates.</param>
    private static void ParseWevtProvider(ReadOnlySpan<byte> data, int offset, List<WevtTemplate> templates)
    {
        if (offset + WevtHeaderSize > data.Length)
            return;

        if (!data.Slice(offset, 4).SequenceEqual("WEVT"u8))
            return;

        uint descriptorCount = MemoryMarshal.Read<uint>(data.Slice(offset + 12));

        int elemStart = offset + WevtHeaderSize;
        for (uint e = 0; e < descriptorCount; e++)
        {
            int elemDescOffset = elemStart + (int)(e * ElementDescriptorSize);
            if (elemDescOffset + ElementDescriptorSize > data.Length)
                break;

            uint elementOffset = MemoryMarshal.Read<uint>(data.Slice(elemDescOffset));

            if (elementOffset + 4 <= (uint)data.Length &&
                data.Slice((int)elementOffset, 4).SequenceEqual("TTBL"u8))
            {
                ParseTtbl(data, (int)elementOffset, templates);
            }
        }
    }

    /// <summary>
    /// Parses a TTBL (template table) block and extracts all TEMP entries.
    /// </summary>
    /// <param name="data">Full CRIM blob.</param>
    /// <param name="offset">Absolute offset of the TTBL header within <paramref name="data"/>.</param>
    /// <param name="templates">Accumulator for extracted templates.</param>
    private static void ParseTtbl(ReadOnlySpan<byte> data, int offset, List<WevtTemplate> templates)
    {
        if (offset + TtblHeaderSize > data.Length)
            return;

        uint count = MemoryMarshal.Read<uint>(data.Slice(offset + 8));

        int pos = offset + TtblHeaderSize;
        for (uint t = 0; t < count; t++)
        {
            if (pos + TempHeaderSize > data.Length)
                break;

            if (!data.Slice(pos, 4).SequenceEqual("TEMP"u8))
                break;

            uint tempSize = MemoryMarshal.Read<uint>(data.Slice(pos + 4));
            if (tempSize < TempHeaderSize || pos + (int)tempSize > data.Length)
                break;

            uint itemDescriptorCount = MemoryMarshal.Read<uint>(data.Slice(pos + 8));
            uint templateItemsOffset = MemoryMarshal.Read<uint>(data.Slice(pos + 16));
            Guid guid = MemoryMarshal.Read<Guid>(data.Slice(pos + 24));

            // BinXML starts at byte 40 within the TEMP entry (after the header).
            // templateItemsOffset is absolute (from CRIM start) — convert to relative within the TEMP entry.
            // BinXML ends where the item descriptor table begins, or at end-of-TEMP if no items.
            int binXmlStart = pos + TempHeaderSize;
            int binXmlEnd;
            if (itemDescriptorCount > 0 && templateItemsOffset > (uint)pos)
            {
                // Convert absolute CRIM offset to absolute data offset, clamped to TEMP boundary
                int itemsRel = (int)(templateItemsOffset - (uint)pos);
                binXmlEnd = pos + Math.Min(itemsRel, (int)tempSize);
            }
            else
            {
                binXmlEnd = pos + (int)tempSize;
            }

            int binXmlLen = binXmlEnd - binXmlStart;
            if (binXmlLen > 0)
            {
                byte[] binXml = data.Slice(binXmlStart, binXmlLen).ToArray();
                templates.Add(new WevtTemplate(guid, binXml));
            }

            pos += (int)tempSize;
        }
    }
}