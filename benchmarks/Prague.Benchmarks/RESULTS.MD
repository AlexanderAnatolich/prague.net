# v0.0.9

## Before PooledSet -> ValueSet

BenchmarkDotNet v0.15.8, macOS Sequoia 15.3.1 (24D70) [Darwin 24.3.0]
Apple M4 Pro, 1 CPU, 12 logical and 12 physical cores
.NET SDK 9.0.307
[Host] : .NET 9.0.11 (9.0.11, 9.0.1125.51716), Arm64 RyuJIT armv8.0-a

Toolchain=InProcessNoEmitToolchain IterationCount=3 WarmupCount=1

| Method                              | ReaderThreads | DurationMs |    Mean | Rank | Reads | Writes | Duration | Reads/s |        Gen0 |        Gen1 |  Allocated |
|-------------------------------------|---------------|------------|--------:|-----:|------:|-------:|---------:|--------:|------------:|------------:|-----------:|
| ConcurrentReadsWithWriter_Pooled    | 11            | 3000       | 3.006 s |    1 | 19365 |   1704 |  3002 ms |    6451 |  13000.0000 |   6000.0000 |  108.96 MB |
| ConcurrentReadsWithWriter_NotPooled | 11            | 3000       | 3.009 s |    1 | 40059 |   1626 |  3007 ms |   13322 | 566000.0000 | 283000.0000 | 4290.99 MB |

## After PooledSet -> ValueSet

BenchmarkDotNet v0.15.8, macOS Sequoia 15.3.1 (24D70) [Darwin 24.3.0]
Apple M4 Pro, 1 CPU, 12 logical and 12 physical cores
.NET SDK 9.0.307
[Host] : .NET 9.0.11 (9.0.11, 9.0.1125.51716), Arm64 RyuJIT armv8.0-a

Toolchain=InProcessNoEmitToolchain IterationCount=3 WarmupCount=1

| Method                              | ReaderThreads | DurationMs |    Mean | Rank | Reads | Writes | Duration | Reads/s |        Gen0 |        Gen1 |  Allocated |
|-------------------------------------|---------------|------------|--------:|-----:|------:|-------:|---------:|--------:|------------:|------------:|-----------:|
| ConcurrentReadsWithWriter_Pooled    | 11            | 3000       | 3.003 s |    1 | 20543 |   1698 |  3003 ms |    6848 |  13000.0000 |   6000.0000 |   98.28 MB |
| ConcurrentReadsWithWriter_NotPooled | 11            | 3000       | 3.008 s |    1 | 43507 |   1644 |  3009 ms |   14459 | 615000.0000 | 307000.0000 | 4648.05 MB |

## Optimizations round 1

| Method                              | ReaderThreads | DurationMs |    Mean | Rank | Reads | Writes | Duration | Reads/s |        Gen0 |        Gen1 |  Allocated |
|-------------------------------------|---------------|------------|--------:|-----:|------:|-------:|---------:|--------:|------------:|------------:|-----------:|
| ConcurrentReadsWithWriter_Pooled    | 11            | 3000       | 3.005 s |    1 | 20235 |   1698 |  3008 ms |    6727 |  10000.0000 |   5000.0000 |   85.58 MB |
| ConcurrentReadsWithWriter_NotPooled | 11            | 3000       | 3.007 s |    1 | 40318 |   1626 |  3006 ms |   13413 | 570000.0000 | 285000.0000 | 4307.35 MB |

## Optimizations Round 2

| Method                              | ReaderThreads | DurationMs |    Mean | Rank | Reads | Writes | Duration | Reads/s |        Gen0 |        Gen1 |      Gen2 |  Allocated |
|-------------------------------------|---------------|------------|--------:|-----:|------:|-------:|---------:|--------:|------------:|------------:|----------:|-----------:|
| ConcurrentReadsWithWriter_Pooled    | 11            | 3000       | 3.006 s |    1 | 21012 |   1692 |  3004 ms |    6995 |  11000.0000 |   5000.0000 |         - |   91.84 MB |
| ConcurrentReadsWithWriter_NotPooled | 11            | 3000       | 3.006 s |    1 | 38532 |   1650 |  3010 ms |   12801 | 546000.0000 | 273000.0000 | 2000.0000 | 4116.59 MB |

