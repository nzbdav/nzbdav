# Technical support pack [since 0.9.0](https://github.com/nzbdav/nzbdav/releases/tag/v0.9.0){ .nzbdav-since }

**Settings → Support** generates a ZIP you can provide to trusted NzbDAV support
when troubleshooting an issue. The archive is streamed to your browser and is
not retained by NzbDAV.

## Included

- Recent backend logs from the in-memory log buffer
- Redacted active Settings and runtime/build information
- Aggregate provider throughput, outage, failover, and consumption metrics

Backend logs are memory-only and are cleared when NzbDAV restarts. Frontend and
container logs are not included.

## Privacy

The pack redacts passwords, API keys, tokens, URL credentials, sensitive URL
parameters, authorization values, and IP addresses. It does **not** anonymize
file names, filesystem paths, account usernames, DNS names, or non-secret URL
paths. Review the ZIP before sharing it.

The pack never includes databases, database backups, NZBs, blobs, environment
files, session or API-key files, crash dumps, stream traces, or segment-cache
data.
