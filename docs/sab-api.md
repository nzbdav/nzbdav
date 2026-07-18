# SABnzbd API compatibility

NzbDav implements the SABnzbd-compatible operations used by Sonarr, Radarr, and
similar download clients. It is not a complete replacement for SABnzbd's
administrative API.

## Supported operations

- `version`, `status`, `fullstatus`, `get_config`, and `get_cats`
- `addfile` and `addurl`
- `queue` listing and `queue&name=delete`
- `history` listing and `history&name=delete`

Queue and history filters accept both `cat` and `category`. The default category
sentinel returned by `get_cats` is `*`.

## Intentional differences

- Job identifiers are UUIDs rather than `SABnzbd_nzo_*` strings. Treat them as
  opaque values and return them unchanged in later requests.
- Responses are JSON. The `output=xml` option is not implemented.
- Queue and history roots contain the fields needed by supported download
  clients rather than every SABnzbd UI field.
- History has no separate archive tier. `history&name=delete` permanently
  removes matching history rows whether `archive` is omitted, `1`, or `0`.
- The configured **Ignore SAB history limit** option can ignore a client's
  `limit` value. NzbDav still enforces its server-side maximum page size.
- Authentication failures use HTTP error status codes instead of always
  returning HTTP 200 with an error body.

## Delete behavior

Queue delete accepts a UUID, comma-separated UUIDs, repeated `value`
parameters, or `value=all`. The SAB `del_files=1` flag is accepted but has no
additional effect because NzbDav does not keep an incomplete-download
directory.

History delete accepts UUIDs, comma-separated UUIDs, `value=all`, or
`value=failed`. SAB's `del_files=1` applies only to failed-job files; failed
NzbDav jobs never mount WebDAV content, so the flag has no additional effect.

The NzbDav admin UI can explicitly delete mounted content for completed jobs
with the NzbDav-specific `del_completed_files=1` parameter. Download clients
should not send this parameter after importing a symlink or STRM file, because
the mounted source must remain available for playback.
