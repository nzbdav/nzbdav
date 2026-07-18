> [!IMPORTANT]
> This fork is designed to be a drop in replacement/upgrade from `nzbdav-dev/nzbdav v0.6.4`.
>
> Early adopters are reporting **2x network throughput** capability and a **400% reduction in seek time**.

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

Please add feature requests and issues over on our [Issue Tracker](https://github.com/nzbdav/nzbdav/issues) or join our [Discord `#nzbdav`](https://discord.gg/EJaptcg9UY) to chat with us!

## Why another fork?

This project is a maintained fork of [nzbdav-dev/nzbdav](https://github.com/nzbdav-dev/nzbdav). We took ownership of the full Usenet streaming stack — nzbdav, UsenetSharp, RapidYencSharp, rapidyenc, and SharpCompress — so playback, connection, and decoding fixes could land in the right layer instead of waiting on a single upstream dependency chain.

Read the full story in the [release announcement](https://nzbdav.github.io/nzbdav/release-announcement/).

## Special thanks

Special thanks to the forks and contributors whose ideas we absorbed:

* [@Nzbdav-dev](https://github.com/Nzbdav-dev)
* [@Pukabyte](https://github.com/Pukabyte)
* [@elfhosted](https://github.com/elfhosted)
* [@kha-kis](https://github.com/kha-kis)
* [@mrghxst](https://github.com/mrghxst)
* [qooode/nzbdavex](https://github.com/qooode/nzbdavex)

## Features

* 📁 **WebDAV server**
  Host your virtual filesystem over HTTP(S)

* ☁️ **Mount NZB documents**
  Browse NZB contents instantly, no download needed

* 📽️ **Full streaming & seeking**
  Jump to any point in your video streams

* 🚀 **NNTP article pipelining**
  Optional pipelined article fetches for higher throughput and faster seeks

* 🗃️ **Archive streaming**
  View, stream, and seek inside RAR and 7z archives

* 🔓 **Password-protected archives**
  Stream encrypted content transparently

* 🔀 **Multiple Usenet providers**
  Automatic failover with per-provider circuit breakers

* 📊 **Live operations dashboard**
  Throughput, latency, errors, active reads, provider usage, failover saves, and indexer activity

* 🧭 **Provider routing and limits**
  Cascade priorities, per-provider data caps, usage resets, and connection benchmarking

* 🔎 **Built-in indexer search**
  Configure Newznab indexers, track API usage, search them manually, and mount results

* 🚫 **Search exclude filters**
  Manual regex excludes plus auto-synced remote lists (e.g. TRaSH) with refresh status

* 🎛️ **Search profiles and adapters**
  Expose selected indexers through token-scoped Addon, Newznab, and JSON APIs

* 🐕 **Watchdog playback failover**
  Verify candidates, retry failed releases, and inspect each playback attempt

* 🛡️ **Warden dead-release ledger**
  Remember unavailable releases, combine trusted remote ledgers, and import, export, or back up the data

* 📡 **Watchtower proactive resolution**
  Keep wanted movies and episodes mapped to verified releases before playback

* 📜 **Live log viewer**
  Filter, follow, and download backend logs from the admin UI

* 🗂️ **WebDAV management**
  Browse, download, and delete eligible virtual filesystem items from the UI

* 💙 **Health checks & optional repairs**
  Monitor content health and trigger replacements through Radarr/Sonarr when configured

* 🧩 **SABnzbd-compatible API**
  Drop-in replacement for SABnzbd

* 🙌 **Sonarr/Radarr integration**
  Import through Rclone symlinks or lightweight STRM files

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

Full documentation is published at [nzbdav.github.io/nzbdav](https://nzbdav.github.io/nzbdav/).

The [0.7.x release announcement](https://nzbdav.github.io/nzbdav/release-announcement/) summarizes the coordinated stack releases (nzbdav, UsenetSharp, RapidYencSharp, rapidyenc), .NET 10 migration, network performance work, and our audit of upstream issues and PRs.

The [comprehensive setup guide](https://nzbdav.github.io/nzbdav/setup-guide/) covers everything needed for a full production deployment:

* **Docker Compose** — persistent deployment, container health checks, and updates
* **Import strategies** — Rclone symlinks for Plex or STRM files for Emby/Jellyfin
* **Performance tuning** — benchmarking WebDAV connection limits
* **Integrations** — automating Radarr/Sonarr queue management and repairs
* **Stremio** — streaming Usenet on demand via AIOStreams
* **Search profiles** — token-scoped Newznab, Addon, and JSON adapter setup
* **Watchtower** — proactive wanted-list resolution and readiness controls in the [Watchtower guide](https://nzbdav.github.io/nzbdav/watchtower/)

## Development

The project consists of a .NET backend (WebDAV, Usenet streaming, SAB API) and a React Router frontend (admin UI). See [CONTRIBUTING.md](CONTRIBUTING.md) for local development setup and [CHANGELOG.md](CHANGELOG.md) for release history. Source for the published docs lives in [`docs/`](docs/).

## License

NzbDav is released under the [MIT License](LICENSE).

> [!NOTE]
> NzbDav is intended for use with legally obtained content only. The project maintainers do not condone piracy and will not provide support for users suspected of engaging in copyright infringement.