# Optimimizations Rounds 3 - Final

| Method                              | ReaderThreads | DurationMs |    Mean | Rank |    Reads | Writes | Duration | Reads/s |        Gen0 |        Gen1 |      Gen2 |    Allocated |
|-------------------------------------|---------------|------------|--------:|-----:|---------:|-------:|---------:|--------:|------------:|------------:|----------:|-------------:|
| ConcurrentReadsWithWriter_Pooled    | 11            | 3000       | 3.055 s |    1 | 29591857 |   1535 |  3047 ms | 9711801 |           - |           - |         - |      90.2 KB |
| ConcurrentReadsWithWriter_NotPooled | 11            | 3000       | 3.045 s |    1 | 24763813 |   1545 |  3045 ms | 8132615 | 607000.0000 | 302000.0000 | 2000.0000 | 4841851.1 KB |

## JoinCacheBenchmark (GameCount = 500; MarketsPerGame = 20; TotalMarkets = GameCount * MarketsPerGame; )

BenchmarkDotNet v0.15.8, macOS Sequoia 15.3.1 (24D70) [Darwin 24.3.0]
Apple M4 Pro, 1 CPU, 12 logical and 12 physical cores
.NET SDK 9.0.307
[Host]     : .NET 9.0.11 (9.0.11, 9.0.1125.51716), Arm64 RyuJIT armv8.0-a
DefaultJob : .NET 9.0.11 (9.0.11, 9.0.1125.51716), Arm64 RyuJIT armv8.0-a

| Method                      | ReaderThreads |           Mean |         Error |         StdDev |     Ratio |  RatioSD | Rank |       Gen0 |       Gen1 |     Gen2 |    Allocated | Alloc Ratio |
|-----------------------------|---------------|---------------:|--------------:|---------------:|----------:|---------:|-----:|-----------:|-----------:|---------:|-------------:|------------:|
| ReadAllGames                | 16            |       2.486 us |     0.0444 us |      0.0562 us |      1.00 |     0.03 |    1 |     0.9689 |     0.0038 |        - |      7.92 KB |        1.00 |
| ReadGamesWithInfo           | 16            |      11.929 us |     0.0624 us |      0.0553 us |      4.80 |     0.11 |    2 |     0.9613 |          - |        - |      7.95 KB |        1.00 |
| ReadLiveGamesWithMarkets    | 16            |     127.173 us |     0.7243 us |      0.6775 us |     51.17 |     1.13 |    3 |     8.7891 |     0.7324 |        - |     73.08 KB |        9.22 |
| ReadGamesWithMarkets        | 16            |     304.801 us |     1.4629 us |      1.1422 us |    122.64 |     2.67 |    4 |    21.4844 |     3.9063 |        - |    175.98 KB |       22.21 |
| ReadGamesWithInfoAndMarkets | 16            |     307.475 us |     2.1123 us |      1.6492 us |    123.72 |     2.73 |    4 |    21.9727 |     3.9063 |        - |       180 KB |       22.72 |
| SingleGameLookupWithJoins   | 16            |     317.110 us |     5.7514 us |     12.6245 us |    127.59 |     5.73 |    4 |   246.5820 |    16.6016 |        - |   2021.09 KB |      255.13 |
| ConcurrentReadsWithWriter   | 16            | 144,539.594 us | 2,855.4169 us |  8,100.3432 us | 58,157.62 | 3,474.38 |    5 | 37000.0000 | 18750.0000 | 500.0000 | 288016.38 KB |   36,357.10 |
| ConcurrentReadsWithJoins    | 16            | 156,067.842 us | 5,508.1719 us | 16,067.5972 us | 62,796.18 | 6,574.03 |    5 | 37250.0000 | 19000.0000 | 750.0000 | 288026.84 KB |   36,358.42 |


## RingBuffer ArrayPool

// * Summary *

