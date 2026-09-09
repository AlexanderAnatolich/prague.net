# Prague performance baseline
_Generated 2026-09-09T10:25:16Z_

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
`cpu` · `Darwin 25.2.0 Darwin Kernel Version 25.2.0: Tue Nov 18 21:09:56 PST 2025; root:xnu-12377.61.12~1/RELEASE_ARM64_T6041` · `.NET 9.0.19` · commit `a2ae5b3`
| metric | value | unit |
|---|---:|---|
| ingest.throughput | 8255946.43 | ent/s |
| ingest.alloc | 208.02 | bytes |
| query.uniqueLookup.p50 | 216.35 | ns |
| query.uniqueLookup.alloc | 0.00 | bytes |
| query.rangeScan.p50 | 2968.43 | ns |
| query.rangeScan.alloc | 0.00 | bytes |
| query.joinOne.p50 | 8592.81 | ns |
| query.joinOne.alloc | 0.00 | bytes |
| query.joinMany.p50 | 95495.58 | ns |
| query.joinMany.alloc | 0.00 | bytes |
| query.joinManyAll.p50 | 183738.14 | ns |
| query.joinManyAll.alloc | 0.00 | bytes |
| query.multiJoin.p50 | 100636.73 | ns |
| query.multiJoin.alloc | 0.00 | bytes |

### core-sort  
`cpu` · `Darwin 25.2.0 Darwin Kernel Version 25.2.0: Tue Nov 18 21:09:56 PST 2025; root:xnu-12377.61.12~1/RELEASE_ARM64_T6041` · `.NET 9.0.19` · commit `a2ae5b3`
| metric | value | unit |
|---|---:|---|
| query.sortDistinct.p50 | 11499.75 | ns |
| query.sortDistinct.alloc | 0.00 | bytes |
| query.sortTied.p50 | 9248.98 | ns |
| query.sortTied.alloc | 0.00 | bytes |
| query.sortTiedJoined.p50 | 193562.46 | ns |
| query.sortTiedJoined.alloc | 0.00 | bytes |
| query.sortTiedJoinedBoundedPage.p50 | 192294.20 | ns |
| query.sortTiedJoinedBoundedPage.alloc | 0.00 | bytes |
| query.sortTiedLeftThenJoinClassic.p50 | 30326.28 | ns |
| query.sortTiedLeftThenJoinClassic.alloc | 0.00 | bytes |
| query.sortTiedLeftThenJoinBounded.p50 | 20118.84 | ns |
| query.sortTiedLeftThenJoinBounded.alloc | 0.00 | bytes |
| query.sortBoundedPage.p50 | 7805.39 | ns |
| query.sortBoundedPage.alloc | 0.00 | bytes |
| query.sortBoundedFullPage.p50 | 34472.75 | ns |
| query.sortBoundedFullPage.alloc | 0.00 | bytes |

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
`github-ubuntu` · `Ubuntu 24.04.4 LTS` · `.NET 9.0.20` · commit `9b34732`
| metric | value | unit |
|---|---:|---|
| read.throughput | 2873005.65 | reads/s |
| query.multiJoin.p50 | 367567.00 | ns |
| query.multiJoin.p99 | 12524031.00 | ns |
| query.multiJoin.p999 | 13767679.00 | ns |

### core-only  
`github-ubuntu` · `Ubuntu 24.04.4 LTS` · `.NET 9.0.20` · commit `9b34732`
| metric | value | unit |
|---|---:|---|
| ingest.throughput | 4655727.86 | ent/s |
| ingest.alloc | 196.33 | bytes |
| query.uniqueLookup.p50 | 292.69 | ns |
| query.uniqueLookup.alloc | 0.00 | bytes |
| query.rangeScan.p50 | 4731.69 | ns |
| query.rangeScan.alloc | 0.00 | bytes |
| query.joinOne.p50 | 15338.01 | ns |
| query.joinOne.alloc | 0.00 | bytes |
| query.joinMany.p50 | 182444.62 | ns |
| query.joinMany.alloc | 2.00 | bytes |
| query.joinManyAll.p50 | 357359.59 | ns |
| query.joinManyAll.alloc | 3.00 | bytes |
| query.multiJoin.p50 | 196820.15 | ns |
| query.multiJoin.alloc | 2.00 | bytes |

### core-sort  
`github-ubuntu` · `Ubuntu 24.04.4 LTS` · `.NET 9.0.20` · commit `9b34732`
| metric | value | unit |
|---|---:|---|
| query.sortDistinct.p50 | 13191.28 | ns |
| query.sortDistinct.alloc | 0.00 | bytes |
| query.sortTied.p50 | 11141.38 | ns |
| query.sortTied.alloc | 0.00 | bytes |
| query.sortTiedJoined.p50 | 384178.90 | ns |
| query.sortTiedJoined.alloc | 2.00 | bytes |
| query.sortTiedJoinedBoundedPage.p50 | 382672.00 | ns |
| query.sortTiedJoinedBoundedPage.alloc | 3.00 | bytes |
| query.sortTiedLeftThenJoinClassic.p50 | 47205.45 | ns |
| query.sortTiedLeftThenJoinClassic.alloc | 0.00 | bytes |
| query.sortTiedLeftThenJoinBounded.p50 | 24998.33 | ns |
| query.sortTiedLeftThenJoinBounded.alloc | 0.00 | bytes |
| query.sortBoundedPage.p50 | 7792.95 | ns |
| query.sortBoundedPage.alloc | 0.00 | bytes |
| query.sortBoundedFullPage.p50 | 43636.19 | ns |
| query.sortBoundedFullPage.alloc | 0.00 | bytes |

### full-sim  
`github-ubuntu` · `Ubuntu 24.04.4 LTS` · `.NET 9.0.20` · commit `9b34732`
| metric | value | unit |
|---|---:|---|
| ingest.throughput | 1176105.81 | ent/s |
| query.multiJoin.p50 | 199159.00 | ns |
| query.multiJoin.p99 | 421583.00 | ns |
| query.multiJoin.p999 | 596959.00 | ns |
