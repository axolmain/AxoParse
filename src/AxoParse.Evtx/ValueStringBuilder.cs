using System.Buffers;
using System.Runtime.CompilerServices;

namespace AxoParse.Evtx;

/// <summary>
/// ArrayPool-backed string builder ref struct. Stackalloc initial buffer covers most records
/// without heap allocation. Falls back to ArrayPool when outgrown. Must Dispose() to return buffer.
/// </summary>
internal ref struct ValueStringBuilder
{
    private char[]? _arrayToReturnToPool;
    private Span<char> _chars;
    private int _pos;

    public ValueStringBuilder(Span<char> initialBuffer)
    {
        _arrayToReturnToPool = null;
        _chars = initialBuffer;
        _pos = 0;
    }

    public int Length => _pos;

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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Append(string? s)
    {
        if (s == null) return;
        if (s.Length == 1)
        {
            Append(s[0]);
            return;
        }

        Append(s.AsSpan());
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Append(scoped ReadOnlySpan<char> value)
    {
        if (value.Length == 0) return;
        if (_pos > _chars.Length - value.Length)
        {
            Grow(value.Length);
        }

        value.CopyTo(_chars.Slice(_pos));
        _pos += value.Length;
    }

    public void AppendFormatted<T>(T value, scoped ReadOnlySpan<char> format = default)
        where T : ISpanFormattable
    {
        if (value.TryFormat(_chars.Slice(_pos), out int charsWritten, format, null))
        {
            _pos += charsWritten;
        }
        else
        {
            // Grow and retry
            Grow(64);
            if (!value.TryFormat(_chars.Slice(_pos), out charsWritten, format, null))
            {
                Grow(256);
                value.TryFormat(_chars.Slice(_pos), out charsWritten, format, null);
            }

            _pos += charsWritten;
        }
    }

    public ReadOnlySpan<char> AsSpan() => _chars.Slice(0, _pos);

    public override string ToString()
    {
        return _chars.Slice(0, _pos).ToString();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void GrowAndAppend(char c)
    {
        Grow(1);
        _chars[_pos++] = c;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void Grow(int additionalCapacityBeyondPos)
    {
        int newCapacity = Math.Max(_pos + additionalCapacityBeyondPos, _chars.Length * 2);
        char[] poolArray = ArrayPool<char>.Shared.Rent(newCapacity);
        _chars.Slice(0, _pos).CopyTo(poolArray);

        char[]? toReturn = _arrayToReturnToPool;
        _chars = _arrayToReturnToPool = poolArray;
        if (toReturn != null)
        {
            ArrayPool<char>.Shared.Return(toReturn);
        }
    }

    public void Dispose()
    {
        char[]? toReturn = _arrayToReturnToPool;
        this = default;
        if (toReturn != null)
        {
            ArrayPool<char>.Shared.Return(toReturn);
        }
    }
}