BenchmarkDotNet v0.15.8, macOS Tahoe 26.2 (25C56) [Darwin 25.2.0]
Apple M4 Pro, 1 CPU, 12 logical and 12 physical cores
.NET SDK 9.0.307
[Host]     : .NET 9.0.11 (9.0.11, 9.0.1125.51716), Arm64 RyuJIT armv8.0-a
DefaultJob : .NET 9.0.11 (9.0.11, 9.0.1125.51716), Arm64 RyuJIT armv8.0-a


| Method                        | KeySize | Mean       | Error     | StdDev     | Ratio | RatioSD | Rank | Gen0   | Allocated | Alloc Ratio |
|------------------------------ |-------- |-----------:|----------:|-----------:|------:|--------:|-----:|-------:|----------:|------------:|
| 'RingBuffer single-thread'    | 16      |   2.648 us | 0.0027 us |  0.0021 us |  0.56 |    0.01 |    1 |      - |         - |          NA |
| 'ArrayPool single-thread'     | 16      |   4.770 us | 0.0504 us |  0.0447 us |  1.00 |    0.01 |    2 |      - |         - |          NA |
| 'RingBuffer SPSC via Channel' | 16      | 131.704 us | 2.6277 us |  7.3250 us | 27.61 |    1.55 |    3 | 0.2441 |    2216 B |          NA |
| 'ArrayPool SPSC via Channel'  | 16      | 222.862 us | 4.6507 us | 13.0410 us | 46.73 |    2.75 |    4 | 0.2441 |    2303 B |          NA |
|                               |         |            |           |            |       |         |      |        |           |             |
| 'RingBuffer single-thread'    | 64      |   2.902 us | 0.0161 us |  0.0135 us |  0.58 |    0.00 |    1 |      - |         - |          NA |
| 'ArrayPool single-thread'     | 64      |   4.989 us | 0.0210 us |  0.0175 us |  1.00 |    0.00 |    2 |      - |         - |          NA |
| 'RingBuffer SPSC via Channel' | 64      | 138.005 us | 2.6566 us |  2.4850 us | 27.66 |    0.49 |    3 | 0.2441 |    2216 B |          NA |
| 'ArrayPool SPSC via Channel'  | 64      | 241.630 us | 4.7764 us | 13.0752 us | 48.43 |    2.61 |    4 | 0.2441 |    2393 B |          NA |
|                               |         |            |           |            |       |         |      |        |           |             |
| 'RingBuffer single-thread'    | 256     |   5.012 us | 0.0189 us |  0.0177 us |  0.78 |    0.01 |    1 |      - |         - |          NA |
| 'ArrayPool single-thread'     | 256     |   6.438 us | 0.0499 us |  0.0467 us |  1.00 |    0.01 |    2 |      - |         - |          NA |
| 'RingBuffer SPSC via Channel' | 256     | 141.648 us | 2.5220 us |  2.3591 us | 22.00 |    0.39 |    3 | 0.2441 |    2216 B |          NA |
| 'ArrayPool SPSC via Channel'  | 256     | 229.947 us | 4.5901 us |  4.9113 us | 35.72 |    0.78 |    4 | 0.2441 |    2227 B |          NA |




// * Summary *

BenchmarkDotNet v0.15.8, macOS Tahoe 26.2 (25C56) [Darwin 25.2.0]
Apple M4 Pro, 1 CPU, 12 logical and 12 physical cores
.NET SDK 9.0.307
[Host]     : .NET 9.0.11 (9.0.11, 9.0.1125.51716), Arm64 RyuJIT armv8.0-a
DefaultJob : .NET 9.0.11 (9.0.11, 9.0.1125.51716), Arm64 RyuJIT armv8.0-a


