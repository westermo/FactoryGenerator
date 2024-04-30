window.BENCHMARK_DATA = {
  "lastUpdate": 1714467820747,
  "repoUrl": "https://github.com/westermo/FactoryGenerator",
  "entries": {
    "Benchmark.Net Benchmark": [
      {
        "commit": {
          "author": {
            "email": "142813963+carl-andersson-at-westermo@users.noreply.github.com",
            "name": "Caran",
            "username": "carl-andersson-at-westermo"
          },
          "committer": {
            "email": "noreply@github.com",
            "name": "GitHub",
            "username": "web-flow"
          },
          "distinct": true,
          "id": "3a6ba070e1fe813de207b8e147adc9935c069309",
          "message": "Merge pull request #15 from westermo/benchmarking\n\nBenchmarking",
          "timestamp": "2024-04-30T11:01:30+02:00",
          "tree_id": "539554b487819ed63021ad5a642d18af91be7ef4",
          "url": "https://github.com/westermo/FactoryGenerator/commit/3a6ba070e1fe813de207b8e147adc9935c069309"
        },
        "date": 1714467819843,
        "tool": "benchmarkdotnet",
        "benches": [
          {
            "name": "Benchmarks.ResolveBenchmarks.ResolveChain",
            "value": 136.43982877050126,
            "unit": "ns",
            "range": "± 1.0080206931436444"
          },
          {
            "name": "Benchmarks.ResolveBenchmarks.ResolveScoped",
            "value": 22.17171641588211,
            "unit": "ns",
            "range": "± 0.1249040851403255"
          },
          {
            "name": "Benchmarks.ResolveBenchmarks.ResolveSingleton",
            "value": 20.478168591856956,
            "unit": "ns",
            "range": "± 0.12990104641035763"
          },
          {
            "name": "Benchmarks.ResolveBenchmarks.ResolveTransient",
            "value": 27.88349703947703,
            "unit": "ns",
            "range": "± 0.28987690534695254"
          },
          {
            "name": "Benchmarks.ResolveBenchmarks.Create",
            "value": 954.1363311180702,
            "unit": "ns",
            "range": "± 8.329441718655918"
          }
        ]
      }
    ]
  }
}