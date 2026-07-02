# Matddns

A universal DynDNS updater with a web UI. Pull an IP from one or more sources (the container's public IP, UniFi WANs — locally or via the UniFi cloud, a FRITZ!Box, a DNS lookup, a static value, or a value pushed in) and keep one or more DNS records in sync, with failover to the first reachable source.

[![Build & Publish](https://github.com/Real-TTX/Matddns/actions/workflows/docker-publish.yml/badge.svg)](https://github.com/Real-TTX/Matddns/actions/workflows/docker-publish.yml)
[![GHCR image](https://img.shields.io/badge/ghcr.io-real--ttx%2Fmatddns-2496ED?logo=docker&logoColor=white)](https://github.com/Real-TTX/Matddns/pkgs/container/matddns)
![.NET 8](https://img.shields.io/badge/.NET-8.0-512BD4?logo=dotnet&logoColor=white)

## Contents

- [Features](#features)
- [Quick start](#quick-start)
- [Configuration](#configuration)
- [How it works](#how-it-works)
- [Failover and validation](#failover-and-validation)
- [IPv6 / dual-stack](#ipv6--dual-stack)
- [DynDNS server (incoming)](#dyndns-server-incoming)
- [Monitoring API](#monitoring-api)
- [System and logging](#system-and-logging)
- [Versioning and schema](#versioning-and-schema)
- [Tech stack](#tech-stack)
- [License](#license)

## Features

- Several source types: the container's own public IP; UniFi UDM/UGW WANs read locally from the controller (including LTE failover) or from the UniFi Site Manager cloud; a FRITZ!Box over TR-064; a DNS lookup of any hostname; a static value; a value pushed in by a device; or another Matddns instance (peer pull over its JSON API).
- Several targets: the Cloudflare, Netcup, Hetzner DNS and GoDaddy APIs; another Matddns instance (peer push over its JSON API); plus generic DynDNS update URLs with built-in presets for ~20 providers (DuckDNS, No-IP, Dynu, DynDNS.org, Strato, deSEC, DynV6, Zoneedit, EasyDNS, OpenDNS, DNS-O-Matic, He.net, SPDYN, Selfhost, Variomedia, DDNSS, GoIP, ipv64, FreeDNS, …).
- Failover rules: a DNS record is bound to an ordered list of sources; the first reachable one wins.
- Reachability validation: a source is only accepted if its IP answers a ping or a TCP-port check.
- DynDNS server: Matddns can also receive updates, so a router or device can push its IP to a token-protected URL.
- Dual-stack: every source carries an optional IPv4 and IPv6 (IPv6-only works too).
- Dashboard and a monitoring API (`/api/health`, `/api/state`). The dashboard can optionally be made public (read-only).
- Multi-user login, a per-install time zone, and an adjustable log level with retention.
- One .NET 8 container with a single `/data` volume; CI publishes the image to GHCR on every push.

## Quick start

Run the published image from GHCR:

```yaml
# docker-compose.yml
services:
  matddns:
    image: ghcr.io/real-ttx/matddns:latest
    container_name: matddns
    restart: unless-stopped
    ports:
      - "4060:8080"
    volumes:
      - ./data:/data
    environment:
      - TZ=Europe/Berlin
    # lets the non-root user send ICMP for the "ping" failover check
    sysctls:
      - net.ipv4.ping_group_range=0 2147483647
    # healthy while /healthz returns HTTP 200 (liveness; 8080 is the in-container port)
    healthcheck:
      test: ["CMD", "curl", "-fsS", "-o", "/dev/null", "http://localhost:8080/healthz"]
      interval: 30s
      timeout: 5s
      retries: 3
      start_period: 20s
```

```bash
docker compose up -d
```

Or build it locally:

```bash
docker compose up -d --build
```

Then open <http://localhost:4060>.

The default login is `admin` / `admin`. Change it under System → Users before exposing Matddns beyond localhost.

## Configuration

| | |
|---|---|
| UI | <http://localhost:4060> (host `4060` maps to container `8080`) |
| Default login | `admin` / `admin` |
| Persisted data | `/data`: `config.json`, `log.txt`, and the DataProtection `keys/` (so sessions survive restarts) |
| Time zone | set under System → General; applied to all UI timestamps (the log and `/api/*` stay UTC) |

Everything else is configured in the UI; there are no files to hand-edit. The UI is English throughout.

## How it works

There are three building blocks.

### Sources (where an IP comes from)

| Kind | What it provides |
|------|------------------|
| Public IP | the container's own outbound IPv4/IPv6 |
| UniFi | one entry per WAN read from a local UDM/UGW (name, IPv4, global IPv6), including LTE failover; entries appear automatically |
| UniFi Cloud | gateways read from the UniFi Site Manager cloud API — one key, pick the gateways to track (with an optional alias); each keeps its current public IP |
| FRITZ!Box | the WAN IPv4/IPv6 read from a FRITZ!Box over TR-064 (locally via `fritz.box`, or remotely via MyFRITZ!) |
| DNS lookup | resolves any hostname to its A/AAAA (e.g. a MyFRITZ! address) |
| Static IP | a fixed value you type in (a known server, a fallback) |
| DynDNS server | an external device or router pushes its IP to a token URL; see [DynDNS server (incoming)](#dyndns-server-incoming) |
| Matddns peer | pulls the source entries (IPv4/IPv6) of another Matddns instance over its JSON API, authenticated by a token (base URL + token) |

### Domains (where an IP is written)

| Kind | Target |
|------|--------|
| DynDNS | any provider via an update URL with `{ipv4}`, `{ipv6}`, `{hostname}`, `{user}`, `{password}` placeholders (empty ones are dropped, so one URL covers A and AAAA); HTTP Basic auth is added when user/password are set, and presets prefill the URL |
| Netcup | the Netcup DNS API (record + zone) |
| Cloudflare | the Cloudflare API (API token with Zone.DNS edit; record + zone, A/AAAA/CNAME, optional create-on-demand) |
| Hetzner DNS | the Hetzner DNS API (Auth-API-Token; record + zone, A/AAAA/CNAME, optional create-on-demand) |
| GoDaddy | the GoDaddy DNS API (API key + secret; record + zone, A/AAAA/CNAME, optional create-on-demand) |
| Matddns peer | pushes the chosen IP to another Matddns instance over its JSON API (base URL + token); parses the JSON result |

Each entry is a single record: a full FQDN plus type A, AAAA, or CNAME (for Netcup, record/subdomain + zone).

### Rules (the glue)

A rule links one record to an ordered list of source entries (the failover order). The record type comes from the entry.

- Triggers are independent and can be combined: react on IP change (event-driven), and/or re-check every N seconds. A periodic re-check is what drives ping/TCP failover, because a source going down does not change its IP.
- Validation: none, ping, or TCP port (see below).

## Failover and validation

- A / AAAA: the first reachable source wins; its IP is written to the record.
- CNAME: the first reachable source wins; the target hostname configured per source is written.

A source counts as reachable only when its current IP passes the rule's validation:

| Validation | Needs |
|------------|-------|
| None | the source just has to have an IP |
| Ping | ICMP echo to the source IP (needs the `ping_group_range` sysctl shown above) |
| TCP port | a TCP connect to `IP:port` succeeds (no special privileges) |

## IPv6 / dual-stack

Every source entry holds an optional IPv4 and IPv6 address (IPv6-only is supported). Public IP, UniFi (local and cloud) and FRITZ!Box read both families (only globally routable v6 is kept); static, DNS lookup and incoming-push carry both too. Rules pick by record type (A uses IPv4, AAAA uses IPv6) and skip a source that lacks the needed family. The UI shows v4 and v6 everywhere.

## DynDNS server (incoming)

A DynDNS-server source lets a device report its IP to a token-protected URL (shown on the source's page, with copy-ready FRITZ!Box and UniFi examples). Both URLs take separate `ipv4`/`ipv6` parameters; send either or both, and the other family is left untouched.

A **dynamic receiver** (a dynamic rule binding a DynDNS-server source to a Netcup zone) lets one token manage many records under a namespace: the device sends `&hostname=<name>.<zone>` and that record is created/updated on the fly. Auto-creating records is opt-in per Netcup zone ("Allow dynamic records").

JSON API, for scripts and devices:

```http
GET /api/update?token=<token>&ipv4=<v4>&ipv6=<v6>
```

```jsonc
200 { "status": "ok", "changed": true, "ipv4": "…", "ipv6": "…", "time": "…" }
400 { "status": "no-ip" }
401 { "status": "unauthorized" }
```

Omit both IPs (or use the legacy auto-detecting `&ip=`) to fall back to the caller's IP.

dyndns2, for routers and FRITZ!Box (HTTP Basic auth, or `?token=` in the URL):

```http
GET /nic/update?ipv4=<v4>&ipv6=<v6>
```

This one keeps the plain-text dyndns2 protocol routers expect: `good` / `nochg` / `badauth` / `nohost`. Use `/api/update` everywhere else.

## Monitoring API

Unauthenticated JSON endpoints for monitoring software. They never expose passwords or API keys:

| Endpoint | Purpose |
|----------|---------|
| `GET /healthz` | liveness; returns `ok` while the app runs |
| `GET /api/health` | health summary: HTTP 200 when OK, 503 when degraded, plus source/rule counts and IP-change stats |
| `GET /api/state` | full state: per-source IPs, per-rule status, recent IP changes |
| `GET /api/source?token=` | peer pull: this instance's source entries (IPs only), for a valid DynDNS-Server token — used by a Matddns-peer source on another instance |

```bash
curl http://localhost:4060/api/health   # point a monitor here and alert on a non-200 status
```

The `/api/*` and `/nic/update` endpoints are unauthenticated by design, so they work behind a monitor or a router. Keep Matddns on a trusted network or behind a reverse proxy. The optional public dashboard exposes the same data as `/api/state`, nothing more.

## System and logging

- System is split into tabs: General (log level, retention, time zone, public-dashboard toggle), Users (add/edit/delete; you can't delete the user you're logged in as or the last user), and API (endpoint docs).
- Public dashboard: when enabled, the dashboard is viewable without a login (read-only); every other page still requires authentication, and the menu hides the items you can't use.
- Log level order is `Debug < Info < Update < Warn < Error`; anything below the minimum is never recorded. Routine WAN polling and reachability checks sit on `Debug`, so the default `Info` stays quiet.
- Retention: entries older than N days are pruned hourly (`0` = unlimited).

## Versioning and schema

Release images carry a monotonic version (`0.4.<run_number>`) injected at build time; the footer shows version, build, and date. Local builds show `local · build local`. The same token cache-busts static assets so UI changes reach the browser.

`config.json` carries a `schemaVersion`. On startup, `ConfigService` runs every ordered migration between the stored version and `CurrentSchemaVersion`, then stamps the new version and saves. A fresh install starts at the current version; an older config is upgraded in place. To change the data shape: raise `CurrentSchemaVersion`, append a `MigrateVXToVY` step to the `Migrations` array (append-only, never reordered), and raise the release minor. Migrations are idempotent and run once per step.

## Tech stack

- .NET 8 / ASP.NET Core Razor Pages, cookie auth, a hosted background updater service
- System.Text.Json config in `/data/config.json`; DataProtection keys in `/data/keys`
- Docker (`mcr.microsoft.com/dotnet/aspnet:8.0`, non-root, `tzdata` for time zones)
- GitHub Actions publishing to GHCR on every push to `main`

## License

No license file is included yet. If you intend others to reuse this, add a `LICENSE` (for example MIT) at the repo root.
