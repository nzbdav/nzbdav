> **Historical document (frozen as of 0.7.2).**  
> This announcement captures the coordinated 0.7.2 stack release. Version numbers below are **not** kept current — see [GitHub Releases](https://github.com/nzbdav/nzbdav/releases) for the latest. Do not bump the tables in this file.

# NzbDav stack release announcement (historical, 0.7.2)

This document summarizes the coordinated releases across the [nzbdav](https://github.com/nzbdav) organization: a fork of [nzbdav-dev/nzbdav](https://github.com/nzbdav-dev/nzbdav) with ownership of the full Usenet streaming dependency tree.

**Latest releases**

| Project | Version | Link |
|---------|---------|------|
| nzbdav | **0.7.2** | [Release](https://github.com/nzbdav/nzbdav/releases/tag/v0.7.2) |
| UsenetSharp | **2.0.2** | [Release](https://github.com/nzbdav/UsenetSharp/releases/tag/v2.0.2) |
| RapidYencSharp | **2.0.2** | [Release](https://github.com/nzbdav/RapidYencSharp/releases/tag/v2.0.2) |
| rapidyenc | **1.2.1** | [Release](https://github.com/nzbdav/rapidyenc/releases/tag/v1.2.1) |

**Docker:** `ghcr.io/nzbdav/nzbdav:0.7.2` (or `:latest` after publish)  
**NuGet:** `NzbDav.UsenetSharp` 2.0.2, `NzbDav.RapidYencSharp` 2.0.2

---

## Why we rebuilt the stack

Most reports of choppy playback, stalled downloads, and “not connected to server” errors trace through the same dependency chain—not a single UI bug. We took ownership of each layer so network performance fixes could land in the right place:

```
Plex / Radarr / rclone / Stremio
              │
              ▼
         nzbdav (v0.7.2)
    connection pooling, pipelining policy,
    playback resilience, WebDAV streaming
              │
              ▼
      UsenetSharp (v2.0.2)
    NNTP client, BODY pipelining, streaming reads
              │
              ▼
    RapidYencSharp (v2.0.2)
         managed native interop
              │
              ▼
       rapidyenc (v1.2.1)
         native yEnc decode
```

---

## .NET 10 across the stack

The C# toolchain now targets **.NET 10** end to end:

| Project | Change |
|---------|--------|
| **rapidyenc** | Native library + CI regression guardrails (v1.2.x) |
| **RapidYencSharp** | v2.0.0 — requires .NET 10; published as `NzbDav.RapidYencSharp` |
| **UsenetSharp** | v2.0.0 — `net10.0` only; hot-path optimizations |
| **nzbdav** | `net10.0` backend; Docker builds on `mcr.microsoft.com/dotnet/sdk:10.0-alpine` |

This aligns runtime, native interop, and container images on one supported baseline while connection and streaming behavior were reworked.

---

## Layer 1: rapidyenc (native decode)

Every article body passes through yEnc decode. Throughput and correctness here directly affect streaming latency.

**v1.2.0**

- Native regression guardrails in CI
- Windows CLI fix to preserve binary data on decode

**v1.2.1**

- Release pipeline hardening

---

## Layer 2: RapidYencSharp (managed interop)

**v2.0.0** (breaking)

- Targets **.NET 10** only

**v2.0.1 / v2.0.2**

- Published under the `NzbDav.RapidYencSharp` NuGet package id for reliable consumption from UsenetSharp and nzbdav

---

## Layer 3: UsenetSharp (NNTP + streaming)

`NzbDav.UsenetSharp` on NuGet is where most wire-level performance work landed.

### Connection reliability

- **TCP_NODELAY** and **TCP keepalive** on connect (v1.0.8)
- Configurable read timeouts and bounded drain of abandoned bodies
- Connection health APIs (`IsConnected`, `IsHealthy`) for pool consumers
- Fixes for disposal races, stale cancellation, and poisoned connections (v1.2.1–v1.2.4)

### Throughput on the wire

- **Pipelined decoded `BODY` commands** (v1.2.0) — multiple requests per connection without waiting for each response
- **64 KiB chunked article reads** and streaming body bytes without per-line string allocation (v1.1.0)
- **Chunked yEnc decode** with optional CRC32 validation (v1.1.0)
- Coalesced read timeouts across body reads

### Runtime

- **v2.0.0** — .NET 10 + hot-path optimizations
- **v2.0.2** — Preserve reader state across cancelled refills (reduces pipeline stalls after seek/cancel)

nzbdav **0.6.8 → 0.7.2** tracks UsenetSharp **1.2.2 → 2.0.2** as these improvements shipped.

---

## Layer 4: nzbdav (application + WebDAV)

Fork baseline was roughly upstream **v0.6.4**; the fork release line is now **v0.7.2**.

### Network and NNTP performance (0.6.5 → 0.7.2)

**Connection management**

- Provider **circuit breaker** and multi-provider failover
- Stop reusing **connections poisoned by cancellation**
- Treat unexpected NNTP responses as **retryable connection failures**
- Separate **streaming vs queue connection budgets** with prioritized semaphores
- Keep **paused streaming connections warm longer**
- SQLite **WAL + busy timeout** to reduce database stalls under concurrent load

**Pipelining end to end**

- Pipeline segment **BODY** requests for WebDAV read-ahead (0.6.8)
- Route pipelined fetches through **UsenetSharp `DecodedBodiesAsync` batch API** (0.7.2)
- Configurable toggles for queue pipelining (`usenet.pipelining.*`) and WebDAV pipelined body requests
- Close pipelined transfer **lifecycle gaps** (callbacks, permits, ordering)

See [nntp-pipelining.md](./nntp-pipelining.md) for settings and architecture.

**WebDAV streaming**

- Persist **segment byte ranges** for arithmetic seeks
- **Prefetch on small forward seeks**; pooled response copy buffers
- Correct **suffix byte ranges** (HTTP 416 instead of 500 past boundary)
- Enforce stream **lifecycle contracts** on cancel/seek
- AES decrypt in **256 KiB runs**; seek-prefix discard in **64 KiB chunks**

**Playback resilience (0.7.0+)**

- **Watchtower** — proactive Usenet discovery (see [watchtower.md](./watchtower.md))
- **PlaybackFastVerifier** — fast-fail segment checks with timeout budgets
- Play/watchdog orchestration for candidate fallback

**Stability fixes reported upstream**

- Idempotent **history delete** (fixes *Arr infinite retry loops on HTTP 500)
- Queue processing **starts at startup** (addresses “downloads stop until restart”)
- Fail queue items with **missing NZB blobs** instead of blocking the entire queue
- Provider-tagged logging for connection errors

### Beyond networking

- Full **UI restyle** (Tailwind design system)
- **0.7.0** — new persistence schemas; observability and discovery workflows
- Packages published to **NuGet.org**; Docker images on **GHCR** (multi-arch)

---

## Symptoms mapped to fixes

| Symptom | Where we fixed it |
|---------|-------------------|
| Slow first byte / low Mbps on seek | UsenetSharp pipelining + chunked reads; nzbdav prefetch |
| Playback freezes ~40s then recovers | UsenetSharp timeouts; connection poisoning fixes; 0.7 playback fast-fail |
| “Not connected” / `ConnectAsync` errors | UsenetSharp connection health; nzbdav pool lifecycle |
| Downloads stop until container restart | nzbdav queue startup; NNTP pool recovery |
| High CPU during yEnc on large segments | rapidyenc + RapidYencSharp + UsenetSharp zero-copy paths |
| *Arr hammers history delete | nzbdav idempotent SAB history delete |

---

## Auditing the parent project backlog

We are not building in isolation. [nzbdav-dev/nzbdav](https://github.com/nzbdav-dev/nzbdav) still has a large backlog of open community issues and pull requests. We are systematically auditing that backlog against our fork and tracking results with the **`upstream-audit`** label on [nzbdav/nzbdav](https://github.com/nzbdav/nzbdav).

### Open pull request audit

We evaluated **17 non-Dependabot open PRs** on the parent repo. Each still-relevant item is tracked on the fork (issues [#47–#60](https://github.com/nzbdav/nzbdav/issues?q=label%3Aupstream-audit+%22upstream-audit%22+-%22upstream-issue-audit%22)).

Examples:

- **Already addressed in our fork:** idempotent history delete (parent PR #439)
- **Still on our radar:** duplicate segment fallback (#310), `/content` recovery (#311), `LISTEN_ADDRESS` (#420), Lidarr (#421), native `URL_BASE` (#437)

### Open issue audit

We evaluated **116 open issues** on the parent repo:

- **[#62](https://github.com/nzbdav/nzbdav/issues/62)** — consolidated tracker for ~31 issues we believe **v0.7.2 already addresses**, with cross-repo mentions to `nzbdav-dev/nzbdav#NNN`
- **[#63–#147](https://github.com/nzbdav/nzbdav/issues?q=label%3Aupstream-audit+%22upstream-issue-audit%22)** — individual trackers for bugs and features that still apply, partially apply, or need verification

Each audit item documents the upstream reference, fork state, likely cause, and likely fix so we can cherry-pick or reimplement without blindly merging diverged code.

**Filter all audit work:** [github.com/nzbdav/nzbdav/issues?q=label:upstream-audit](https://github.com/nzbdav/nzbdav/issues?q=label%3Aupstream-audit+is%3Aopen)

---

## Upgrade notes

- **Docker:** repull `nzbdav/nzbdav:0.7.2` (or your configured tag). Persist `/config` as before.
- **Breaking (0.7.0):** new persistence schemas and operational configuration for the 0.7 line—review [CHANGELOG.md](../CHANGELOG.md) before upgrading production instances.
- **UsenetSharp 2.x:** requires **.NET 10**; bundled in official Docker images.
- **Pipelining:** validate against your providers with Settings → Usenet → **NNTP Pipelining** and the provider benchmark before relying on it for large queue imports.

---

## Further reading

- [CHANGELOG.md](../CHANGELOG.md) — full version history
- [nntp-pipelining.md](./nntp-pipelining.md) — pipelining settings and client chain
- [watchtower.md](./watchtower.md) — proactive discovery (0.7.0+)
- [setup-guide.md](./setup-guide.md) — deployment and integrations
