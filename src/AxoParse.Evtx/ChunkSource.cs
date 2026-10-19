using Microsoft.Win32.SafeHandles;

namespace AxoParse.Evtx;

/// <summary>
/// Where chunk bytes come from: a whole file in memory, positional reads from a file handle, or reads from a seekable
/// stream. File and stream sources read each chunk into the caller's own 64 KB buffer. Rendering only touches the
/// chunk's own bytes (see <see cref="BinXml.BinXmlParser"/>), so every source gives identical output.
/// </summary>
internal sealed class ChunkSource
{
    #region Constructors And Destructors

    /// <summary>
    /// Creates a source over exactly one of the three inputs.
    /// </summary>
    /// <param name="fileData">Whole file in memory, or null.</param>
    /// <param name="file">Open file handle, or null.</param>
    /// <param name="stream">Seekable, readable stream, or null.</param>
    /// <param name="length">Total input length in bytes.</param>
    private ChunkSource(byte[]? fileData, SafeFileHandle? file, Stream? stream, long length)
    {
        _fileData = fileData;
        _file = file;
        _stream = stream;
        Length = length;
    }

    #endregion

    #region Properties

    /// <summary>
    /// Total input length in bytes.
    /// </summary>
    public long Length { get; }

    /// <summary>
    /// True when each worker needs its own 64 KB buffer to read chunks into.
    /// </summary>
    public bool NeedsBuffer => _fileData == null;

    #endregion

    #region Public Methods

    /// <summary>
    /// A source over a whole file in memory.
    /// </summary>
    /// <param name="fileData">The file bytes.</param>
    /// <returns>The source.</returns>
    public static ChunkSource FromArray(byte[] fileData) => new(fileData, null, null, fileData.Length);

    /// <summary>
    /// A source that reads chunks with positional reads, leaving the handle's file position alone.
    /// </summary>
    /// <param name="file">Handle opened for reading.</param>
    /// <returns>The source.</returns>
    public static ChunkSource FromHandle(SafeFileHandle file) => new(null, file, null, RandomAccess.GetLength(file));

    /// <summary>
    /// A source over a seekable stream whose content, from position 0, is the EVTX file. A <see cref="FileStream"/>
    /// is read through its handle with positional reads, so workers read in parallel; any other stream is read under
    /// a lock, one chunk at a time, and its position is changed.
    /// </summary>
    /// <param name="stream">Readable, seekable stream.</param>
    /// <returns>The source.</returns>
    /// <exception cref="ArgumentException">The stream can't be read or can't seek.</exception>
    public static ChunkSource FromStream(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead || !stream.CanSeek)
            throw new ArgumentException("The input stream must be readable and seekable.", nameof(stream));
        if (stream is FileStream fileStream)
            return FromHandle(fileStream.SafeFileHandle);
        return new ChunkSource(null, null, stream, stream.Length);
    }

    /// <summary>
    /// Fills <paramref name="destination"/> from <paramref name="file"/> at <paramref name="offset"/>, looping over
    /// short reads.
    /// </summary>
    /// <param name="file">Open file handle.</param>
    /// <param name="destination">Bytes to fill.</param>
    /// <param name="offset">File offset to read from.</param>
    /// <exception cref="EndOfStreamException">The file ended first (it shrank while being read).</exception>
    public static void ReadFully(SafeFileHandle file, Span<byte> destination, long offset)
    {
        int filled = 0;
        while (filled < destination.Length)
        {
            int read = RandomAccess.Read(file, destination[filled..], offset + filled);
            if (read == 0)
                throw new EndOfStreamException($"EVTX file ended at {offset + filled} while reading {destination.Length} bytes at {offset}");
            filled += read;
        }
    }

    /// <summary>
    /// Makes the 64 KB chunk at <paramref name="offset"/> available.
    /// </summary>
    /// <param name="offset">Absolute file offset of the chunk.</param>
    /// <param name="buffer">The caller's 64 KB read buffer (file and stream sources only).</param>
    /// <param name="data">Array holding the chunk.</param>
    /// <returns>Offset of the chunk within <paramref name="data"/>.</returns>
    public int Load(long offset, byte[]? buffer, out byte[] data)
    {
        if (_fileData != null)
        {
            data = _fileData;
            return (int)offset;
        }

        Read(buffer.AsSpan(0, EvtxChunk.ChunkSize), offset);
        data = buffer!;
        return 0;
    }

    /// <summary>
    /// Reads and parses the file header.
    /// </summary>
    /// <returns>The parsed header.</returns>
    /// <exception cref="EvtxParseException">The input is shorter than a header or its signature is wrong.</exception>
    public EvtxFileHeader ReadHeader()
    {
        if (_fileData != null)
            return EvtxFileHeader.ParseEvtxFileHeader(_fileData);

        Span<byte> headerBytes = stackalloc byte[_fileHeaderBytes];
        int headerLength = (int)Math.Min(Length, _fileHeaderBytes);
        Read(headerBytes[..headerLength], 0);
        return EvtxFileHeader.ParseEvtxFileHeader(headerBytes[..headerLength]);
    }

    #endregion

    #region Non-Public Methods

    /// <summary>
    /// Fills <paramref name="destination"/> from the file or stream at <paramref name="offset"/>.
    /// </summary>
    /// <param name="destination">Bytes to fill.</param>
    /// <param name="offset">Input offset to read from.</param>
    /// <exception cref="EndOfStreamException">The input ended first.</exception>
    private void Read(Span<byte> destination, long offset)
    {
        if (_file != null)
        {
            ReadFully(_file, destination, offset);
            return;
        }

        // Workers share the stream, and Position + Read is not atomic
        lock (_stream!)
        {
            _stream.Position = offset;
            _stream.ReadExactly(destination);
        }
    }

    #endregion

    #region Non-Public Fields

    /// <summary>
    /// Bytes of the file header that are decoded (the 4 KB header block holds only these 128 bytes of data).
    /// </summary>
    private const int _fileHeaderBytes = 128;

    /// <summary>
    /// Open file handle, or null.
    /// </summary>
    private readonly SafeFileHandle? _file;

    /// <summary>
    /// Whole file in memory, or null.
    /// </summary>
    private readonly byte[]? _fileData;

    /// <summary>
    /// Seekable stream (other than a <see cref="FileStream"/>), or null.
    /// </summary>
    private readonly Stream? _stream;

    #endregion
}
