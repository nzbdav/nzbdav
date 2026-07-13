# Comprehensive NzbDav Setup Guide

An opinionated, step-by-step walkthrough for setting up NzbDav for maximum performance ("infinite library" style) with Radarr, Sonarr, Plex, Emby/Jellyfin, and Stremio.

## Table of contents

1. [How the "infinite library" works](#how-the-infinite-library-works)
2. [Phase 1 — Prerequisites](#phase-1--prerequisites)
3. [Phase 2 — Initial deployment](#phase-2--initial-deployment)
4. [Phase 3 — Rclone sidecar (symlink imports only)](#phase-3--rclone-sidecar-symlink-imports-only)
5. [Phase 4 — Integrations](#phase-4--integrations)
6. [Phase 5 — Usenet streaming in Stremio (via AIOStreams)](#phase-5--usenet-streaming-in-stremio-via-aiostreams)
7. [Phase 6 — Search profiles and adapters](#phase-6--search-profiles-and-adapters)
8. [Phase 7 — Operations](#phase-7--operations)

## How the "infinite library" works

Before configuring anything, it helps to understand the flow.

### Path A: The automation flow (Radarr/Sonarr + Plex/Jellyfin)

1. **Radarr/Sonarr** sends an `.nzb` file to NzbDav (acting as a download client) to "download".
2. **NzbDav** mounts the NZB onto the WebDAV without actually downloading it.
3. **NzbDav** creates the import artifact selected under `Settings` → `SABnzbd`:
   * **Symlinks — Plex:** NzbDav exposes symlinks under `/completed-symlinks`. Rclone translates them into filesystem symlinks.
   * **STRM Files — Emby/Jellyfin:** NzbDav writes small `.strm` files containing authenticated streaming URLs. Rclone is not required.
4. **Radarr/Sonarr** imports the symlinks or STRM files into your media library (e.g. `/mnt/media/movies`).
5. Your media server follows the symlink or STRM URL → NzbDav → Usenet provider.

> [!NOTE]
> NzbDav avoids storing full media files. The symlink workflow can still use a bounded local Rclone VFS cache to smooth seeking and high-bitrate playback.

### Path B: The on-demand flow (Stremio)

1. **Stremio (via AIOStreams)** searches your indexers using the Newznab addon and finds a release.
2. **AIOStreams** sends the `.nzb` to NzbDav's API to mount it.
3. **NzbDav** mounts the file instantly via WebDAV.
4. **AIOStreams** generates a streamable URL.
   * With the recommended proxy setup, this URL points to AIOStreams, which tunnels the traffic from NzbDav.
5. **Stremio** plays the video from that URL (bypassing Rclone/symlinks entirely).

---

## Phase 1 — Prerequisites

### 1. Usenet provider

You need a Usenet provider to download content. Consult the [Usenet Providers Wiki](https://www.reddit.com/r/usenet/wiki/providerdeals/) for a full list.

### 2. Indexers

You need Usenet indexers to find content. Consult the [Usenet Indexers Wiki](https://www.reddit.com/r/usenet/wiki/indexers/) for a full list.

Configure them in NzbDav under `Settings → Indexers`, and (optionally) in any external automation tool you use.

### 3. Docker

Install a current Docker Engine and the Docker Compose v2 plugin. The Rclone symlink workflow additionally requires a Linux host with `/dev/fuse` available. The STRM workflow does not require FUSE or Rclone.

---

## Phase 2 — Initial deployment

We start with a basic NzbDav container.

We use the pre-built multi-arch image published to GHCR (`ghcr.io/nzbdav/nzbdav`). No clone or build needed.

> **IPv6-only host?** `ghcr.io` is not reachable over IPv6. The same images are mirrored to Docker Hub (IPv6-capable) — replace `ghcr.io/nzbdav/nzbdav` with `nzbdav/nzbdav` in the `image:` lines throughout this guide.

> Prefer to build from source? Clone the repo (`git clone https://github.com/nzbdav/nzbdav.git`) and replace the `image:` line in the compose below with `build: /path/to/nzbdav`.

### 1. Create `docker-compose.yml`

Create the file structure like below:

```
your-root-docker-folder/
├── apps
│   ├── nzbdav
│   │   └── docker-compose.yml   👈 Create this file now
│   └── ...
```

Update `PUID`, `PGID`, `TZ`, and volume paths as needed. You can get your PUID/PGID by running `id` in your terminal.

```yaml
services:
  nzbdav:
    image: ghcr.io/nzbdav/nzbdav:latest
    container_name: nzbdav
    restart: unless-stopped
    healthcheck:
      # Follow the onboarding/login redirect and verify that the UI can reach the backend
      test: ["CMD-SHELL", "curl -fsSL http://localhost:3000/login > /dev/null || exit 1"]
      interval: 30s
      retries: 3         # mark unhealthy after 3 consecutive failures
      start_period: 30s  # allow migrations and both processes to start
      timeout: 5s
    ports:
      - "3000:3000"
    environment:
      # Change these IDs to match the user you got from `id` above
      PUID: "1000"
      PGID: "1000"
      # Set the time zone to match your location
      TZ: America/New_York
    volumes:
      - ./config:/config
      - /mnt:/mnt
```

Run the container:

```bash
docker compose up -d
```

The image runs two processes: the frontend and public proxy on port `3000`, and the backend on internal port `8080`. Clients should use port `3000`; the compose healthcheck follows the login/onboarding flow so it verifies that the frontend can also reach the backend.

> [!IMPORTANT]
> Port `3000` serves plain HTTP. If NzbDav will be reachable outside your trusted network, put it behind an HTTPS reverse proxy and do not expose the container port directly to the internet. WebDAV uses Basic authentication, so remote access without TLS exposes credentials. When the proxy runs on the Docker host, bind the port to localhost with `127.0.0.1:3000:3000`.
>
> For adapter/Newznab absolute links and STRM URLs, set an explicit **Base URL** under Settings (preferred). Without it, public scheme/host come only from trusted forwarded headers: the frontend strips client `X-Forwarded-*` and rewrites canonical values for the backend (which trusts loopback by default). TLS-terminating reverse proxies should set `TRUST_PROXY=1` on the container so Express honors the proxy’s forwarded headers when rewriting, **or** set Base URL explicitly — otherwise generated links may stay `http://`. Split-container topologies can widen backend trust with `TRUSTED_PROXY_CIDRS` (comma-separated IPs or CIDRs).

### 2. Core configuration

Navigate to `http://your-server-ip:3000`.

**A. Create the admin account**

Set your username and password.

**B. Usenet settings (`Settings` → `Usenet`)**

| Setting | Value |
|---------|-------|
| Host | `news.newshosting.com` (put your provider here) |
| Port | `563` |
| Username / Password | Your Usenet credentials |
| Max Connections | Your provider's allowed maximum (for example, `20`) |
| Type | `Pool Connections` |
| Use SSL | Checked |
| Storage group (optional) | Leave blank unless multiple providers share the same upstream storage |

**Storage group (optional):** If you have multiple providers that resell the *same* upstream storage (identical article availability), give them the same free-text label. When one reports an article missing (NNTP 430), NzbDav skips the remaining providers sharing that label for that request instead of re-probing the same storage. Connection errors never trigger a skip. Only group providers you are sure share storage *and* the same takedown/retention policy — otherwise a miss on one reseller could hide an article still available on another.

**C. WebDAV settings (`Settings` → `WebDAV`)**

| Setting | Value |
|---------|-------|
| WebDAV User | A dedicated WebDAV username (defaults to `admin`) |
| WebDAV Password | Create a password (required for Rclone, AIOStreams, and other WebDAV clients) |
| Enforce Read-Only | Leave checked, unless you'd like to delete files from a terminal |

### 3. Choose an import strategy

Go to `Settings` → `SABnzbd` → `Import Strategy`:

| Strategy | Best for | Configuration |
|----------|----------|---------------|
| **Symlinks — Plex** | Plex, or any setup that needs real filesystem entries | Continue to [Phase 3](#phase-3--rclone-sidecar-symlink-imports-only) and set **Rclone Mount Directory** to `/mnt/remote/nzbdav`. |
| **STRM Files — Emby/Jellyfin** | Emby/Jellyfin setups that can play `.strm` URLs | Skip Phase 3. Set **Completed Downloads Dir** to a path shared with Radarr/Sonarr, such as `/mnt/completed-downloads`, and set **Base URL** to an NzbDav URL reachable by your media server. |

The example compose file maps host `/mnt` to container `/mnt`, so `/mnt/completed-downloads` and your media library are visible to NzbDav. Map the same host paths into Radarr/Sonarr and your media server at the same container paths.

### 4. Speed tuning (optional)

> [!NOTE]
> **Max Download Connections** defaults to the lower of `15` or your total pooled provider connections. This may be enough to saturate a 1Gbps connection, but the result depends on your provider, CPU, and network. Only tune it if you are experiencing speed issues.

You can find the optimal **Max Download Connections** for your network (`Settings` → `WebDAV` → `Max Download Connections`) using the steps below:

1. **Baseline test** — run this on your server to check raw bandwidth:

   ```bash
   wget -O /dev/null https://ash-speed.hetzner.com/10GB.bin --report-speed=bits
   ```

2. **NzbDav internal test:**
   * In one terminal window, monitor CPU usage:

     ```bash
     docker stats nzbdav
     ```

   * Download a movie `.nzb` via your indexer website and upload it to NzbDav.
   * In the NzbDav UI, go to `Dav Explore` → `content` → your movie category (normally `movies`) → pick the movie you just added. Right-click the **Download** link for its video file and use your browser's **Copy Link Address** action.
   * The copied link uses the public frontend on port `3000`. For a benchmark inside the combined container, preserve its exact case-sensitive path and `downloadKey`, but replace the scheme/host/port with the internal backend address `http://localhost:8080`. For example:

     ```bash
     docker exec nzbdav curl -sS --max-time 20 -o /dev/null -w 'Average: %{speed_download} bytes/s\n' 'http://localhost:8080/view/content/movies/<movie-folder>/<movie-name>.mkv?downloadKey=<download-key>'
     ```

     The timeout message after 20 seconds is expected. Note the average speed and container CPU usage; multiply bytes/second by eight to compare it with a network speed reported in bits/second.

3. **Adjust and repeat:**
   * Set `Max Download Connections` to `10`. Test speed (e.g. 500Mbps @ 70% CPU).
   * Set `Max Download Connections` to `15`. Test speed (e.g. 1Gbps @ 85% CPU).
   * **Sweet spot:** stop when the speed plateaus and keep the lowest connection count that reaches it.

---

## Phase 3 — Rclone sidecar (symlink imports only)

Skip this phase if you selected **STRM Files — Emby/Jellyfin**. For the symlink strategy, mount the NzbDav WebDAV onto the host filesystem using a sidecar container.

### 1. Prepare the host directory

```bash
sudo mkdir -p /mnt/remote/nzbdav                          # create the mount folder
sudo chown -R $(id -u):$(id -g) /mnt/remote/nzbdav        # give your user ownership
```

### 2. Generate the Rclone config

```
your-root-docker-folder/
├── apps
│   ├── nzbdav
│   │   ├── docker-compose.yml
│   │   └── rclone.conf          👈 Create this empty file now
│   └── ...
```

Generate an obscured password from the WebDAV password you set in NzbDav earlier:

```bash
docker run --rm -it rclone/rclone obscure "<your-webdav-password>"
```

Then populate `rclone.conf` with:

```ini
[nzbdav]
type = webdav
url = http://nzbdav:3000/
vendor = other
user = your-webdav-user
pass = your-obscured-password
```

Rclone's obscured password is not encryption. Restrict access to the config file:

```bash
chmod 600 rclone.conf
```

### 3. Update `docker-compose.yml`

Add the Rclone sidecar under `services:` in your existing `apps/nzbdav/docker-compose.yml`. Update `TZ`, `--uid`, `--gid`, and volume paths as needed.

```yaml
  nzbdav_rclone:
    image: rclone/rclone:latest
    container_name: nzbdav_rclone
    restart: unless-stopped
    environment:
      TZ: America/New_York
    volumes:
      # Host path : container path : propagation
      - /mnt:/mnt:rshared
      - ./rclone.conf:/config/rclone/rclone.conf:ro
      # Keep the bounded VFS cache out of the disposable container layer
      - ./rclone-cache:/cache
    cap_add:
      - SYS_ADMIN
    security_opt:
      - apparmor:unconfined
    devices:
      - /dev/fuse:/dev/fuse:rwm
    depends_on:
      nzbdav:
        condition: service_healthy
        restart: true
    # Mount flags optimized for streaming — see "Understanding the flags" below
    command: >
      mount nzbdav: /mnt/remote/nzbdav
        --cache-dir=/cache
        --uid=1000
        --gid=1000
        --allow-other
        --links
        --use-cookies
        --vfs-cache-mode=full
        --vfs-cache-max-size=20G
        --vfs-cache-max-age=24h
        --buffer-size=0M
        --vfs-read-ahead=512M
        --dir-cache-time=20s
```

Start the sidecar:

```bash
docker compose up -d nzbdav_rclone
```

If you later change the Rclone config or the compose file, apply the changes with:

```bash
docker compose up -d --force-recreate nzbdav_rclone
```

Verify the mount is working:

```bash
ls -la /mnt/remote/nzbdav
# Should show: .ids, completed-symlinks, content, nzbs
```

### Understanding the flags

| Flag | Why |
|------|-----|
| `--cache-dir=/cache` | **Persistence.** Places the VFS cache on the dedicated bind mount instead of in the container's writable layer. The cache is disposable and does not need to be backed up. |
| `--links` | **Crucial.** Translates `*.rclonelink` files within the WebDAV into real symlinks on your filesystem. Requires Rclone v1.70.3+. |
| `--use-cookies` | **Performance.** Without this, Rclone re-authenticates on every single request, causing massive slowdowns. |
| `--allow-other` | **Permissions.** Ensures other containers (like Radarr/Plex) can see the mounted files. |
| `--vfs-cache-mode=full` | **Performance.** Enables disk-backed read caching and VFS read-ahead for smoother playback. |
| `--buffer-size=0M` | **Stability.** Prevents double-caching (RAM + disk). |
| `--vfs-read-ahead=512M` | **Smooth playback.** Buffers 512MB ahead of the current position to handle high-bitrate spikes without stuttering. |
| `--vfs-cache-max-size=20G` | **Disk management.** Sets a soft limit for the VFS cache; open files can temporarily exceed it. Adjust to your available storage. |
| `--vfs-cache-max-age=24h` | **Cleanup.** Removes cache entries that have not been accessed for 24 hours. |
| `--dir-cache-time=20s` | **Responsiveness.** Keeps the directory cache short so new downloads/links appear quickly in the mount. |

The official Rclone image does not use `PUID`/`PGID` environment variables. The `--uid` and `--gid` mount flags control the ownership presented to applications reading the mount.

> [!TIP]
> These flags are optimized for streaming. Resist the urge to add more: `unnecessary flags = potential pitfalls`. For background on buffer sizing, see this [Rclone forum discussion](https://forum.rclone.org/t/whats-the-suitable-value-to-set-for-buffer-size-with-vfs-read-ahead/39971/4).

---

## Phase 4 — Integrations

### 1. Add NzbDav as a download client in Radarr/Sonarr

Go to Radarr/Sonarr → `Settings` → `Download Clients` → `Add Download Client`:

The hostnames in this guide (`nzbdav`, `radarr`, and `sonarr`) only resolve when the containers share a Docker network. If they run in separate Compose projects, attach them to a shared external network or use hostnames/addresses that are reachable between the projects.

| Setting | Value |
|---------|-------|
| Client | **SABnzbd** |
| Name | `NzbDav` |
| Host | `nzbdav` |
| Port | `3000` |
| API Key | Found in NzbDav `Settings` → `SABnzbd` |

### 2. Configure NzbDav for Radarr/Sonarr

Go to NzbDav `Settings` → `Radarr/Sonarr`.

1. **Radarr Instances → Add**
   * **Host:** `http://radarr:7878`
   * **API Key:** Radarr → `Settings` → `General` → `Security` → `API Key`
2. **Sonarr Instances → Add**
   * **Host:** `http://sonarr:8989`
   * **API Key:** Sonarr → `Settings` → `General` → `Security` → `API Key`
3. **Automatic Queue Management**

   Configure these rules to handle failed or bad releases, keeping your queue clean with as little manual intervention as possible. Feel free to experiment and adjust them to your liking.

   * **Do Nothing:**
     * Found matching series via grab history, but release was matched to series by ID. Automatic import is not possible.
     * Found matching movie via grab history, but release was matched to movie by ID. Manual Import required.
     * Episode was not found in the grabbed release.
     * Episode was unexpected considering the folder name.
     * Invalid season or episode.
     * Single episode file contains all episodes in seasons.
     * Unable to determine if file is a sample.
     * Found archive file, might need to be extracted.
   * **Remove, Blocklist, and Search:**
     * No files found are eligible for import.
     * No audio tracks detected.
     * Sample.
   * **Remove and Blocklist:**
     * Not an upgrade for existing episode file(s).
     * Not an upgrade for existing movie file.
     * Not a Custom Format upgrade.
   * **Remove:**
     * Episode file already imported.

### 3. Configure imports & repairs

1. **Import strategy (`Settings` → `SABnzbd`):**
   * **Symlinks — Plex:** Set **Rclone Mount Directory** to `/mnt/remote/nzbdav`. This tells NzbDav which completed path to report to Radarr/Sonarr.
   * **STRM Files — Emby/Jellyfin:** Set **Completed Downloads Dir** to `/mnt/completed-downloads` (or another shared path), and set **Base URL** to an NzbDav URL reachable by your media server.
   * Whichever strategy you choose, the completed path NzbDav reports must be visible inside Radarr/Sonarr at the exact same path.
2. **Repairs (`Settings` → `Repairs`):**
   * **Library Directory:** `/mnt/media` — point this to the root folder where your actual movie/TV libraries live on the host.
   * **Enable Background Repairs:** Checked. This lets NzbDav monitor for dead links in your library and trigger redownloads automatically. The checkbox remains disabled until the Library Directory and at least one Radarr/Sonarr instance are configured.

---

## Phase 5 — Usenet streaming in Stremio (via AIOStreams)

You can stream your Usenet content directly in Stremio using [AIOStreams](https://github.com/Viren070/AIOStreams). For current upstream guidance, see the [AIOStreams Usenet documentation](https://docs.aiostreams.viren070.me/guides/usenet/).

### 1. Configure the NzbDav service

In the AIOStreams UI:

1. Go to the **Services** menu and select **NzbDav**.
2. Enter the details:

   | Setting | Value |
   |---------|-------|
   | URL | An address AIOStreams can reach: `http://nzbdav:3000` when co-hosted on the same Docker network, or your public HTTPS URL when AIOStreams is remote |
   | Public URL | Leave blank when using the recommended AIOStreams proxy; otherwise use the public HTTPS URL your player can reach |
   | NzbDAV API Key | From NzbDav `Settings` → `SABnzbd` |
   | NzbDAV WebDAV Username | From NzbDav `Settings` → `WebDAV` |
   | NzbDAV WebDAV Password | From NzbDav `Settings` → `WebDAV` |
   | AIOStreams Auth Token *(recommended)* | A `username:password` pair from your self-hosted AIOStreams `AIOSTREAMS_AUTH` configuration |

Providing the AIOStreams Auth Token makes AIOStreams proxy the stream. This keeps NzbDav private, avoids exposing WebDAV credentials to the player, and prevents HTTP/HTTPS protocol mismatches. If you do not use the proxy, **Public URL** must be an HTTPS address reachable by every playback device.

### 2. Configure the Newznab addon

In the AIOStreams UI:

1. Go to **Addons** → **Marketplace** → from the Types dropdown, select **Usenet**.
2. Find the **Newznab** addon and click **Configure**.
3. Add your indexers (repeat for each one):

   | Setting | Value |
   |---------|-------|
   | Name | `NZBGeek` (or similar) |
   | Newznab URL | Select `NZBgeek` from the dropdown |
   | API Key | Your indexer's API key |
   | AIOStreams Proxy Auth *(optional)* | A `username:password` pair from `AIOSTREAMS_AUTH`; use this when AIOStreams and NzbDav have different public IPs or when you want AIOStreams to proxy and cache NZB grabs |
   | Search Mode | **Both** if your indexer API allowance permits it; some indexers only return all results through query search |

4. Leave everything else as default and click **Install**.

### 3. Install to Stremio

Go to the **Save & Install** tab, click **Save**, and then install the addon to Stremio.

---

## Phase 6 — Search profiles and adapters

Search profiles expose selected NzbDav indexers through token-scoped endpoints. Create and manage profiles under `Settings` → `Profiles`, and treat each generated token as a secret.

### 1. Newznab adapter for Prowlarr, Sonarr, or Radarr

1. Add a custom Newznab indexer.
2. Set the URL to `http://nzbdav:3000/adapters/newznab/{token}`. Substitute the profile token; do not include `/api`, because the client appends it.
3. Enter any non-empty API key. Authentication uses the URL token; the API-key field is accepted for client compatibility.
4. Enable categories `2000` (Movies) and `5000` (TV).
5. Test the indexer. The client calls `/api?t=caps` to verify the adapter.

### 2. Addon adapter

The Addon adapter exposes a token-scoped manifest endpoint. Any compatible client installs it directly via the manifest URL:

```
http://nzbdav:3000/adapters/addon/{token}/manifest.json
```

The manifest advertises the resources the client may request for `movie` and `series` types (keyed by IMDB ids). When the user picks a title in the client, the client calls back into NzbDav and receives a list of release candidates with an `url` field that, when followed, triggers on-demand fetch + mount and redirects to a playable URL served by NzbDav's WebDAV mount.

### 3. JSON Search API

`GET /api/search/{token}/lookup?type=movie&id=tt0111161` returns vendor-neutral JSON of the form:

```json
{
  "profile": "Movies",
  "type": "movie",
  "id": "tt0111161",
  "count": 3,
  "results": [
    {
      "title": "...",
      "indexer": "...",
      "sizeBytes": 123456789,
      "postedAt": "2024-01-01T00:00:00Z",
      "grabs": 42,
      "playUrl": "http://nzbdav:3000/api/search/{token}/play/{playToken}.mkv"
    }
  ]
}
```

For series, pass `type=series&id=tt0944947&season=1&episode=1`. Hitting the `playUrl` triggers the same on-demand mount + playback redirect flow used by the Addon adapter.

---

## Phase 7 — Operations

### Database retention

NzbDav can prune aged SAB history and health-check rows so `db.sqlite` does not grow without bound:

| Variable | Default | Description |
| --- | --- | --- |
| `DATABASE_HISTORY_RETENTION_DAYS` | `90` | Keep SAB history entries for this many days. Set to `0` to retain everything. Mounted WebDAV content is **not** deleted. |
| `DATABASE_HEALTHCHECK_RETENTION_DAYS` | `30` | Keep health-check result rows for this many days. Set to `0` to retain everything. |
| `DATABASE_MAINTENANCE_INTERVAL_HOURS` | `6` | How often the background retention sweeps run. |

These can also be set under **Settings → Maintenance** (`database.history-retention-days`, `database.healthcheck-retention-days`). Use **Reset Health-Check Statistics** on that page to clear all health-check counters immediately.

### Back up NzbDav

Back up the host directory mapped to `/config` (shown as `./config` in this guide). It contains the database, settings, credentials, and persisted application data, so store the backup securely. Stop the container or use a filesystem snapshot to get a consistent backup:

```bash
docker compose stop nzbdav
tar -czf nzbdav-config-backup.tar.gz ./config
docker compose start nzbdav
```

The `rclone-cache` directory is disposable and should not be included in backups.

### Update NzbDav

Back up `/config` before updating. From the directory containing `docker-compose.yml`:

```bash
docker compose pull nzbdav
docker compose up -d nzbdav
```

If you also want to update the Rclone sidecar:

```bash
docker compose pull nzbdav_rclone
docker compose up -d nzbdav_rclone
```

The `latest` tag follows stable releases. For reproducible deployments, replace `latest` with a specific release tag from the [GitHub releases page](https://github.com/nzbdav/nzbdav/releases).

> [!IMPORTANT]
> When upgrading an installation older than `0.6.0`, NzbDav deliberately stops before the irreversible database migration. After making a complete `/config` backup, add `UPGRADE: "0.6.0"` to the NzbDav service's `environment`, run the update again, and remove the variable after the upgrade succeeds.

### View logs

```bash
docker compose logs --tail=200 -f nzbdav
docker compose logs --tail=200 -f nzbdav_rclone
```

If the Rclone mount fails, first verify that `/dev/fuse` exists on the host, the sidecar has started after NzbDav became healthy, and the WebDAV username/password in `rclone.conf` match `Settings` → `WebDAV`. If Rclone specifically rejects `--allow-other`, enable `user_allow_other` in the FUSE configuration available inside the sidecar.
