using AxoParse.Evtx.Evtx;

namespace AxoParse.Cli;

/// <summary>
/// CLI entry point. Parses an EVTX file and writes events as JSONL to stdout or a file.
/// </summary>
public class Program
{
    /// <summary>
    /// Parses the specified EVTX file and writes successful events as JSONL.
    /// </summary>
    /// <param name="args">Command-line arguments: &lt;file.evtx&gt; [-o output.jsonl].</param>
    /// <returns>0 on success, 1 on error.</returns>
    public static int Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help")
        {
            Console.Error.WriteLine("Usage: axoparse <file.evtx> [-o output.jsonl]");
            return args.Length == 0 ? 1 : 0;
        }

        string inputPath = args[0];
        string? outputPath = null;

        for (int i = 1; i < args.Length; i++)
        {
            if ((args[i] is "-o" or "--output") && i + 1 < args.Length)
            {
                outputPath = args[++i];
            }
            else
            {
                Console.Error.WriteLine($"Unknown argument: {args[i]}");
                return 1;
            }
        }

        try
        {
            byte[] fileData = File.ReadAllBytes(inputPath);
            EvtxParser parser = EvtxParser.Parse(fileData, format: OutputFormat.Json);

            if (outputPath != null)
            {
                using FileStream fs = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None,
                    bufferSize: 65536);
                WriteEvents(parser, fs);
            }
            else
            {
                using Stream stdout = Console.OpenStandardOutput();
                using BufferedStream buffered = new BufferedStream(stdout, 65536);
                WriteEvents(parser, buffered);
            }

            Console.Error.WriteLine($"Parsed {parser.TotalRecords} events from {parser.Chunks.Count} chunks");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 1;
        }
    }

    /// <summary>
    /// Writes all successful events from the parser as JSONL (one JSON object per line) to the target stream.
    /// </summary>
    /// <param name="parser">The parsed EVTX file.</param>
    /// <param name="stream">The destination stream.</param>
    private static void WriteEvents(EvtxParser parser, Stream stream)
    {
        foreach (EvtxEvent evt in parser.GetEvents())
        {
            if (!evt.IsSuccess)
                continue;
            stream.Write(evt.Json.Span);
            stream.Write("\n"u8);
        }
    }
}
