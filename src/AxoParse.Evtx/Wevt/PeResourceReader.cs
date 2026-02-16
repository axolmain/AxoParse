using PeNet;
using PeNet.Header.Pe;

namespace AxoParse.Evtx;

/// <summary>
/// Extracts WEVT_TEMPLATE resource data from PE binaries (DLLs/EXEs).
/// Windows event providers embed their template definitions as named PE resources
/// with the type string "WEVT_TEMPLATE". The resource data is a CRIM manifest blob
/// containing provider definitions, template tables, and BinXML fragments.
/// </summary>
internal static class PeResourceReader
{
    /// <summary>
    /// The PE resource type name used by Windows event providers to store template manifests.
    /// </summary>
    private const string WevtTemplateResourceName = "WEVT_TEMPLATE";

    /// <summary>
    /// Extracts raw WEVT_TEMPLATE resource bytes from a PE binary.
    /// Traverses the PE resource directory looking for a named resource type "WEVT_TEMPLATE",
    /// descends to the first data entry, and returns the raw bytes.
    /// </summary>
    /// <param name="peData">Complete PE file bytes.</param>
    /// <returns>Raw WEVT_TEMPLATE resource bytes (a CRIM manifest blob), or null if not found or PE is invalid.</returns>
    internal static byte[]? ExtractWevtTemplate(byte[] peData)
    {
        PeFile pe;
        try
        {
            pe = new PeFile(peData);
        }
        catch
        {
            return null;
        }

        ImageResourceDirectory? resourceDir = pe.ImageResourceDirectory;
        if (resourceDir is null)
            return null;

        // Root directory entries — find the one named "WEVT_TEMPLATE"
        List<ImageResourceDirectoryEntry?>? rootEntries = resourceDir.DirectoryEntries;
        ImageResourceDirectoryEntry? wevtEntry = FindNamedEntry(rootEntries, WevtTemplateResourceName);
        if (wevtEntry is null)
            return null;

        // Second level: resource ID entries (typically just one with ID=1)
        List<ImageResourceDirectoryEntry?>? idEntries = wevtEntry.ResourceDirectory?.DirectoryEntries;
        if (idEntries is null || idEntries.Count == 0)
            return null;

        ImageResourceDirectoryEntry? firstIdEntry = idEntries[0];
        if (firstIdEntry is null)
            return null;

        // Third level: language entries — take the first available
        List<ImageResourceDirectoryEntry?>? langEntries = firstIdEntry.ResourceDirectory?.DirectoryEntries;
        if (langEntries is null || langEntries.Count == 0)
            return null;

        ImageResourceDirectoryEntry? langEntry = langEntries[0];
        if (langEntry is null)
            return null;

        ImageResourceDataEntry? dataEntry = langEntry.ResourceDataEntry;
        if (dataEntry is null)
            return null;

        uint rva = dataEntry.OffsetToData;
        uint size = dataEntry.Size1;

        uint fileOffset = RvaToFileOffset(pe, rva);
        if (fileOffset == 0 || fileOffset + size > (uint)peData.Length)
            return null;

        return peData.AsSpan((int)fileOffset, (int)size).ToArray();
    }

    /// <summary>
    /// Finds a resource directory entry by its Unicode name string.
    /// Named entries have the high bit set on <see cref="ImageResourceDirectoryEntry.Name"/>;
    /// the lower 31 bits are the offset to a length-prefixed Unicode string in the resource section.
    /// PeNet exposes the resolved name via <see cref="ImageResourceDirectoryEntry.NameResolved"/>.
    /// </summary>
    /// <param name="entries">Root-level resource directory entries (nullable elements).</param>
    /// <param name="name">Resource type name to match (case-sensitive).</param>
    /// <returns>The matching entry, or null if not found.</returns>
    private static ImageResourceDirectoryEntry? FindNamedEntry(List<ImageResourceDirectoryEntry?>? entries, string name)
    {
        if (entries is null)
            return null;

        for (int i = 0; i < entries.Count; i++)
        {
            ImageResourceDirectoryEntry? entry = entries[i];
            if (entry is null) continue;
            // Named entries have the high bit set on the Name field
            if ((entry.Name & 0x80000000) != 0 && entry.NameResolved == name)
                return entry;
        }

        return null;
    }

    /// <summary>
    /// Converts a relative virtual address (RVA) to a raw file offset using section headers.
    /// </summary>
    /// <param name="pe">Parsed PE file.</param>
    /// <param name="rva">Relative virtual address to convert.</param>
    /// <returns>File offset, or 0 if the RVA does not fall within any section.</returns>
    private static uint RvaToFileOffset(PeFile pe, uint rva)
    {
        if (pe.ImageSectionHeaders is null)
            return 0;

        for (int i = 0; i < pe.ImageSectionHeaders.Length; i++)
        {
            ImageSectionHeader section = pe.ImageSectionHeaders[i];
            if (rva >= section.VirtualAddress && rva < section.VirtualAddress + section.VirtualSize)
                return rva - section.VirtualAddress + section.PointerToRawData;
        }

        return 0;
    }
}