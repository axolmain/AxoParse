using System.Buffers;
using System.Runtime.CompilerServices;

namespace AxoParse.Evtx;

/// <summary>
/// ArrayPool-backed string builder ref struct. Stackalloc initial buffer covers most records
/// without heap allocation. Falls back to ArrayPool when outgrown. Must Dispose() to return buffer.
/// </summary>
internal ref struct ValueStringBuilder : IDisposable
{
    #region Constructors And Destructors

    /// <summary>
    /// Initialises the builder with a caller-supplied buffer (typically stackalloc).
    /// No pooled array is rented until this buffer is exhausted.
    /// </summary>
    /// <param name="initialBuffer">Stack-allocated or pre-existing span to use as the initial backing store.</param>
    public ValueStringBuilder(Span<char> initialBuffer)
    {
        _arrayToReturnToPool = null;
        _chars = initialBuffer;
        _pos = 0;
    }

    #endregion

    #region Properties

    /// <summary>
    /// Number of characters written to the builder.
    /// </summary>
    public int Length => _pos;

    #endregion

    #region Public Methods

    /// <summary>
    /// Appends a single character, growing the buffer if necessary.
    /// </summary>
    /// <param name="c">The character to append.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Append(char c)
    {
        if ((uint)_pos < (uint)_chars.Length)
        {
            _chars[_pos++] = c;
        }
        else
        {
            GrowAndAppend(c);
        }
    }

    /// <summary>
    /// Appends a string, or does nothing if null. Single-character strings are fast-pathed.
    /// Inlines the copy directly to avoid the span intermediate and extra method call.
    /// </summary>
    /// <param name="s">The string to append, or null.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Append(string? s)
    {
        if (s == null) return;
        int len = s.Length;
        if (len == 1)
        {
            Append(s[0]);
            return;
        }

        if (len == 0) return;
        if (_pos > _chars.Length - len)
        {
            Grow(len);
        }

        s.CopyTo(_chars.Slice(_pos, len));
        _pos += len;
    }

    /// <summary>
    /// Appends a span of characters, growing the buffer if necessary.
    /// </summary>
    /// <param name="value">The characters to append.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Append(scoped ReadOnlySpan<char> value)
    {
        if (value.Length == 0) return;
        if (_pos > _chars.Length - value.Length)
        {
            Grow(value.Length);
        }

        value.CopyTo(_chars[_pos..]);
        _pos += value.Length;
    }

    /// <summary>
    /// Formats and appends a value that implements <see cref="ISpanFormattable"/>, growing the buffer on demand.
    /// </summary>
    /// <typeparam name="T">A type implementing <see cref="ISpanFormattable"/>.</typeparam>
    /// <param name="value">The value to format and append.</param>
    /// <param name="format">Optional format specifier.</param>
    public void AppendFormatted<T>(T value, scoped ReadOnlySpan<char> format = default)
        where T : ISpanFormattable
    {
        if (value.TryFormat(_chars[_pos..], out int charsWritten, format, null))
        {
            _pos += charsWritten;
        }
        else
        {
            GrowAndFormat(value, format);
        }
    }

    /// <summary>
    /// Slow path for <see cref="AppendFormatted{T}"/>: grows the buffer and retries formatting.
    /// Separated to keep the inline fast path small.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void GrowAndFormat<T>(T value, scoped ReadOnlySpan<char> format)
        where T : ISpanFormattable
    {
        Grow(64);
        if (!value.TryFormat(_chars[_pos..], out int charsWritten, format, null))
        {
            Grow(256);
            value.TryFormat(_chars[_pos..], out charsWritten, format, null);
        }

        _pos += charsWritten;
    }

    /// <summary>
    /// Returns the written portion of the buffer as a read-only span.
    /// </summary>
    /// <returns>A span over the characters appended so far.</returns>
    public ReadOnlySpan<char> AsSpan() => _chars[.._pos];

    /// <summary>
    /// Returns any pooled array to <see cref="ArrayPool{T}"/> and resets the builder.
    /// Must be called when the builder is no longer needed to avoid pool exhaustion.
    /// </summary>
    public void Dispose()
    {
        char[]? toReturn = _arrayToReturnToPool;
        this = default;
        if (toReturn != null)
        {
            ArrayPool<char>.Shared.Return(toReturn);
        }
    }

    /// <summary>
    /// Allocates a new string from the written portion of the buffer.
    /// </summary>
    /// <returns>A string containing all appended characters.</returns>
    public override string ToString()
    {
        return _chars[.._pos].ToString();
    }

    #endregion

    #region Non-Public Methods

    /// <summary>
    /// Replaces the current buffer with a larger one rented from <see cref="ArrayPool{T}"/>,
    /// copying existing content and returning the previous pooled array if any.
    /// </summary>
    /// <param name="additionalCapacityBeyondPos">Minimum number of extra characters needed beyond the current position.</param>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void Grow(int additionalCapacityBeyondPos)
    {
        int newCapacity = Math.Max(_pos + additionalCapacityBeyondPos, _chars.Length * 2);
        char[] poolArray = ArrayPool<char>.Shared.Rent(newCapacity);
        _chars[.._pos].CopyTo(poolArray);

        char[]? toReturn = _arrayToReturnToPool;
        _chars = _arrayToReturnToPool = poolArray;
        if (toReturn != null)
        {
            ArrayPool<char>.Shared.Return(toReturn);
        }
    }

    /// <summary>
    /// Grows the buffer by at least one character and appends <paramref name="c"/>. Called when the inline fast-path overflows.
    /// </summary>
    /// <param name="c">The character to append after growing.</param>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void GrowAndAppend(char c)
    {
        Grow(1);
        _chars[_pos++] = c;
    }

    #endregion

    #region Non-Public Fields

    /// <summary>
    /// Pooled array backing the builder once the initial stackalloc buffer is outgrown; null while using the initial buffer.
    /// </summary>
    private char[]? _arrayToReturnToPool;

    /// <summary>
    /// Active character buffer — either the caller-supplied stackalloc span or a pooled array.
    /// </summary>
    private Span<char> _chars;

    /// <summary>
    /// Current write position (number of characters appended so far).
    /// </summary>
    private int _pos;

    #endregion
}