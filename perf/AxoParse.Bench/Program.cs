using System.Globalization;
using AxoParse.Evtx;

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

// Stream chunks straight from disk to stdout: each worker reads the 64 KB chunk it renders, so the file is never
// loaded whole and memory stays flat (matches Rust evtx_dump, which also reads chunk by chunk)
using Stream stdout = Console.OpenStandardOutput();
EvtxParser.WriteTo(args[0], stdout, format, threads);

return 0;
