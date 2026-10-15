using System.Text;

namespace AxoParse.Evtx.Tests;

public class ValueUtf8BuilderTests
{
    #region Public Methods

    /// <summary>
    /// Verifies that any mix of raw UTF-16, isolated UTF-16 and ASCII appends produces exactly the bytes of building the
    /// same text as one UTF-16 string (isolated segments with unpaired surrogates already replaced by U+FFFD, as the
    /// escapers do) and encoding it once with <see cref="Encoding.UTF8"/>. Surrogate pairs split across raw appends must
    /// be joined, and lone surrogates at any boundary must become U+FFFD.
    /// </summary>
    [Fact]
    public void AppendsMatchEncodingTheConcatenatedString()
    {
        Random rng = new(4242);
        char[] pool = ['a', 'Z', ' ', '<', '"', 'é', '中', '�', '\uD800', '\uDBFF', '\uDC00', '\uDFFF', '\uD83D', '\uDE00'];
        for (int iteration = 0; iteration < 20000; iteration++)
        {
            StringBuilder reference = new();
            ValueUtf8Builder builder = new(new byte[8]);
            int segments = rng.Next(1, 8);
            for (int s = 0; s < segments; s++)
            {
                char[] chars = new char[rng.Next(0, 6)];
                for (int i = 0; i < chars.Length; i++)
                    chars[i] = rng.Next(5) == 0 ? (char)rng.Next(0, char.MaxValue + 1) : pool[rng.Next(pool.Length)];

                switch (rng.Next(3))
                {
                    case 0:
                        builder.AppendUtf16(chars);
                        reference.Append(chars);
                        break;
                    case 1:
                        builder.AppendUtf16Isolated(chars);
                        reference.Append(FixUnpairedSurrogates(chars));
                        break;
                    default:
                        byte ascii = (byte)rng.Next(0x20, 0x7F);
                        builder.Append(ascii);
                        reference.Append((char)ascii);
                        break;
                }
            }

            byte[] expected = Encoding.UTF8.GetBytes(reference.ToString());
            byte[] actual = builder.AsSpan().ToArray();
            builder.Dispose();
            Assert.True(expected.AsSpan().SequenceEqual(actual), $"Mismatch at iteration {iteration}");
        }
    }

    /// <summary>
    /// Verifies numbers are appended as invariant-culture ASCII and that growth past the initial buffer keeps content.
    /// </summary>
    [Fact]
    public void FormatsNumbersAndGrows()
    {
        ValueUtf8Builder builder = new(new byte[4]);
        builder.AppendFormatted(-42);
        builder.Append((byte)' ');
        builder.AppendFormatted(1.5d);
        builder.Append(" tail"u8);
        string result = Encoding.UTF8.GetString(builder.AsSpan());
        builder.Dispose();

        Assert.Equal("-42 1.5 tail", result);
    }

    #endregion

    #region Non-Public Methods

    /// <summary>
    /// Replaces unpaired surrogates with U+FFFD, keeping valid pairs — the fix-up the UTF-16 escapers apply.
    /// </summary>
    /// <param name="chars">Text to fix up.</param>
    /// <returns>The fixed-up text.</returns>
    private static string FixUnpairedSurrogates(char[] chars)
    {
        StringBuilder sb = new(chars.Length);
        for (int i = 0; i < chars.Length; i++)
        {
            char c = chars[i];
            if (char.IsHighSurrogate(c) && (i + 1 < chars.Length) && char.IsLowSurrogate(chars[i + 1]))
            {
                sb.Append(c).Append(chars[++i]);
            }
            else if (char.IsSurrogate(c))
            {
                sb.Append('�');
            }
            else
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }

    #endregion
}
