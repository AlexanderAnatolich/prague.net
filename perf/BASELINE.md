# Prague performance baseline
_Generated 2026-09-08T23:50:19Z_

## apple-m4pro-darwin

### concurrent  
`Apple M4 Pro` · `Darwin 25.2.0 Darwin Kernel Version 25.2.0: Tue Nov 18 21:09:56 PST 2025; root:xnu-12377.61.12~1/RELEASE_ARM64_T6041` · `.NET 9.0.17` · commit `420d77b`
| metric | value | unit |
|---|---:|---|
| read.throughput | 14916033.75 | reads/s |
| query.multiJoin.p50 | 175087.00 | ns |
| query.multiJoin.p99 | 7778047.00 | ns |
| query.multiJoin.p999 | 34099199.00 | ns |

### core-only  
`Apple M4 Pro` · `Darwin 25.2.0 Darwin Kernel Version 25.2.0: Tue Nov 18 21:09:56 PST 2025; root:xnu-12377.61.12~1/RELEASE_ARM64_T6041` · `.NET 9.0.17` · commit `420d77b`
| metric | value | unit |
|---|---:|---|
| ingest.throughput | 7689081.85 | ent/s |
| ingest.alloc | 265.84 | bytes |
| query.uniqueLookup.p50 | 216.94 | ns |
| query.uniqueLookup.alloc | 0.00 | bytes |
| query.rangeScan.p50 | 2934.13 | ns |
| query.rangeScan.alloc | 64.00 | bytes |
| query.joinOne.p50 | 8514.58 | ns |
| query.joinOne.alloc | 64.00 | bytes |
| query.joinMany.p50 | 104405.79 | ns |
| query.joinMany.alloc | 65.00 | bytes |
| query.multiJoin.p50 | 109515.32 | ns |
| query.multiJoin.alloc | 65.00 | bytes |

### core-sort  
`cpu` · `Darwin 25.2.0 Darwin Kernel Version 25.2.0: Tue Nov 18 21:09:56 PST 2025; root:xnu-12377.61.12~1/RELEASE_ARM64_T6041` · `.NET 9.0.19` · commit `5d5a794`
| metric | value | unit |
|---|---:|---|
| query.sortDistinct.p50 | 18912.30 | ns |
| query.sortDistinct.alloc | 88.00 | bytes |
| query.sortTied.p50 | 16050.10 | ns |
| query.sortTied.alloc | 88.00 | bytes |
| query.sortTiedJoined.p50 | 218185.44 | ns |
| query.sortBoundedPage.p50 | 7950.49 | ns |
| query.sortBoundedPage.alloc | 0.00 | bytes |
| query.sortBoundedFullPage.p50 | 34954.20 | ns |
| query.sortBoundedFullPage.alloc | 1.00 | bytes |

### full-sim  
`Apple M4 Pro` · `Darwin 25.2.0 Darwin Kernel Version 25.2.0: Tue Nov 18 21:09:56 PST 2025; root:xnu-12377.61.12~1/RELEASE_ARM64_T6041` · `.NET 9.0.17` · commit `420d77b`
| metric | value | unit |
|---|---:|---|
| ingest.throughput | 1395903.66 | ent/s |
| query.multiJoin.p50 | 120835.00 | ns |
| query.multiJoin.p99 | 274751.00 | ns |
| query.multiJoin.p999 | 410047.00 | ns |

## linux-x64-ci

### concurrent  
`github-ubuntu` · `Ubuntu 24.04.4 LTS` · `.NET 9.0.18` · commit `81013b8`
| metric | value | unit |
|---|---:|---|
| read.throughput | 1798093.18 | reads/s |
| query.multiJoin.p50 | 581535.00 | ns |
| query.multiJoin.p99 | 13113343.00 | ns |
| query.multiJoin.p999 | 15152639.00 | ns |

### core-only  
`github-ubuntu` · `Ubuntu 24.04.4 LTS` · `.NET 9.0.18` · commit `81013b8`
| metric | value | unit |
|---|---:|---|
| ingest.throughput | 3733693.98 | ent/s |
| ingest.alloc | 254.13 | bytes |
| query.uniqueLookup.p50 | 383.69 | ns |
| query.uniqueLookup.alloc | 0.00 | bytes |
| query.rangeScan.p50 | 6040.72 | ns |
| query.rangeScan.alloc | 64.00 | bytes |
| query.joinOne.p50 | 19709.36 | ns |
| query.joinOne.alloc | 64.00 | bytes |
| query.joinMany.p50 | 349655.63 | ns |
| query.joinMany.alloc | 67.00 | bytes |
| query.multiJoin.p50 | 368968.39 | ns |
| query.multiJoin.alloc | 67.00 | bytes |

### full-sim  
`github-ubuntu` · `Ubuntu 24.04.4 LTS` · `.NET 9.0.18` · commit `81013b8`
| metric | value | unit |
|---|---:|---|
| ingest.throughput | 953627.69 | ent/s |
| query.multiJoin.p50 | 386031.00 | ns |
| query.multiJoin.p99 | 825919.00 | ns |
| query.multiJoin.p999 | 1105727.00 | ns |