| Method                          | Size | Mean           | Error       | StdDev      | Ratio | RatioSD | Rank | Gen0    | Gen1   | Allocated | Alloc Ratio |
|-------------------------------- |----- |---------------:|------------:|------------:|------:|--------:|-----:|--------:|-------:|----------:|------------:|
| SortedArraySet_Iterate          | 10   |       2.913 ns |   0.0819 ns |   0.1251 ns | 0.006 |    0.00 |    1 |       - |      - |         - |        0.00 |
| ImmutableHashSet_Contains       | 10   |      15.415 ns |   0.2479 ns |   0.2319 ns | 0.032 |    0.00 |    2 |       - |      - |         - |        0.00 |
| ImmutableHashSet_AddDuplicate   | 10   |      18.866 ns |   0.1810 ns |   0.1604 ns | 0.039 |    0.00 |    3 |       - |      - |         - |        0.00 |
| SortedArraySet_AddDuplicate     | 10   |      42.236 ns |   0.4766 ns |   0.4225 ns | 0.088 |    0.00 |    4 |       - |      - |         - |        0.00 |
| SortedArraySet_Contains         | 10   |      48.572 ns |   0.8561 ns |   0.8008 ns | 0.101 |    0.00 |    5 |       - |      - |         - |        0.00 |
| SortedArraySet_AddOneByOne      | 10   |      85.695 ns |   1.6134 ns |   1.5092 ns | 0.179 |    0.00 |    6 |  0.0257 |      - |     216 B |        0.10 |
| ImmutableHashSet_Iterate        | 10   |     120.476 ns |   0.5665 ns |   0.5022 ns | 0.251 |    0.00 |    7 |       - |      - |         - |        0.00 |
| SortedArraySet_RemoveOneByOne   | 10   |     164.352 ns |   1.6701 ns |   1.5622 ns | 0.343 |    0.01 |    8 |  0.0114 |      - |      96 B |        0.04 |
| ImmutableHashSet_RemoveOneByOne | 10   |     287.406 ns |   2.6304 ns |   2.3318 ns | 0.599 |    0.01 |    9 |  0.1779 |      - |    1488 B |        0.65 |
| ImmutableHashSet_AddOneByOne    | 10   |     479.991 ns |   7.4954 ns |   8.6317 ns | 1.000 |    0.02 |   10 |  0.2708 |      - |    2272 B |        1.00 |
|                                 |      |                |             |             |       |         |      |         |        |           |             |
| SortedArraySet_Iterate          | 100  |      46.394 ns |   0.4771 ns |   0.4229 ns | 0.005 |    0.00 |    1 |       - |      - |         - |        0.00 |
| ImmutableHashSet_Contains       | 100  |     331.858 ns |   6.1089 ns |   7.0350 ns | 0.038 |    0.00 |    2 |       - |      - |         - |        0.00 |
| ImmutableHashSet_AddDuplicate   | 100  |     421.835 ns |   8.1013 ns |   9.3294 ns | 0.048 |    0.00 |    3 |       - |      - |         - |        0.00 |
| SortedArraySet_AddDuplicate     | 100  |     895.772 ns |   4.0885 ns |   3.8243 ns | 0.102 |    0.00 |    4 |       - |      - |         - |        0.00 |
| SortedArraySet_Contains         | 100  |     905.198 ns |   2.7544 ns |   2.4417 ns | 0.103 |    0.00 |    4 |       - |      - |         - |        0.00 |
| ImmutableHashSet_Iterate        | 100  |   1,232.048 ns |   4.0126 ns |   3.7534 ns | 0.140 |    0.00 |    5 |       - |      - |         - |        0.00 |
| SortedArraySet_AddOneByOne      | 100  |   1,624.610 ns |  11.6073 ns |   9.6926 ns | 0.185 |    0.00 |    6 |  0.1411 |      - |    1184 B |        0.03 |
| SortedArraySet_RemoveOneByOne   | 100  |   3,067.355 ns |  17.6230 ns |  16.4846 ns | 0.349 |    0.00 |    7 |  0.0534 |      - |     456 B |        0.01 |
| ImmutableHashSet_RemoveOneByOne | 100  |   6,474.842 ns |  28.7141 ns |  26.8592 ns | 0.736 |    0.00 |    8 |  3.7155 | 0.0229 |   31120 B |        0.77 |
| ImmutableHashSet_AddOneByOne    | 100  |   8,796.627 ns |  51.9852 ns |  48.6270 ns | 1.000 |    0.01 |    9 |  4.8370 | 0.0458 |   40584 B |        1.00 |
|                                 |      |                |             |             |       |         |      |         |        |           |             |
| SortedArraySet_Iterate          | 1000 |     512.802 ns |   1.4665 ns |   1.3000 ns | 0.004 |    0.00 |    1 |       - |      - |         - |       0.000 |
| ImmutableHashSet_Contains       | 1000 |   6,606.469 ns |  70.9393 ns |  66.3567 ns | 0.047 |    0.00 |    2 |       - |      - |         - |       0.000 |
| ImmutableHashSet_AddDuplicate   | 1000 |   7,410.626 ns |  45.7221 ns |  42.7684 ns | 0.052 |    0.00 |    3 |       - |      - |         - |       0.000 |
| ImmutableHashSet_Iterate        | 1000 |  13,487.104 ns |  62.5895 ns |  55.4840 ns | 0.095 |    0.00 |    4 |       - |      - |         - |       0.000 |
| SortedArraySet_AddDuplicate     | 1000 |  13,704.674 ns |  51.0595 ns |  47.7611 ns | 0.097 |    0.00 |    4 |       - |      - |         - |       0.000 |
| SortedArraySet_Contains         | 1000 |  13,709.137 ns | 133.9130 ns | 125.2623 ns | 0.097 |    0.00 |    4 |       - |      - |         - |       0.000 |
| SortedArraySet_AddOneByOne      | 1000 |  29,819.597 ns | 284.1037 ns | 265.7508 ns | 0.211 |    0.00 |    5 |  0.9766 |      - |    8424 B |       0.014 |
| SortedArraySet_RemoveOneByOne   | 1000 |  62,816.993 ns | 436.0578 ns | 364.1280 ns | 0.444 |    0.00 |    6 |  0.3662 |      - |    4056 B |       0.007 |
| ImmutableHashSet_RemoveOneByOne | 1000 | 116,827.809 ns | 887.5783 ns | 830.2413 ns | 0.826 |    0.01 |    7 | 59.9365 | 3.5400 |  501824 B |       0.842 |
| ImmutableHashSet_AddOneByOne    | 1000 | 141,414.297 ns | 717.2825 ns | 670.9465 ns | 1.000 |    0.01 |    8 | 71.0449 | 6.3477 |  595848 B |       1.000 |


