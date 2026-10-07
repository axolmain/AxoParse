using System.Buffers;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Unicode;

namespace AxoParse.Evtx;

/// <summary>
/// ArrayPool-backed UTF-8 builder ref struct used to render records straight to UTF-8.
/// <para>
/// UTF-16 text is transcoded as it is appended. The bytes produced are exactly those of building the whole record as
/// a UTF-16 string and encoding it once with <see cref="System.Text.Encoding.UTF8"/> (unpaired surrogates become
/// U+FFFD, EF BF BD). To get that right across appends, a high surrogate ending one <see cref="AppendUtf16"/> call is
/// held back until the next append: if the next text starts with a low surrogate the two form one 4-byte sequence,
/// otherwise the held surrogate is written as U+FFFD.
/// </para>
/// Must be completed with <see cref="AsSpan"/> (which flushes a held surrogate) and disposed to return pooled buffers.
/// </summary>
internal ref struct ValueUtf8Builder : IDisposable
{
    #region Constructors And Destructors

    /// <summary>
    /// Initialises the builder with a caller-supplied buffer. No pooled array is rented until it is exhausted.
    /// </summary>
    /// <param name="initialBuffer">Initial backing store (stackalloc, a reused per-thread array, or a sink's span).</param>
    public ValueUtf8Builder(Span<byte> initialBuffer)
    {
        _arrayToReturnToPool = null;
        _bytes = initialBuffer;
        _pos = 0;
        _pendingHighSurrogate = '\0';
    }

    #endregion

    #region Properties

    /// <summary>
    /// Number of bytes written so far, excluding a held-back high surrogate.
    /// </summary>
    public int Length => _pos;

    /// <summary>
    /// True while the backing store is still the caller-supplied initial buffer.
    /// </summary>
    public bool IsInInitialBuffer => _arrayToReturnToPool == null;

    #endregion

    #region Public Methods

    /// <summary>
    /// Appends one ASCII byte (markup character or digit).
    /// </summary>
    /// <param name="b">ASCII byte to append.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Append(byte b)
    {
        if (_pendingHighSurrogate != '\0')
            FlushPendingSurrogate();

        if ((uint)_pos < (uint)_bytes.Length)
        {
            _bytes[_pos++] = b;
        }
        else
        {
            Grow(1);
            _bytes[_pos++] = b;
        }
    }

    /// <summary>
    /// Appends bytes that are already UTF-8 (ASCII literals, pre-encoded template parts, already-rendered output).
    /// </summary>
    /// <param name="utf8">UTF-8 bytes to append.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Append(scoped ReadOnlySpan<byte> utf8)
    {
        // Empty appends must not flush: appending "" between the two halves of a pair does not separate them
        if (utf8.Length == 0)
            return;
        if (_pendingHighSurrogate != '\0')
            FlushPendingSurrogate();
        if (_pos > _bytes.Length - utf8.Length)
            Grow(utf8.Length);

        utf8.CopyTo(_bytes[_pos..]);
        _pos += utf8.Length;
    }

    /// <summary>
    /// Reserves <paramref name="length"/> bytes and returns them for the caller to fill with ASCII/UTF-8.
    /// Every returned byte must be written before the builder is read.
    /// </summary>
    /// <param name="length">Number of bytes to reserve.</param>
    /// <returns>The reserved span.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Span<byte> AppendSpan(int length)
    {
        if (_pendingHighSurrogate != '\0')
            FlushPendingSurrogate();

        int origPos = _pos;
        if (origPos > _bytes.Length - length)
            Grow(length);

        _pos = origPos + length;
        return _bytes.Slice(origPos, length);
    }

    /// <summary>
    /// Returns a writable span of at least <paramref name="sizeHint"/> bytes after the written data, without advancing.
    /// Pair with <see cref="Advance"/> for writers whose output size is only known after writing.
    /// </summary>
    /// <param name="sizeHint">Minimum number of bytes required.</param>
    /// <returns>Writable span starting at the current position.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Span<byte> GetSpan(int sizeHint)
    {
        if (_pendingHighSurrogate != '\0')
            FlushPendingSurrogate();
        if (_pos > _bytes.Length - sizeHint)
            Grow(sizeHint);

        return _bytes[_pos..];
    }

    /// <summary>
    /// Commits <paramref name="count"/> bytes written into the span returned by <see cref="GetSpan"/>.
    /// </summary>
    /// <param name="count">Number of bytes written.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Advance(int count) => _pos += count;

    /// <summary>
    /// Formats a number with the invariant culture and appends its UTF-8 (ASCII) text.
    /// </summary>
    /// <typeparam name="T">A type implementing <see cref="IUtf8SpanFormattable"/>.</typeparam>
    /// <param name="value">The value to format.</param>
    /// <param name="format">Optional format specifier.</param>
    public void AppendFormatted<T>(T value, scoped ReadOnlySpan<char> format = default)
        where T : IUtf8SpanFormattable
    {
        if (_pendingHighSurrogate != '\0')
            FlushPendingSurrogate();

        if (value.TryFormat(_bytes[_pos..], out int written, format, NumberFormatInfo.InvariantInfo))
        {
            _pos += written;
            return;
        }

        // Longest invariant number text (double "R", long.MinValue, decimal) fits easily in 64 bytes
        Grow(64);
        value.TryFormat(_bytes[_pos..], out written, format, NumberFormatInfo.InvariantInfo);
        _pos += written;
    }

    /// <summary>
    /// Transcodes UTF-16 text whose surrogates may pair with neighbouring text, i.e. text that the UTF-16 renderer
    /// appended without surrogate fix-up (names, CDATA, the raw runs of an XML-escaped value).
    /// </summary>
    /// <param name="text">UTF-16 text to append.</param>
    public void AppendUtf16(scoped ReadOnlySpan<char> text)
    {
        if (text.IsEmpty)
            return;

        if (_pendingHighSurrogate != '\0')
        {
            // Clear first: the writes below go through AppendSpan, which would otherwise flush it again
            char high = _pendingHighSurrogate;
            _pendingHighSurrogate = '\0';
            if (char.IsLowSurrogate(text[0]))
            {
                WriteSurrogatePair(high, text[0]);
                text = text[1..];
            }
            else
            {
                WriteReplacementChar();
            }

            if (text.IsEmpty)
                return;
        }

        // A trailing high surrogate is unpaired within this text but may pair with the next append: hold it back
        if (char.IsHighSurrogate(text[^1]))
        {
            _pendingHighSurrogate = text[^1];
            text = text[..^1];
        }

        Transcode(text);
    }

    /// <summary>
    /// Transcodes UTF-16 text whose unpaired surrogates the UTF-16 renderer replaced with U+FFFD (escaped values),
    /// so nothing in it pairs with neighbouring text.
    /// </summary>
    /// <param name="text">UTF-16 text to append.</param>
    public void AppendUtf16Isolated(scoped ReadOnlySpan<char> text)
    {
        if (text.IsEmpty)
            return;
        if (_pendingHighSurrogate != '\0')
            FlushPendingSurrogate();
        Transcode(text);
    }

    /// <summary>
    /// Flushes any held-back high surrogate and returns the complete UTF-8 output.
    /// </summary>
    /// <returns>A span over all bytes written.</returns>
    public ReadOnlySpan<byte> AsSpan()
    {
        if (_pendingHighSurrogate != '\0')
            FlushPendingSurrogate();
        return _bytes[.._pos];
    }

    /// <summary>
    /// Returns any pooled array to <see cref="ArrayPool{T}"/> and resets the builder.
    /// </summary>
    public void Dispose()
    {
        byte[]? toReturn = _arrayToReturnToPool;
        this = default;
        if (toReturn != null)
            ArrayPool<byte>.Shared.Return(toReturn);
    }

    #endregion

    #region Non-Public Methods

    /// <summary>
    /// Writes the held-back high surrogate as U+FFFD: it was not followed by a low surrogate.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void FlushPendingSurrogate()
    {
        _pendingHighSurrogate = '\0';
        WriteReplacementChar();
    }

    /// <summary>
    /// Replaces the current buffer with a larger one rented from <see cref="ArrayPool{T}"/>, keeping the written bytes.
    /// </summary>
    /// <param name="additionalCapacityBeyondPos">Minimum number of extra bytes needed beyond the current position.</param>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void Grow(int additionalCapacityBeyondPos)
    {
        int newCapacity = Math.Max(_pos + additionalCapacityBeyondPos, _bytes.Length * 2);
        byte[] poolArray = ArrayPool<byte>.Shared.Rent(newCapacity);
        _bytes[.._pos].CopyTo(poolArray);

        byte[]? toReturn = _arrayToReturnToPool;
        _bytes = _arrayToReturnToPool = poolArray;
        if (toReturn != null)
            ArrayPool<byte>.Shared.Return(toReturn);
    }

    /// <summary>
    /// Transcodes UTF-16 to UTF-8 at the current position; unpaired surrogates become U+FFFD.
    /// </summary>
    /// <param name="text">UTF-16 text with no surrogate held back from a previous append.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Transcode(scoped ReadOnlySpan<char> text)
    {
        if (text.IsEmpty)
            return;

        // Worst case 3 UTF-8 bytes per UTF-16 code unit (BMP char or replacement char; a pair is 4 bytes for 2 units)
        int maxBytes = text.Length * 3;
        if (_pos > _bytes.Length - maxBytes)
            Grow(maxBytes);

        Utf8.FromUtf16(text, _bytes[_pos..], out _, out int written, replaceInvalidSequences: true);
        _pos += written;
    }

    /// <summary>
    /// Writes a supplementary-plane code point from a high/low surrogate pair as 4 UTF-8 bytes.
    /// </summary>
    /// <param name="high">High (leading) surrogate.</param>
    /// <param name="low">Low (trailing) surrogate.</param>
    private void WriteSurrogatePair(char high, char low)
    {
        int codePoint = char.ConvertToUtf32(high, low);
        Span<byte> dst = AppendSpan(4);
        dst[0] = (byte)(0xF0 | (codePoint >> 18));
        dst[1] = (byte)(0x80 | ((codePoint >> 12) & 0x3F));
        dst[2] = (byte)(0x80 | ((codePoint >> 6) & 0x3F));
        dst[3] = (byte)(0x80 | (codePoint & 0x3F));
    }

    /// <summary>
    /// Writes U+FFFD (EF BF BD), the encoding of an unpaired surrogate.
    /// </summary>
    private void WriteReplacementChar()
    {
        Span<byte> dst = AppendSpan(3);
        dst[0] = 0xEF;
        dst[1] = 0xBF;
        dst[2] = 0xBD;
    }

    #endregion

    #region Non-Public Fields

    /// <summary>
    /// Pooled array backing the builder once the initial buffer is outgrown; null while using the initial buffer.
    /// </summary>
    private byte[]? _arrayToReturnToPool;

    /// <summary>
    /// Active byte buffer — the caller-supplied span or a pooled array.
    /// </summary>
    private Span<byte> _bytes;

    /// <summary>
    /// High surrogate that ended the last <see cref="AppendUtf16"/> text and may pair with the next; '\0' if none.
    /// </summary>
    private char _pendingHighSurrogate;

    /// <summary>
    /// Current write position (number of bytes appended so far).
    /// </summary>
    private int _pos;

    #endregion
}
