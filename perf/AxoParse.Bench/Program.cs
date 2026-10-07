using System.Globalization;
using AxoParse.Evtx.Evtx;

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

byte[] data = File.ReadAllBytes(args[0]);

// Stream each chunk's UTF-8 output straight to stdout (matches Rust evtx_dump output to a pipe/file):
// no per-record strings or arrays are kept, so memory stays flat and no gen1/gen2 collections run
using Stream stdout = Console.OpenStandardOutput();
EvtxParser.WriteTo(data, stdout, format, threads);

return 0;