# Bounded top-K paging inside the sorted Execute* terminals

Sorted `Execute*` terminals bound transparently when `take` is finite: the page is selected with a
heap of size `skip+take` instead of materializing and full-sorting every matched row; the joined
variants narrow inner joins without dictionary writes and fill join slots for the page rows only.
Same method names, same fixture and seed in both runs. ShortRun job (3 iterations) on a laptop —
Error is large on some classic rows, so read these as order-of-magnitude.

## Before (main @ 1bb8b1e — classic full-sort pipeline behind the sorted terminals)

```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.9106/25H2/2025Update/HudsonValley2)
AMD Ryzen 9 9955HX3D 2.50GHz, 1 CPU, 16 logical and 16 physical cores
.NET SDK 10.0.302
  [Host]   : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v4
  ShortRun : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v4

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method                        | N      | Take | Mean         | Error        | StdDev      | Ratio | RatioSD | Rank | Allocated | Alloc Ratio |
|------------------------------ |------- |----- |-------------:|-------------:|------------:|------:|--------:|-----:|----------:|------------:|
| Sorted_ExecutePooled          | 10000  | 20   |     588.2 μs |     15.39 μs |     0.84 μs |  1.00 |    0.00 |    1 |      64 B |        1.00 |
| SortedInnerJoin_ExecutePooled | 10000  | 20   |   1,312.5 μs |    163.19 μs |     8.95 μs |  2.23 |    0.01 |    2 |      24 B |        0.38 |
|                               |        |      |              |              |             |       |         |      |           |             |
| Sorted_ExecutePooled          | 10000  | 200  |     580.0 μs |     74.80 μs |     4.10 μs |  1.00 |    0.01 |    1 |      64 B |        1.00 |
| SortedInnerJoin_ExecutePooled | 10000  | 200  |   1,320.0 μs |    321.91 μs |    17.64 μs |  2.28 |    0.03 |    2 |      24 B |        0.38 |
|                               |        |      |              |              |             |       |         |      |           |             |
| Sorted_ExecutePooled          | 100000 | 20   |   9,872.8 μs |  4,060.66 μs |   222.58 μs |  1.00 |    0.03 |    1 |      64 B |        1.00 |
| SortedInnerJoin_ExecutePooled | 100000 | 20   |  22,113.6 μs | 15,811.40 μs |   866.68 μs |  2.24 |    0.09 |    2 |      24 B |        0.38 |
|                               |        |      |              |              |             |       |         |      |           |             |
| Sorted_ExecutePooled          | 100000 | 200  |   9,540.0 μs |  6,506.93 μs |   356.67 μs |  1.00 |    0.05 |    1 |      64 B |        1.00 |
| SortedInnerJoin_ExecutePooled | 100000 | 200  |  23,975.5 μs |  6,364.69 μs |   348.87 μs |  2.52 |    0.09 |    2 |      24 B |        0.38 |
|                               |        |      |              |              |             |       |         |      |           |             |
| Sorted_ExecutePooled          | 500000 | 20   |  79,272.2 μs | 63,079.22 μs | 3,457.59 μs |  1.00 |    0.05 |    1 |      71 B |        1.00 |
| SortedInnerJoin_ExecutePooled | 500000 | 20   | 181,552.2 μs | 10,783.08 μs |   591.06 μs |  2.29 |    0.09 |    2 |      24 B |        0.34 |
|                               |        |      |              |              |             |       |         |      |           |             |
| Sorted_ExecutePooled          | 500000 | 200  |  77,574.4 μs | 86,447.78 μs | 4,738.49 μs |  1.00 |    0.08 |    1 |      71 B |        1.00 |
| SortedInnerJoin_ExecutePooled | 500000 | 200  | 177,186.5 μs | 62,154.36 μs | 3,406.89 μs |  2.29 |    0.13 |    2 |      24 B |        0.34 |

## After (feature/top-k-bounded-execute — bounded pipeline)

`*_ClassicCore_Pooled` rows re-run the old pipeline through the internal cores for an
in-run baseline; they match the Before run above within noise.

```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.9106/25H2/2025Update/HudsonValley2)
AMD Ryzen 9 9955HX3D 2.50GHz, 1 CPU, 16 logical and 16 physical cores
.NET SDK 10.0.302
  [Host]   : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v4
  ShortRun : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v4

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method                             | N      | Take | Mean          | Error         | StdDev       | Ratio | RatioSD | Rank | Allocated | Alloc Ratio |
|----------------------------------- |------- |----- |--------------:|--------------:|-------------:|------:|--------:|-----:|----------:|------------:|
| Sorted_ExecutePooled               | 10000  | 20   |      36.20 μs |      1.287 μs |     0.071 μs |  0.06 |    0.00 |    1 |         - |        0.00 |
| SortedInnerJoin_ExecutePooled      | 10000  | 20   |     545.82 μs |     67.698 μs |     3.711 μs |  0.93 |    0.01 |    2 |         - |        0.00 |
| Sorted_ClassicCore_Pooled          | 10000  | 20   |     588.72 μs |     20.895 μs |     1.145 μs |  1.00 |    0.00 |    2 |      64 B |        1.00 |
| SortedInnerJoin_ClassicCore_Pooled | 10000  | 20   |   1,374.58 μs |     49.003 μs |     2.686 μs |  2.33 |    0.01 |    3 |      24 B |        0.38 |
|                                    |        |      |               |               |              |       |         |      |           |             |
| Sorted_ExecutePooled               | 10000  | 200  |      74.14 μs |     19.680 μs |     1.079 μs |  0.13 |    0.00 |    1 |         - |        0.00 |
| Sorted_ClassicCore_Pooled          | 10000  | 200  |     591.40 μs |     57.430 μs |     3.148 μs |  1.00 |    0.01 |    2 |      64 B |        1.00 |
| SortedInnerJoin_ExecutePooled      | 10000  | 200  |     650.56 μs |     40.846 μs |     2.239 μs |  1.10 |    0.01 |    2 |         - |        0.00 |
| SortedInnerJoin_ClassicCore_Pooled | 10000  | 200  |   1,355.27 μs |     42.077 μs |     2.306 μs |  2.29 |    0.01 |    3 |      24 B |        0.38 |
|                                    |        |      |               |               |              |       |         |      |           |             |
| Sorted_ExecutePooled               | 100000 | 20   |     320.24 μs |    115.373 μs |     6.324 μs |  0.04 |    0.00 |    1 |         - |        0.00 |
| SortedInnerJoin_ExecutePooled      | 100000 | 20   |   7,693.88 μs | 19,732.071 μs | 1,081.581 μs |  0.91 |    0.12 |    2 |         - |        0.00 |
| Sorted_ClassicCore_Pooled          | 100000 | 20   |   8,435.10 μs |  5,921.890 μs |   324.599 μs |  1.00 |    0.05 |    2 |      64 B |        1.00 |
| SortedInnerJoin_ClassicCore_Pooled | 100000 | 20   |  18,798.97 μs | 17,483.957 μs |   958.355 μs |  2.23 |    0.12 |    3 |      24 B |        0.38 |
|                                    |        |      |               |               |              |       |         |      |           |             |
| Sorted_ExecutePooled               | 100000 | 200  |     426.91 μs |    165.335 μs |     9.063 μs |  0.04 |    0.00 |    1 |         - |        0.00 |
| SortedInnerJoin_ExecutePooled      | 100000 | 200  |   4,703.28 μs |  1,796.008 μs |    98.445 μs |  0.49 |    0.01 |    2 |         - |        0.00 |
| Sorted_ClassicCore_Pooled          | 100000 | 200  |   9,573.03 μs |  4,707.966 μs |   258.059 μs |  1.00 |    0.03 |    3 |      64 B |        1.00 |
| SortedInnerJoin_ClassicCore_Pooled | 100000 | 200  |  17,759.79 μs |  6,534.758 μs |   358.192 μs |  1.86 |    0.05 |    4 |      24 B |        0.38 |
|                                    |        |      |               |               |              |       |         |      |           |             |
| Sorted_ExecutePooled               | 500000 | 20   |   9,562.42 μs |  3,199.751 μs |   175.389 μs |  0.13 |    0.01 |    1 |         - |        0.00 |
| Sorted_ClassicCore_Pooled          | 500000 | 20   |  76,017.93 μs | 58,690.756 μs | 3,217.038 μs |  1.00 |    0.05 |    2 |      71 B |        1.00 |
| SortedInnerJoin_ExecutePooled      | 500000 | 20   |  86,449.48 μs | 12,119.492 μs |   664.310 μs |  1.14 |    0.04 |    2 |     163 B |        2.30 |
| SortedInnerJoin_ClassicCore_Pooled | 500000 | 20   | 172,929.30 μs | 46,861.848 μs | 2,568.656 μs |  2.28 |    0.09 |    3 |      24 B |        0.34 |
|                                    |        |      |               |               |              |       |         |      |           |             |
| Sorted_ExecutePooled               | 500000 | 200  |   2,595.59 μs |    166.673 μs |     9.136 μs |  0.03 |    0.00 |    1 |         - |        0.00 |
| SortedInnerJoin_ExecutePooled      | 500000 | 200  |  47,703.38 μs | 21,792.732 μs | 1,194.533 μs |  0.63 |    0.03 |    2 |         - |        0.00 |
| Sorted_ClassicCore_Pooled          | 500000 | 200  |  75,950.17 μs | 62,090.966 μs | 3,403.415 μs |  1.00 |    0.06 |    3 |      71 B |        1.00 |
| SortedInnerJoin_ClassicCore_Pooled | 500000 | 200  | 114,746.22 μs |  8,132.138 μs |   445.750 μs |  1.51 |    0.06 |    4 |      24 B |        0.34 |
