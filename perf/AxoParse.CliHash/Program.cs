using System.Diagnostics;
using System.Security.Cryptography;

// Verifies a built AxoParse.Bench CLI (normally the NativeAOT publish) against the golden output hashes used by
// GoldenOutputTests: runs it on every sample x {xml, json} x {1, 8} threads and SHA-256s stdout. The CLI streams
// through EvtxParser.WriteTo, so its bytes must match the in-process golden hashes exactly. Catches trimming/AOT and
// stdout-handling differences the in-process tests can't. Exit code 0 = all match, 1 = mismatch, 2 = usage error.
//
// Usage: dotnet run --project perf/AxoParse.CliHash -c Release -- <path-to-cli> [--data <dir>] [--golden <file>]

if ((args.Length == 0) || args[0].StartsWith('-'))
{
    Console.Error.WriteLine("Usage: AxoParse.CliHash <path-to-cli> [--data <dir>] [--golden <file>]");
    return 2;
}

string cli = Path.GetFullPath(args[0]);
string? root = FindRepoRoot(AppContext.BaseDirectory) ?? FindRepoRoot(Environment.CurrentDirectory);
string? dataDir = root == null ? null : Path.Combine(root, "tests", "data");
string? goldenPath = root == null ? null : Path.Combine(root, "tests", "AxoParse.Evtx.Tests", "Golden", "output-sha256.txt");
for (int i = 1; i < args.Length - 1; i++)
{
    if (args[i] == "--data") dataDir = Path.GetFullPath(args[++i]);
    else if (args[i] == "--golden") goldenPath = Path.GetFullPath(args[++i]);
}

if (!File.Exists(cli) || (dataDir == null) || !Directory.Exists(dataDir) || (goldenPath == null) || !File.Exists(goldenPath))
{
    Console.Error.WriteLine($"Missing input. cli={cli} data={dataDir} golden={goldenPath}");
    return 2;
}

Dictionary<string, string> golden = new(StringComparer.Ordinal);
foreach (string line in File.ReadAllLines(goldenPath))
{
    if ((line.Length == 0) || line.StartsWith('#')) continue;
    int space = line.IndexOf(' ');
    golden[line[(space + 1)..]] = line[..space];
}

string[] formats = ["xml", "json"];
int[] threadCounts = [1, 8];
int checkedCount = 0;
List<string> failures = new();
Stopwatch sw = Stopwatch.StartNew();

foreach (string file in Directory.GetFiles(dataDir, "*.evtx").Order(StringComparer.Ordinal))
{
    string name = Path.GetFileName(file);
    foreach (string format in formats)
    {
        string key = $"{format}/{name}";
        if (!golden.TryGetValue(key, out string? expected))
        {
            failures.Add($"{key}: no golden entry (re-bless GoldenOutputTests)");
            continue;
        }

        foreach (int threads in threadCounts)
        {
            (string hash, int exitCode, string stderr) = RunAndHash(cli, file, format, threads);
            checkedCount++;
            if (exitCode != 0)
                failures.Add($"{key} ({threads} threads): CLI exited {exitCode}: {stderr.Trim()}");
            else if (hash != expected)
                failures.Add($"{key} ({threads} threads): hash {hash[..12]}... != golden {expected[..12]}...");
        }
    }
}

if (failures.Count > 0)
{
    Console.Error.WriteLine($"CLI output check FAILED: {failures.Count} of {checkedCount} runs differ from {goldenPath}");
    foreach (string failure in failures)
        Console.Error.WriteLine("  " + failure);
    return 1;
}

Console.WriteLine($"CLI output check passed: {checkedCount} runs match golden hashes ({sw.Elapsed.TotalSeconds:F1} s)");
return 0;

// Runs the CLI on one sample and hashes its stdout as it streams (output can be hundreds of MB).
static (string Hash, int ExitCode, string Stderr) RunAndHash(string cli, string file, string format, int threads)
{
    ProcessStartInfo psi = new(cli)
    {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
    };
    psi.ArgumentList.Add(file);
    psi.ArgumentList.Add("-t");
    psi.ArgumentList.Add(threads.ToString(System.Globalization.CultureInfo.InvariantCulture));
    psi.ArgumentList.Add("-o");
    psi.ArgumentList.Add(format);

    using Process process = Process.Start(psi)!;
    Task<string> stderr = process.StandardError.ReadToEndAsync();
    byte[] hash = SHA256.HashData(process.StandardOutput.BaseStream);
    process.WaitForExit();
    return (Convert.ToHexStringLower(hash), process.ExitCode, stderr.Result);
}

// Walks up from a directory to the one containing AxoParse.slnx.
static string? FindRepoRoot(string start)
{
    for (DirectoryInfo? dir = new(start); dir != null; dir = dir.Parent)
    {
        if (File.Exists(Path.Combine(dir.FullName, "AxoParse.slnx")))
            return dir.FullName;
    }

    return null;
}
