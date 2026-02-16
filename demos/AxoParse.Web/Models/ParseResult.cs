namespace AxoParse.Web.Models;

/// <summary>
/// Aggregate result of parsing an EVTX file, including statistics and all extracted records.
/// </summary>
/// <param name="FileName">Original file name uploaded by the user.</param>
/// <param name="TotalRecords">Total number of event records found across all chunks.</param>
/// <param name="ChunkCount">Number of chunks in the EVTX file.</param>
/// <param name="ParseTimeMs">Wall-clock milliseconds taken to parse the file.</param>
/// <param name="Records">List of extracted event record views for table display.</param>
public readonly record struct ParseResult(
    string FileName,
    int TotalRecords,
    int ChunkCount,
    long ParseTimeMs,
    List<EventRecordView> Records);