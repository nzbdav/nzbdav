<p align="center">
  <img width="1101" height="238" alt="NzbDav" src="https://github.com/user-attachments/assets/b14165f4-24ff-4abe-8af6-3ca852e781d4" />
</p>

<h1 align="center">NzbDav</h1>

<p align="center">
  <strong>Mount NZBs as a virtual filesystem and stream directly from Usenet — without downloading full media files first.</strong>
</p>

<p align="center">
  <a href="https://github.com/nzbdav/nzbdav/releases"><img alt="Latest release" src="https://img.shields.io/github/v/release/nzbdav/nzbdav" /></a>
  <a href="https://github.com/nzbdav/nzbdav/pkgs/container/nzbdav"><img alt="Docker image" src="https://img.shields.io/badge/ghcr.io-nzbdav%2Fnzbdav-blue?logo=docker&logoColor=white" /></a>
  <a href="https://github.com/nzbdav/nzbdav/actions/workflows/ci.yml"><img alt="CI status" src="https://img.shields.io/github/actions/workflow/status/nzbdav/nzbdav/ci.yml?branch=main&label=CI" /></a>
  <a href="LICENSE"><img alt="License" src="https://img.shields.io/github/license/nzbdav/nzbdav" /></a>
</p>

---

NzbDav is a **WebDAV server** that mounts NZB documents as a browsable virtual filesystem — without downloading full media files first. Content streams on demand, straight from your Usenet provider.

It also exposes a **SABnzbd-compatible API**, so Sonarr, Radarr, and similar tools can use it as a drop-in download client. Combined with Plex, Emby, or Jellyfin, this lets you build an effectively infinite media library without storing the full media library on your server.

## Why another fork?

This project is a maintained fork of [nzbdav-dev/nzbdav](https://github.com/nzbdav-dev/nzbdav). We took ownership of the full Usenet streaming stack — nzbdav, UsenetSharp, RapidYencSharp, rapidyenc, and SharpCompress — so playback, connection, and decoding fixes could land in the right layer instead of waiting on a single upstream dependency chain.

Read the full story in the [release announcement](docs/release-announcement.md).

Early adopters are reporting **2x network throughput** capability and a **400% reduction in seek time**.

## Special thanks

Special thanks to the forks and contributors whose ideas we absorbed:

* [@Nzbdav-dev](https://github.com/Nzbdav-dev)
* [@Pukabyte](https://github.com/Pukabyte)
* [@elfhosted](https://github.com/elfhosted)
* [@kha-kis](https://github.com/kha-kis)
* [@mrghxst](https://github.com/mrghxst)
* [qooode/nzbdavex](https://github.com/qooode/nzbdavex)

## Features

* 📁 **WebDAV server** — host your virtual filesystem over HTTP(S)
* ☁️ **Mount NZB documents** — browse NZB contents instantly, no download needed
* 📽️ **Full streaming & seeking** — jump to any point in your video streams
* 🗃️ **Archive streaming** — view, stream, and seek inside RAR and 7z archives
* 🔓 **Password-protected archives** — stream encrypted content transparently
* 🔀 **Multiple Usenet providers** — automatic failover with per-provider circuit breakers
* 📊 **Live operations dashboard** — throughput, latency, errors, active reads, provider usage, failover saves, and indexer activity
* 🧭 **Provider routing and limits** — cascade priorities, per-provider data caps, usage resets, and connection benchmarking
* 🔎 **Built-in indexer search** — configure Newznab indexers, track API usage, search them manually, and mount results
* 🎛️ **Search profiles and adapters** — expose selected indexers through token-scoped Addon, Newznab, and JSON APIs
* 🐕 **Watchdog playback failover** — verify candidates, retry failed releases, and inspect each playback attempt
* 🛡️ **Warden dead-release ledger** — remember unavailable releases, combine trusted remote ledgers, and import, export, or back up the data
* 📡 **Watchtower proactive resolution** — keep wanted movies and episodes mapped to verified releases before playback
* 📜 **Live log viewer** — filter, follow, and download backend logs from the admin UI
* 🗂️ **WebDAV management** — browse, download, and delete eligible virtual filesystem items from the UI
* 💙 **Health checks & optional repairs** — monitor content health and trigger replacements through Radarr/Sonarr when configured
* 🧩 **SABnzbd-compatible API** — drop-in replacement for SABnzbd
* 🙌 **Sonarr/Radarr integration** — import through Rclone symlinks or lightweight STRM files

## Quick start

NzbDav ships as a single Docker image. To try it out:

```bash
docker run --rm -it -p 3000:3000 ghcr.io/nzbdav/nzbdav:latest
```

This trial command is ephemeral: its settings are discarded when the container exits.

For a persistent setup, use Docker Compose:

```yaml
services:
  nzbdav:
    image: ghcr.io/nzbdav/nzbdav:latest
    container_name: nzbdav
    restart: unless-stopped
    ports:
      - "3000:3000"
    environment:
      PUID: "1000"
      PGID: "1000"
      TZ: Etc/UTC
    volumes:
      - ./config:/config
```

Then open `http://localhost:3000`, create your admin account, and head to the **Settings** page to configure your Usenet provider:

> [!IMPORTANT]
> Port `3000` serves plain HTTP. If NzbDav will be reachable outside your trusted network, put it behind an HTTPS reverse proxy and do not expose the container port directly to the internet. WebDAV uses Basic authentication, so TLS is essential for remote access. When the proxy runs on the Docker host, bind the port to localhost with `127.0.0.1:3000:3000`.

You'll also want to set a username and password for the WebDAV server itself.

## Documentation

The [0.7.x release announcement](docs/release-announcement.md) summarizes the coordinated stack releases (nzbdav, UsenetSharp, RapidYencSharp, rapidyenc), .NET 10 migration, network performance work, and our audit of upstream issues and PRs.

The [comprehensive setup guide](docs/setup-guide.md) covers everything needed for a full production deployment:

* **Docker Compose** — persistent deployment, container health checks, and updates
* **Import strategies** — Rclone symlinks for Plex or STRM files for Emby/Jellyfin
* **Performance tuning** — benchmarking WebDAV connection limits
* **Integrations** — automating Radarr/Sonarr queue management and repairs
* **Stremio** — streaming Usenet on demand via AIOStreams
* **Search profiles** — token-scoped Newznab, Addon, and JSON adapter setup
* **Watchtower** — proactive wanted-list resolution and readiness controls in the [Watchtower guide](docs/watchtower.md)

## Development

The project consists of a .NET backend (WebDAV, Usenet streaming, SAB API) and a React Router frontend (admin UI). See [CONTRIBUTING.md](CONTRIBUTING.md) for local development setup and [CHANGELOG.md](CHANGELOG.md) for release history.

## License

NzbDav is released under the [MIT License](LICENSE).

> [!NOTE]
> NzbDav is intended for use with legally obtained content only. The project maintainers do not condone piracy and will not provide support for users suspected of engaging in copyright infringement.
