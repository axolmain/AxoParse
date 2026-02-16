using System.Diagnostics;
using AxoParse.Evtx;

string file = args.Length > 0 ? args[0] : "tests/data/security_big_sample.evtx";

byte[] data = File.ReadAllBytes(file);

const int WarmupRuns = 3;
const int MeasuredRuns = 5;

long[] RunTest(int threads, OutputFormat format)
{
    for (int i = 0; i < WarmupRuns; i++)
    {
        EvtxParser.Parse(data, threads, format);
    }

    GC.Collect();
    GC.WaitForPendingFinalizers();
    GC.Collect();

    long[] times = new long[MeasuredRuns];
    for (int i = 0; i < MeasuredRuns; i++)
    {
        Stopwatch sw = Stopwatch.StartNew();
        EvtxParser.Parse(data, threads, format);
        sw.Stop();
        times[i] = sw.ElapsedMilliseconds;
    }

    return times;
}

int[] threadCounts = [1, 8];
OutputFormat[] formats = [OutputFormat.Xml, OutputFormat.Json];

// results[format][threadIdx][run]
long[][][] results = new long[formats.Length][][];
for (int f = 0; f < formats.Length; f++)
{
    results[f] = new long[threadCounts.Length][];
    for (int t = 0; t < threadCounts.Length; t++)
    {
        Console.Error.Write($"Running {formats[f]} {threadCounts[t]}t... ");
        results[f][t] = RunTest(threadCounts[t], formats[f]);
        Console.Error.WriteLine("done");
    }
}

// Header
Console.WriteLine();
string header = "Run".PadRight(6);
for (int f = 0; f < formats.Length; f++)
{
    for (int t = 0; t < threadCounts.Length; t++)
    {
        string col = $"{formats[f]} {threadCounts[t]}t";
        header += col.PadLeft(12);
    }
}
Console.WriteLine(header);
Console.WriteLine(new string('-', header.Length));

// Rows
for (int r = 0; r < MeasuredRuns; r++)
{
    string row = $"  {r + 1}".PadRight(6);
    for (int f = 0; f < formats.Length; f++)
    {
        for (int t = 0; t < threadCounts.Length; t++)
        {
            row += $"{results[f][t][r]} ms".PadLeft(12);
        }
    }
    Console.WriteLine(row);
}

// Averages
Console.WriteLine(new string('-', header.Length));
string avgRow = "Avg".PadRight(6);
for (int f = 0; f < formats.Length; f++)
{
    for (int t = 0; t < threadCounts.Length; t++)
    {
        long avg = 0;
        for (int r = 0; r < MeasuredRuns; r++)
            avg += results[f][t][r];
        avg /= MeasuredRuns;
        avgRow += $"{avg} ms".PadLeft(12);
    }
}
Console.WriteLine(avgRow);
