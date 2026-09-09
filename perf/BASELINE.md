# Prague performance baseline
_Generated 2026-09-09T10:17:00Z_

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
`github-ubuntu` · `Ubuntu 24.04.4 LTS` · `.NET 9.0.20` · commit `0614b57`
| metric | value | unit |
|---|---:|---|
| read.throughput | 2001751.58 | reads/s |
| query.multiJoin.p50 | 505087.00 | ns |
| query.multiJoin.p99 | 12955647.00 | ns |
| query.multiJoin.p999 | 13702143.00 | ns |

### core-only  
`github-ubuntu` · `Ubuntu 24.04.4 LTS` · `.NET 9.0.20` · commit `0614b57`
| metric | value | unit |
|---|---:|---|
| ingest.throughput | 3762217.04 | ent/s |
| ingest.alloc | 196.32 | bytes |
| query.uniqueLookup.p50 | 376.10 | ns |
| query.uniqueLookup.alloc | 0.00 | bytes |
| query.rangeScan.p50 | 6284.47 | ns |
| query.rangeScan.alloc | 0.00 | bytes |
| query.joinOne.p50 | 19760.13 | ns |
| query.joinOne.alloc | 0.00 | bytes |
| query.joinMany.p50 | 292483.48 | ns |
| query.joinMany.alloc | 3.00 | bytes |
| query.multiJoin.p50 | 318056.73 | ns |
| query.multiJoin.alloc | 4.00 | bytes |

### core-sort  
`github-ubuntu` · `Ubuntu 24.04.4 LTS` · `.NET 9.0.20` · commit `0614b57`
| metric | value | unit |
|---|---:|---|
| query.sortDistinct.p50 | 17565.70 | ns |
| query.sortDistinct.alloc | 0.00 | bytes |
| query.sortTied.p50 | 14471.96 | ns |
| query.sortTied.alloc | 0.00 | bytes |
| query.sortTiedJoined.p50 | 637258.98 | ns |
| query.sortTiedJoined.alloc | 6.00 | bytes |
| query.sortTiedJoinedBoundedPage.p50 | 639786.85 | ns |
| query.sortTiedJoinedBoundedPage.alloc | 6.00 | bytes |
| query.sortTiedLeftThenJoinClassic.p50 | 65038.68 | ns |
| query.sortTiedLeftThenJoinClassic.alloc | 1.00 | bytes |
| query.sortTiedLeftThenJoinBounded.p50 | 33284.52 | ns |
| query.sortTiedLeftThenJoinBounded.alloc | 0.00 | bytes |
| query.sortBoundedPage.p50 | 9673.19 | ns |
| query.sortBoundedPage.alloc | 0.00 | bytes |
| query.sortBoundedFullPage.p50 | 55703.12 | ns |
| query.sortBoundedFullPage.alloc | 0.00 | bytes |

### full-sim  
`github-ubuntu` · `Ubuntu 24.04.4 LTS` · `.NET 9.0.20` · commit `0614b57`
| metric | value | unit |
|---|---:|---|
| ingest.throughput | 1029230.14 | ent/s |
| query.multiJoin.p50 | 313391.00 | ns |
| query.multiJoin.p99 | 693695.00 | ns |
| query.multiJoin.p999 | 802399.00 | ns |
