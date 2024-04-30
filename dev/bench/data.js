window.BENCHMARK_DATA = {
  "lastUpdate": 1714468160972,
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
      },
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
          "id": "57943a16c1f1f799a47b4b03bb01057aefefeed8",
          "message": "Merge pull request #14 from westermo/support-netstandard\n\nMove to netstandard 2.0",
          "timestamp": "2024-04-30T11:07:23+02:00",
          "tree_id": "b9b994defce54d5e481ec68ffb92834878b9a11a",
          "url": "https://github.com/westermo/FactoryGenerator/commit/57943a16c1f1f799a47b4b03bb01057aefefeed8"
        },
        "date": 1714468160585,
        "tool": "benchmarkdotnet",
        "benches": [
          {
            "name": "Benchmarks.ResolveBenchmarks.ResolveChain",
            "value": 135.57445757729667,
            "unit": "ns",
            "range": "± 0.9292594534452475"
          },
          {
            "name": "Benchmarks.ResolveBenchmarks.ResolveScoped",
            "value": 26.348994531801768,
            "unit": "ns",
            "range": "± 0.22476358238827698"
          },
          {
            "name": "Benchmarks.ResolveBenchmarks.ResolveSingleton",
            "value": 20.338140648144943,
            "unit": "ns",
            "range": "± 0.05486431414953504"
          },
          {
            "name": "Benchmarks.ResolveBenchmarks.ResolveTransient",
            "value": 30.03070642856451,
            "unit": "ns",
            "range": "± 0.22286513357095514"
          },
          {
            "name": "Benchmarks.ResolveBenchmarks.Create",
            "value": 943.090672492981,
            "unit": "ns",
            "range": "± 6.0077461667096745"
          }
        ]
      }
    ]
  }
}