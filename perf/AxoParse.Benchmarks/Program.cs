using AxoParse.Benchmarks;
using BenchmarkDotNet.Running;

BenchmarkSwitcher.FromAssembly(typeof(BenchData).Assembly).Run(args, BenchConfig.Create());
