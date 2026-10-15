using System.Globalization;
using AxoParse.Evtx.Evtx;
using Microsoft.Win32.SafeHandles;

if (args.Length == 0) return 1;

int threads = 0; // default: all cores
OutputFormat format = OutputFormat.Xml;
for (int i = 1; i < args.Length; i++)
{
    if ((args[i] == "-t") && (i + 1 < args.Length))
        threads = int.Parse(args[++i], NumberStyles.Integer, NumberFormatInfo.InvariantInfo);
    else if ((args[i] == "-o") && (i + 1 < args.Length))
    {
        // Ordinal comparison: culture-aware casing would initialise globalization and load ICU (~5 ms on Windows)
        format = string.Equals(args[++i], "json", StringComparison.OrdinalIgnoreCase) ? OutputFormat.Json : OutputFormat.Xml;
    }
}

byte[] data = ReadFile(args[0], threads > 0 ? threads : Environment.ProcessorCount);

// Stream each chunk's UTF-8 output straight to stdout (matches Rust evtx_dump output to a pipe/file):
// no per-record strings or arrays are kept, so memory stays flat and no gen1/gen2 collections run
using Stream stdout = Console.OpenStandardOutput();
EvtxParser.WriteTo(data, stdout, format, threads);

return 0;

// Reads the whole file into an uninitialised array (every byte is overwritten, so zeroing is wasted work) in up to
// `parts` parallel ranges. Copying 30 MB out of the page cache takes ~3.5 ms on one thread vs ~1.8 ms on eight (M4).
static byte[] ReadFile(string path, int parts)
{
    using SafeFileHandle handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read);
    long length = RandomAccess.GetLength(handle);
    byte[] data = GC.AllocateUninitializedArray<byte>(checked((int)length));
    // Ranges under 1 MB aren't worth a thread
    int count = (int)Math.Clamp(length / (1024 * 1024), 1, parts);
    long per = (length + count - 1) / count;
    Parallel.For(0, count, part =>
    {
        long offset = part * per;
        long end = Math.Min(length, offset + per);
        while (offset < end)
        {
            int read = RandomAccess.Read(handle, data.AsSpan((int)offset, (int)(end - offset)), offset);
            if (read == 0)
                throw new EndOfStreamException($"{path} shrank while being read");
            offset += read;
        }
    });
    return data;
}
