<div align="center">

# ◈ Matddns

**A universal DynDNS updater with a web UI, multi-source WAN failover, and a built-in DynDNS receiver.**

Pull an IP from one or more **sources** (your public IP, UniFi WANs, a static value, or a pushed value) and keep one or more **DNS records** in sync — with failover to the first reachable source.

[![Build & Publish](https://github.com/Real-TTX/Matddns/actions/workflows/docker-publish.yml/badge.svg)](https://github.com/Real-TTX/Matddns/actions/workflows/docker-publish.yml)
[![GHCR image](https://img.shields.io/badge/ghcr.io-real--ttx%2Fmatddns-2496ED?logo=docker&logoColor=white)](https://github.com/Real-TTX/Matddns/pkgs/container/matddns)
![.NET 8](https://img.shields.io/badge/.NET-8.0-512BD4?logo=dotnet&logoColor=white)
![Dual-stack](https://img.shields.io/badge/IPv4%20%2B%20IPv6-dual--stack-success)

</div>

---

## Table of contents

- [Features](#features)
- [Quick start](#quick-start)
- [Configuration](#configuration)
- [How it works](#how-it-works)
- [Failover &amp; validation](#failover--validation)
- [IPv6 / dual-stack](#ipv6--dual-stack)
- [DynDNS receiver (push)](#dyndns-receiver-push)
- [Monitoring API](#monitoring-api)
- [System &amp; logging](#system--logging)
- [Versioning](#versioning)
- [Tech stack](#tech-stack)
- [License](#license)

## Features

- 🌐 **Multiple source types** — the container's own public IP, **UniFi** UDM/UGW WANs (auto-discovered, incl. LTE failover), a **static** value, or a **pushed** value.
- 🎯 **Multiple targets** — generic **DynDNS** update URLs (presets for DuckDNS, No-IP, Dynu, DynDNS.org, Strato, deSEC) and the **Netcup** DNS API.
- 🔁 **Failover rules** — bind a DNS record to an ordered list of sources; the first reachable one wins.
- 🩺 **Reachability validation** — accept a source only if its IP answers a **ping** or a **TCP port** check.
- 📡 **DynDNS receiver** — Matddns can itself act as a DynDNS server: routers/devices push their IP to a token-protected URL.
- 🧭 **Dashboard** — health overview, per-source IPs, per-rule status, and recent IP changes.
- 📈 **Monitoring API** — unauthenticated `/api/health` &amp; `/api/state` JSON for Uptime Kuma, Zabbix, etc.
- 🧱 **Dual-stack** — every source carries optional IPv4 **and** IPv6 (IPv6-only is fine).
- 👥 **Users, time zone, log levels** — slim multi-user auth, per-install time zone, and adjustable log verbosity + retention.
- 🐳 **Single container** — `.NET 8`, one image, one `/data` volume; CI publishes to GHCR on every push.

## Quick start

### Run the published image (GHCR)

Every push to `main` builds and publishes `ghcr.io/real-ttx/matddns:latest`.

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
    # let the non-root user send ICMP for the "Ping" failover check
    sysctls:
      - net.ipv4.ping_group_range=0 2147483647
```

```bash
docker compose up -d
```

### Build locally

```bash
docker compose up -d --build
```

Then open **<http://localhost:4060>**.

> [!WARNING]
> The default login is **`admin` / `admin`**. Change it under **System → Users** before exposing Matddns to anything but localhost.

## Configuration

| | |
|---|---|
| **UI** | <http://localhost:4060> (host `4060` → container `8080`) |
| **Default login** | `admin` / `admin` |
| **Persisted data** | `/data` — `config.json`, `log.txt`, and DataProtection `keys/` (so sessions survive restarts) |
| **Time zone** | set under **System → General**; applied to all UI timestamps (the log and `/api/*` stay UTC) |

Everything else is configured in the UI — no files to hand-edit. The UI is **English** throughout.

## How it works

Matddns has three building blocks:

### Sources — *where an IP comes from*

| Kind | What it provides |
|------|------------------|
| **Public IP** | the container's own outbound IPv4/IPv6 |
| **UniFi** | one entry per WAN read from a UDM/UGW (display name + IPv4 + global IPv6), including LTE failover — entries appear automatically |
| **Static IP** | a fixed value you type in (a known server, a fallback) |
| **Push** | an external device/router pushes its IP to a token URL — see [DynDNS receiver](#dyndns-receiver-push) |

### Domains — *where an IP is written*

| Kind | Target |
|------|--------|
| **DynDNS** | any provider via an update URL with `{hostname}` `{ip}` `{user}` `{password}` placeholders (HTTP Basic auth is added when user/password are set); provider presets prefill the URL |
| **Netcup** | the Netcup DNS API (record + zone) |

Each entry is a single record: full FQDN + type **A / AAAA / CNAME** (for Netcup, record/subdomain + zone).

### Rules — *the glue*

A rule links **one record** to an **ordered list of source entries** (the failover order). The record type comes from the target entry.

- **Trigger** — *on IP change* (write only when the source IP actually changes) or a *fixed interval*.
- **Validation** — *none*, *ping*, or *TCP port* (see below).

## Failover &amp; validation

- **A / AAAA** — the first reachable source wins; its IP is written to the record.
- **CNAME** — the first reachable source wins; the target hostname configured per source is written.

A source counts as *reachable* only when its current IP passes the rule's validation:

| Validation | Needs |
|------------|-------|
| **None** | the source just has to have an IP |
| **Ping** | ICMP echo to the source IP (requires the `ping_group_range` sysctl above) |
| **TCP port** | a TCP connect to `IP:port` succeeds (no special privileges) |

## IPv6 / dual-stack

Every source entry holds an optional **IPv4 and IPv6** address (IPv6-only is supported). Public-IP and UniFi read both families (UniFi keeps only globally-routable v6); static and push carry both too. Rules pick by record type — **A → IPv4**, **AAAA → IPv6** — and skip a source that lacks the needed family. The UI shows v4 and v6 everywhere.

## DynDNS receiver (push)

A **Push** source turns Matddns into a DynDNS server: an external device reports its IP to a token-protected URL (shown on the source's page). Both URLs accept separate `ipv4`/`ipv6` parameters — send either or both; each value is stored under the matching A / AAAA source and the other family is left untouched.

**JSON API** — for scripts and devices:

```http
GET /api/update?token=<token>&ipv4=<v4>&ipv6=<v6>
```

```jsonc
200 { "status": "ok", "changed": true, "ipv4": "…", "ipv6": "…", "time": "…" }
400 { "status": "no-ip" }
401 { "status": "unauthorized" }
```

Omit both IPs (or use the legacy auto-detecting `&ip=`) to fall back to the caller's IP.

**dyndns2** — for routers / FRITZ!Box (HTTP Basic auth, password = the token):

```http
GET /nic/update?ipv4=<v4>&ipv6=<v6>
```

Returns the plain-text dyndns2 protocol routers expect: `good` / `nochg` / `badauth` / `nohost`.

## Monitoring API

Unauthenticated JSON endpoints for monitoring software — they never expose passwords or API keys:

| Endpoint | Purpose |
|----------|---------|
| `GET /healthz` | liveness — returns `ok` while the app runs |
| `GET /api/health` | health summary: HTTP **200** when OK, **503** when degraded, plus source/rule counts and IP-change stats |
| `GET /api/state` | full state: per-source IPs, per-rule status, recent IP changes |

```bash
curl http://localhost:4060/api/health   # point your monitor here and alert on non-200
```

> [!NOTE]
> The `/api/*` and `/nic/update` endpoints are intentionally **unauthenticated** so they work behind a monitor or a router. Keep Matddns on a trusted network or behind a reverse proxy.

## System &amp; logging

- **System** is split into tabs: **General** (log level, retention, time zone), **Users** (add / edit / delete; you can't delete the user you're logged in as or the last user), and **API** (endpoint docs).
- **Log level** — `Debug < Info < Update < Warn < Error`; anything below the minimum is never recorded. Routine WAN polling and reachability checks sit on `Debug`, so the default `Info` stays quiet.
- **Retention** — entries older than *N* days are pruned hourly (`0` = unlimited).

## Versioning

Release images carry a monotonic version (`0.2.<run_number>`) injected at build time; the footer shows version · build · date. Local builds show `local · build local`. The same token cache-busts static assets so UI changes always reach the browser. The minor (`0.2`) is bumped whenever the config schema changes.

### Config schema &amp; migrations

`config.json` carries a `schemaVersion`. On startup `ConfigService` runs every ordered migration between the stored version and `CurrentSchemaVersion`, then stamps the new version (and saves). A fresh install is born at the current version; an older config is upgraded in place. To evolve the data shape: bump `CurrentSchemaVersion`, append a `MigrateVXToVY` step to the `Migrations` array (append-only, never reorder), and bump the release minor. Migrations are idempotent and run once per version step.

## Tech stack

- **.NET 8 / ASP.NET Core Razor Pages**, cookie auth, hosted background updater service
- **System.Text.Json** config in `/data/config.json`; DataProtection keys persisted in `/data/keys`
- **Docker** (`mcr.microsoft.com/dotnet/aspnet:8.0`, non-root, `tzdata` for time zones)
- **GitHub Actions** → GHCR on every push to `main`

## License

No license file is included yet. If you intend others to reuse this, add a `LICENSE` (e.g. MIT) at the repo root.
