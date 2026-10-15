using System.Text;
using AxoParse.Evtx.BinXml;

namespace AxoParse.Evtx.Tests;

public class BinXmlValueFormatterTests
{
    #region Public Methods

    /// <summary>
    /// Verifies the single-pass double JSON escape matches escaping twice for every UTF-16 code unit on its own,
    /// including lone surrogates, so the embedded BinXml (0x21) streaming path stays byte-identical.
    /// </summary>
    [Fact]
    public void JsonEscapedTwiceMatchesDoubleEscapeForEveryChar()
    {
        for (int c = 0; c <= char.MaxValue; c++)
        {
            string text = ((char)c).ToString();
            Assert.Equal(EscapeTwiceReference(text), EscapeTwice(text));
        }
    }

    /// <summary>
    /// Verifies the single-pass double JSON escape matches escaping twice for surrogate pairs (valid, reversed,
    /// split by other characters) and for strings mixing escape characters with clean runs.
    /// </summary>
    /// <param name="text">Text to escape.</param>
    [Theory]
    [InlineData("\uD83D\uDE00")]
    [InlineData("\uDE00\uD83D")]
    [InlineData("a\uD83Db\uDE00c")]
    [InlineData("\uD83D\uD83D\uDE00")]
    [InlineData("clean text only")]
    [InlineData("\"C:\\Program Files\\App\" --x=<v>\r\n\t\u0001\u001F end")]
    [InlineData("prefix \uD83D\uDE00 \"quoted\" \uDFFF")]
    public void JsonEscapedTwiceMatchesDoubleEscapeForMixedStrings(string text)
    {
        Assert.Equal(EscapeTwiceReference(text), EscapeTwice(text));
    }

    /// <summary>
    /// Verifies the single-pass double JSON escape matches escaping twice on random strings drawn from
    /// escape characters, ASCII, surrogates and other BMP characters.
    /// </summary>
    [Fact]
    public void JsonEscapedTwiceMatchesDoubleEscapeForRandomStrings()
    {
        Random rng = new(12345);
        char[] pool = ['"', '\\', '\n', '\r', '\t', '\b', '\f', '\u0000', '\u001F', 'a', ' ', '\u00E9', '\uFFFD', '\uD800', '\uDBFF', '\uDC00', '\uDFFF'];
        for (int n = 0; n < 20000; n++)
        {
            char[] chars = new char[rng.Next(0, 40)];
            for (int i = 0; i < chars.Length; i++)
                chars[i] = rng.Next(4) == 0 ? (char)rng.Next(0, char.MaxValue + 1) : pool[rng.Next(pool.Length)];
            string text = new(chars);
            Assert.Equal(EscapeTwiceReference(text), EscapeTwice(text));
        }
    }

    /// <summary>
    /// Verifies escaping UTF-8 that was rendered into a temporary builder (as the JSON fallback paths do) gives the same
    /// bytes as JSON-escaping the original UTF-16 text, including unpaired surrogates and non-ASCII characters.
    /// </summary>
    [Fact]
    public void JsonEscapedUtf8MatchesEscapingTheUtf16Text()
    {
        Random rng = new(777);
        char[] pool = ['"', '\\', '\n', '\u0001', 'a', ' ', '\u00E9', '\u4E2D', '\uD800', '\uDC00', '\uD83D', '\uDE00'];
        for (int n = 0; n < 20000; n++)
        {
            char[] chars = new char[rng.Next(0, 30)];
            for (int i = 0; i < chars.Length; i++)
                chars[i] = pool[rng.Next(pool.Length)];

            ValueUtf8Builder rendered = new(new byte[8]);
            rendered.AppendUtf16(chars);
            ValueUtf8Builder viaUtf8 = new(new byte[8]);
            BinXmlValueFormatter.AppendJsonEscapedUtf8(ref viaUtf8, rendered.AsSpan());
            ValueUtf8Builder viaUtf16 = new(new byte[8]);
            BinXmlValueFormatter.AppendJsonEscaped(ref viaUtf16, chars);

            Assert.True(viaUtf16.AsSpan().SequenceEqual(viaUtf8.AsSpan()), $"Mismatch for iteration {n}");
            rendered.Dispose();
            viaUtf8.Dispose();
            viaUtf16.Dispose();
        }
    }

    #endregion

    #region Non-Public Methods

    /// <summary>
    /// Escapes <paramref name="text"/> with the single-pass <c>AppendJsonEscapedTwice</c>.
    /// </summary>
    /// <param name="text">Text to escape.</param>
    /// <returns>Double-escaped UTF-8 bytes as a hex string (for readable assertion failures).</returns>
    private static string EscapeTwice(string text)
    {
        ValueUtf8Builder vub = new(new byte[16]);
        BinXmlValueFormatter.AppendJsonEscapedTwice(ref vub, text);
        string result = Convert.ToHexString(vub.AsSpan());
        vub.Dispose();
        return result;
    }

    /// <summary>
    /// Escapes <paramref name="text"/> by applying <c>AppendJsonEscaped</c> twice. The first pass's output is valid
    /// UTF-16 once decoded (unpaired surrogates became U+FFFD), so decoding it for the second pass is lossless.
    /// </summary>
    /// <param name="text">Text to escape.</param>
    /// <returns>Double-escaped UTF-8 bytes as a hex string.</returns>
    private static string EscapeTwiceReference(string text)
    {
        ValueUtf8Builder once = new(new byte[16]);
        BinXmlValueFormatter.AppendJsonEscaped(ref once, text);
        string onceText = Encoding.UTF8.GetString(once.AsSpan());
        once.Dispose();

        ValueUtf8Builder twice = new(new byte[16]);
        BinXmlValueFormatter.AppendJsonEscaped(ref twice, onceText);
        string result = Convert.ToHexString(twice.AsSpan());
        twice.Dispose();
        return result;
    }

    #endregion
}
