using System.Diagnostics;
using AxoParse.Evtx;

string file = args.Length > 0 ? args[0] : "tests/data/security_big_sample.evtx";
int threads = args.Length > 1 ? int.Parse(args[1]) : 1;

byte[] data = File.ReadAllBytes(file);

// Warm up JIT
EvtxParser.Parse(data, threads);

Stopwatch sw = Stopwatch.StartNew();
EvtxParser parser = EvtxParser.Parse(data, threads);
sw.Stop();

Console.Error.WriteLine($"Parsed {parser.TotalRecords} records in {sw.ElapsedMilliseconds}ms ({threads} thread(s))");
