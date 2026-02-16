using System;
using System.Collections.Generic;
using System.Linq;

namespace AxoParse.Evtx.Evtx;

/// <summary>
/// Identifies the specific validation failure that caused an <see cref="EvtxParseException"/>.
/// </summary>
public enum EvtxParseError
{
    /// <summary>
    /// File data is shorter than the 128-byte minimum required for a valid EVTX file header.
    /// </summary>
    FileHeaderTooShort,

    /// <summary>
    /// The 8-byte magic signature "ElfFile\0" was not found at the start of the file.
    /// </summary>
    InvalidFileSignature,

    /// <summary>
    /// Chunk data is shorter than the 512-byte minimum required for a valid chunk header.
    /// </summary>
    ChunkHeaderTooShort,

    /// <summary>
    /// The 8-byte magic signature "ElfChnk\0" was not found at the start of the chunk.
    /// </summary>
    InvalidChunkSignature,
}