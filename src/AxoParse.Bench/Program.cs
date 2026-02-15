using AxoParse.Evtx;

if (args.Length == 0) return 1;

int threads = 0; // default: all cores
OutputFormat format = OutputFormat.Xml;
for (int i = 1; i < args.Length; i++)
{
    if (args[i] == "-t" && i + 1 < args.Length)
        threads = int.Parse(args[++i]);
    else if (args[i] == "-o" && i + 1 < args.Length)
    {
        string fmt = args[++i].ToLowerInvariant();
        format = fmt == "json" ? OutputFormat.Json : OutputFormat.Xml;
    }
}

byte[] data = File.ReadAllBytes(args[0]);
EvtxParser parser = EvtxParser.Parse(data, threads, format);

using Stream stdout = Console.OpenStandardOutput();

if (format == OutputFormat.Json)
{
    // Write UTF-8 JSON bytes directly — no string conversion
    for (int index = 0; index < parser.Chunks.Count; index++)
    {
        EvtxChunk chunk = parser.Chunks[index];
        if (chunk.ParsedJson != null)
        {
            for (int i = 0; i < chunk.ParsedJson.Length; i++)
                stdout.Write(chunk.ParsedJson[i]);
        }
    }
}
else
{
    // Write serialized XML to stdout (matches Rust evtx_dump default XML output)
    using StreamWriter writer = new StreamWriter(stdout, bufferSize: 65536);
    for (int index = 0; index < parser.Chunks.Count; index++)
    {
        EvtxChunk chunk = parser.Chunks[index];
        for (int i = 0; i < chunk.ParsedXml.Length; i++)
        {
            string xml = chunk.ParsedXml[i];
            writer.Write(xml);
        }
    }

    writer.Flush();
}

return 0;