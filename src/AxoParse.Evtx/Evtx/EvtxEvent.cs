using System;
using System.Collections.Generic;
using System.Linq;

namespace AxoParse.Evtx.Evtx;

/// <summary>
/// A single parsed EVTX event pairing record metadata with rendered output and optional diagnostics.
/// This is the primary consumption type — use <see cref="EvtxParser.GetEvents"/> to enumerate all events.
/// </summary>
/// <param name="Record">Raw EVTX record metadata (ID, timestamps, offsets).</param>
/// <param name="Xml">Rendered XML string. Populated when <see cref="OutputFormat.Xml"/> is used, otherwise <see cref="string.Empty"/>.</param>
/// <param name="Json">Rendered UTF-8 JSON bytes. Populated when <see cref="OutputFormat.Json"/> is used, otherwise empty.</param>
/// <param name="Diagnostic">Diagnostic message if BinXml rendering failed for this event; <c>null</c> on success.</param>
public readonly record struct EvtxEvent(
    EvtxRecord Record,
    string Xml,
    ReadOnlyMemory<byte> Json,
    string? Diagnostic)
{
    #region Properties

    /// <summary>
    /// True if the event rendered successfully without errors.
    /// </summary>
    public bool IsSuccess => Diagnostic is null;

    #endregion
}