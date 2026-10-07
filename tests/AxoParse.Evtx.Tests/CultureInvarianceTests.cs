using System.Globalization;
using System.Text;
using AxoParse.Evtx.Evtx;

namespace AxoParse.Evtx.Tests;

public class CultureInvarianceTests
{
    #region Public Methods

    /// <summary>
    /// Verifies rendered XML and JSON do not depend on the current culture: parsing every sample under cultures with a
    /// comma decimal separator (de-DE) and a non-ASCII negative sign (sv-SE uses U+2212) must give the same bytes as
    /// parsing under the invariant culture, as the reference parser's output does.
    /// </summary>
    /// <param name="cultureName">Culture installed as current (and UI) culture for the comparison parse.</param>
    [Theory]
    [InlineData("de-DE")]
    [InlineData("sv-SE")]
    public void OutputIsIdenticalUnderAnyCulture(string cultureName)
    {
        CultureInfo original = CultureInfo.CurrentCulture;
        CultureInfo originalUi = CultureInfo.CurrentUICulture;
        try
        {
            foreach (string file in Directory.GetFiles(_testDataDir, "*.evtx"))
            {
                byte[] data = File.ReadAllBytes(file);
                foreach (OutputFormat format in new[] { OutputFormat.Xml, OutputFormat.Json })
                {
                    CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
                    CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;
                    byte[] invariant = Render(data, format);

                    CultureInfo.CurrentCulture = new CultureInfo(cultureName);
                    CultureInfo.CurrentUICulture = new CultureInfo(cultureName);
                    byte[] localised = Render(data, format);

                    Assert.True(invariant.AsSpan().SequenceEqual(localised),
                        $"{Path.GetFileName(file)} ({format}) renders differently under {cultureName}");
                }
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
            CultureInfo.CurrentUICulture = originalUi;
        }
    }

    /// <summary>
    /// Verifies numeric substitution values (negative integers, doubles, floats) are formatted with the invariant culture:
    /// '.' as decimal separator and ASCII '-' as negative sign, whatever the current culture. The sample corpus has no
    /// such values, so this covers the formatting path directly.
    /// </summary>
    /// <param name="cultureName">Culture installed as current culture while formatting.</param>
    [Theory]
    [InlineData("de-DE")]
    [InlineData("sv-SE")]
    public void NumbersFormatInvariantUnderAnyCulture(string cultureName)
    {
        CultureInfo original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo(cultureName);
            ValueUtf8Builder vub = new(new byte[64]);
            vub.AppendFormatted(-42);
            vub.Append((byte)' ');
            vub.AppendFormatted(-1.5d);
            vub.Append((byte)' ');
            vub.AppendFormatted(0.25f);
            string result = Encoding.UTF8.GetString(vub.AsSpan());
            vub.Dispose();

            Assert.Equal("-42 -1.5 0.25", result);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    #endregion

    #region Non-Public Methods

    /// <summary>
    /// Parses single-threaded on the calling thread's culture and concatenates every event's output as UTF-8.
    /// </summary>
    /// <param name="data">EVTX file bytes.</param>
    /// <param name="format">Output format.</param>
    /// <returns>Concatenated rendered output.</returns>
    private static byte[] Render(byte[] data, OutputFormat format)
    {
        using MemoryStream stream = new();
        EvtxParser.WriteTo(data, stream, format, maxThreads: 1, cancellationToken: TestContext.Current.CancellationToken);
        return stream.ToArray();
    }

    #endregion

    #region Non-Public Fields

    private static readonly string _testDataDir = TestPaths.TestDataDir;

    #endregion
}
