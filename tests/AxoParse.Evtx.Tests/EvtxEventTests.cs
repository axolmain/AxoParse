using AxoParse.Evtx.Evtx;

namespace AxoParse.Evtx.Tests;

public class EvtxEventTests
{
    #region Public Methods

    /// <summary>
    /// Verifies that IsSuccess returns false when a diagnostic message is present.
    /// </summary>
    [Fact]
    public void IsSuccessReturnsFalseWhenDiagnosticPresent()
    {
        EvtxEvent evt = new EvtxEvent(
            Record: default,
            Xml: string.Empty,
            Json: ReadOnlyMemory<byte>.Empty,
            Diagnostic: "BinXml render failed");

        Assert.False(evt.IsSuccess);
    }

    /// <summary>
    /// Verifies that IsSuccess returns true when Diagnostic is null (successful render).
    /// </summary>
    [Fact]
    public void IsSuccessReturnsTrueWhenNoDiagnostic()
    {
        EvtxEvent evt = new EvtxEvent(
            Record: default,
            Xml: "<Event/>",
            Json: ReadOnlyMemory<byte>.Empty,
            Diagnostic: null);

        Assert.True(evt.IsSuccess);
    }

    #endregion
}