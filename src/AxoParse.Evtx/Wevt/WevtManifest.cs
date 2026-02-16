using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace AxoParse.Evtx.Wevt;

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
    #region Non-Public Methods

    /// <summary>
    /// Parses a CRIM manifest blob and extracts all TEMP template definitions.
    /// Navigates: CRIM → providers → WEVT → element descriptors → TTBL → TEMP entries.
    /// </summary>
    /// <param name="crimData">Raw CRIM manifest bytes (from PE WEVT_TEMPLATE resource).</param>
    /// <returns>List of extracted templates with their GUIDs and BinXML data.</returns>
    internal static List<WevtTemplate> ParseCrimManifest(ReadOnlySpan<byte> crimData)
    {
        List<WevtTemplate> templates = new();

        if (crimData.Length < _crimHeaderSize)
            return templates;

        if (!crimData[..4].SequenceEqual("CRIM"u8))
            return templates;

        uint providerCount = MemoryMarshal.Read<uint>(crimData[12..]);

        int providerDescStart = _crimHeaderSize;
        for (uint p = 0; p < providerCount; p++)
        {
            int descOffset = providerDescStart + (int)(p * _providerDescriptorSize);
            if (descOffset + _providerDescriptorSize > crimData.Length)
                break;

            // Provider data offset is at bytes 16..20 of the descriptor (after the 16-byte GUID)
            uint wevtOffset = MemoryMarshal.Read<uint>(crimData[(descOffset + 16)..]);
            ParseWevtProvider(crimData, (int)wevtOffset, templates);
        }

        return templates;
    }

    /// <summary>
    /// Parses a TTBL (template table) block and extracts all TEMP entries.
    /// </summary>
    /// <param name="data">Full CRIM blob.</param>
    /// <param name="offset">Absolute offset of the TTBL header within <paramref name="data"/>.</param>
    /// <param name="templates">Accumulator for extracted templates.</param>
    private static void ParseTtbl(ReadOnlySpan<byte> data, int offset, List<WevtTemplate> templates)
    {
        if (offset + _ttblHeaderSize > data.Length)
            return;

        uint count = MemoryMarshal.Read<uint>(data[(offset + 8)..]);

        int pos = offset + _ttblHeaderSize;
        for (uint t = 0; t < count; t++)
        {
            if (pos + _tempHeaderSize > data.Length)
                break;

            if (!data.Slice(pos, 4).SequenceEqual("TEMP"u8))
                break;

            uint tempSize = MemoryMarshal.Read<uint>(data[(pos + 4)..]);
            if ((tempSize < _tempHeaderSize) || (pos + (int)tempSize > data.Length))
                break;

            uint itemDescriptorCount = MemoryMarshal.Read<uint>(data[(pos + 8)..]);
            uint templateItemsOffset = MemoryMarshal.Read<uint>(data[(pos + 16)..]);
            Guid guid = MemoryMarshal.Read<Guid>(data[(pos + 24)..]);

            // BinXML starts at byte 40 within the TEMP entry (after the header).
            // templateItemsOffset is absolute (from CRIM start) — convert to relative within the TEMP entry.
            // BinXML ends where the item descriptor table begins, or at end-of-TEMP if no items.
            int binXmlStart = pos + _tempHeaderSize;
            int binXmlEnd;
            if ((itemDescriptorCount > 0) && (templateItemsOffset > (uint)pos))
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

    /// <summary>
    /// Parses a WEVT provider block, scanning its element descriptors for TTBL entries.
    /// </summary>
    /// <param name="data">Full CRIM blob.</param>
    /// <param name="offset">Absolute offset of the WEVT header within <paramref name="data"/>.</param>
    /// <param name="templates">Accumulator for extracted templates.</param>
    private static void ParseWevtProvider(ReadOnlySpan<byte> data, int offset, List<WevtTemplate> templates)
    {
        if (offset + _wevtHeaderSize > data.Length)
            return;

        if (!data.Slice(offset, 4).SequenceEqual("WEVT"u8))
            return;

        uint descriptorCount = MemoryMarshal.Read<uint>(data[(offset + 12)..]);

        int elemStart = offset + _wevtHeaderSize;
        for (uint e = 0; e < descriptorCount; e++)
        {
            int elemDescOffset = elemStart + (int)(e * _elementDescriptorSize);
            if (elemDescOffset + _elementDescriptorSize > data.Length)
                break;

            uint elementOffset = MemoryMarshal.Read<uint>(data[elemDescOffset..]);

            if ((elementOffset + 4 <= (uint)data.Length) &&
                data.Slice((int)elementOffset, 4).SequenceEqual("TTBL"u8))
            {
                ParseTtbl(data, (int)elementOffset, templates);
            }
        }
    }

    #endregion

    #region Non-Public Fields

    /// <summary>
    /// CRIM header: 16 bytes.
    /// Offset 0: "CRIM" magic (4), size (4), major_version (2), minor_version (2), provider_count (4).
    /// </summary>
    private const int _crimHeaderSize = 16;

    /// <summary>
    /// Element descriptor within WEVT: 8 bytes.
    /// element_offset (4) + unknown (4).
    /// </summary>
    private const int _elementDescriptorSize = 8;

    /// <summary>
    /// Provider descriptor: 20 bytes.
    /// GUID (16) + data offset from CRIM start (4).
    /// </summary>
    private const int _providerDescriptorSize = 20;

    /// <summary>
    /// TEMP header: 40 bytes.
    /// "TEMP" magic (4), size (4), item_descriptor_count (4), item_name_count (4),
    /// template_items_offset (4), event_type (4), GUID (16).
    /// BinXML starts at offset 40.
    /// </summary>
    private const int _tempHeaderSize = 40;

    /// <summary>
    /// TTBL header: 12 bytes.
    /// "TTBL" magic (4), size (4), count (4).
    /// </summary>
    private const int _ttblHeaderSize = 12;

    /// <summary>
    /// WEVT provider header: 20 bytes.
    /// "WEVT" magic (4), size (4), message_id (4), descriptor_count (4), unknown2_count (4).
    /// </summary>
    private const int _wevtHeaderSize = 20;

    #endregion
}