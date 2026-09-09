# Prague performance baseline
_Generated 2026-09-09T09:22:10Z_

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
`cpu` · `Darwin 25.2.0 Darwin Kernel Version 25.2.0: Tue Nov 18 21:09:56 PST 2025; root:xnu-12377.61.12~1/RELEASE_ARM64_T6041` · `.NET 9.0.19` · commit `9d0a8e8`
| metric | value | unit |
|---|---:|---|
| ingest.throughput | 8349305.60 | ent/s |
| ingest.alloc | 208.02 | bytes |
| query.uniqueLookup.p50 | 215.08 | ns |
| query.uniqueLookup.alloc | 0.00 | bytes |
| query.rangeScan.p50 | 2931.99 | ns |
| query.rangeScan.alloc | 0.00 | bytes |
| query.joinOne.p50 | 8684.03 | ns |
| query.joinOne.alloc | 0.00 | bytes |
| query.joinMany.p50 | 98964.44 | ns |
| query.joinMany.alloc | 0.00 | bytes |
| query.multiJoin.p50 | 104678.10 | ns |
| query.multiJoin.alloc | 0.00 | bytes |

### core-sort  
`cpu` · `Darwin 25.2.0 Darwin Kernel Version 25.2.0: Tue Nov 18 21:09:56 PST 2025; root:xnu-12377.61.12~1/RELEASE_ARM64_T6041` · `.NET 9.0.19` · commit `ced408d`
| metric | value | unit |
|---|---:|---|
| query.sortDistinct.p50 | 11541.76 | ns |
| query.sortDistinct.alloc | 0.00 | bytes |
| query.sortTied.p50 | 9269.23 | ns |
| query.sortTied.alloc | 0.00 | bytes |
| query.sortTiedJoined.p50 | 227422.36 | ns |
| query.sortTiedJoined.alloc | 0.00 | bytes |
| query.sortTiedJoinedBoundedPage.p50 | 223486.94 | ns |
| query.sortTiedJoinedBoundedPage.alloc | 0.00 | bytes |
| query.sortTiedLeftThenJoinClassic.p50 | 61954.25 | ns |
| query.sortTiedLeftThenJoinClassic.alloc | 0.00 | bytes |
| query.sortTiedLeftThenJoinBounded.p50 | 20467.06 | ns |
| query.sortTiedLeftThenJoinBounded.alloc | 0.00 | bytes |
| query.sortBoundedPage.p50 | 7497.36 | ns |
| query.sortBoundedPage.alloc | 0.00 | bytes |
| query.sortBoundedFullPage.p50 | 32740.31 | ns |
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
`github-ubuntu` · `Ubuntu 24.04.4 LTS` · `.NET 9.0.20` · commit `c51d137`
| metric | value | unit |
|---|---:|---|
| read.throughput | 2073612.83 | reads/s |
| query.multiJoin.p50 | 514847.00 | ns |
| query.multiJoin.p99 | 12983807.00 | ns |
| query.multiJoin.p999 | 15355903.00 | ns |

### core-only  
`github-ubuntu` · `Ubuntu 24.04.4 LTS` · `.NET 9.0.20` · commit `c51d137`
| metric | value | unit |
|---|---:|---|
| ingest.throughput | 3540641.40 | ent/s |
| ingest.alloc | 196.33 | bytes |
| query.uniqueLookup.p50 | 375.80 | ns |
| query.uniqueLookup.alloc | 0.00 | bytes |
| query.rangeScan.p50 | 6193.97 | ns |
| query.rangeScan.alloc | 0.00 | bytes |
| query.joinOne.p50 | 19432.82 | ns |
| query.joinOne.alloc | 0.00 | bytes |
| query.joinMany.p50 | 296905.46 | ns |
| query.joinMany.alloc | 3.00 | bytes |
| query.multiJoin.p50 | 312607.49 | ns |
| query.multiJoin.alloc | 4.00 | bytes |

### core-sort  
`github-ubuntu` · `Ubuntu 24.04.4 LTS` · `.NET 9.0.20` · commit `c51d137`
| metric | value | unit |
|---|---:|---|
| query.sortDistinct.p50 | 16467.43 | ns |
| query.sortDistinct.alloc | 0.00 | bytes |
| query.sortTied.p50 | 14795.04 | ns |
| query.sortTied.alloc | 0.00 | bytes |
| query.sortTiedJoined.p50 | 750348.16 | ns |
| query.sortTiedJoined.alloc | 6.00 | bytes |
| query.sortTiedJoinedBoundedPage.p50 | 748095.91 | ns |
| query.sortTiedJoinedBoundedPage.alloc | 6.00 | bytes |
| query.sortTiedLeftThenJoinClassic.p50 | 166430.48 | ns |
| query.sortTiedLeftThenJoinClassic.alloc | 1.00 | bytes |
| query.sortTiedLeftThenJoinBounded.p50 | 33770.36 | ns |
| query.sortTiedLeftThenJoinBounded.alloc | 0.00 | bytes |
| query.sortBoundedPage.p50 | 9658.32 | ns |
| query.sortBoundedPage.alloc | 0.00 | bytes |
| query.sortBoundedFullPage.p50 | 56419.23 | ns |
| query.sortBoundedFullPage.alloc | 0.00 | bytes |

### full-sim  
`github-ubuntu` · `Ubuntu 24.04.4 LTS` · `.NET 9.0.20` · commit `c51d137`
| metric | value | unit |
|---|---:|---|
| ingest.throughput | 998384.43 | ent/s |
| query.multiJoin.p50 | 317695.00 | ns |
| query.multiJoin.p99 | 710143.00 | ns |
| query.multiJoin.p999 | 977215.00 | ns |
