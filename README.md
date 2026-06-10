# Matddns

Universeller DynDNS-Updater mit Web UI. Quellen (eigene Public-IP, Unifi WAN1/WAN2/LTE) -> Ziele (DynDNS-URL, Netcup DNS API). Failover-Regeln nutzen die erste erreichbare Quelle und schreiben A / AAAA oder schwenken einen CNAME um.

## Stack
- .NET 8 / ASP.NET Core Razor Pages
- JSON Config in `/data/config.json`, Logdatei `/data/log.txt`
- Cookie-Auth, DataProtection-Keys persistiert in `/data/keys` -> Sessions ueberleben Container-Restart
- Standard Login `admin / admin`

## Start (lokal bauen)
```
docker compose up -d --build
```
UI: http://localhost:4060

## Start (fertiges Image aus GHCR)
Jeder Push auf `main` baut automatisch ein Image und veroeffentlicht es als GitHub Package:
```
ghcr.io/real-ttx/matddns:latest
```
Beispiel `docker-compose.yml`:
```yaml
services:
  matddns:
    image: ghcr.io/real-ttx/matddns:latest
    ports: [ "4060:8080" ]
    volumes: [ "./data:/data" ]
    sysctls: [ "net.ipv4.ping_group_range=0 2147483647" ]   # fuer Ping-Validierung
    restart: unless-stopped
```

UI language: English.

## Data model
- **Sources** - group = a connection. Kinds: **Public IP** (container's own IP, single entry), **Unifi** (auto-creates one entry per WAN; display name + IP read from the UDM), **Static IP** (fixed value, e.g. a known server or fallback), **Push / DynDNS receiver** (an external device/router pushes its IP to a token-protected URL — Matddns acts as a DynDNS server and rules forward it to the real targets).

## DynDNS receiver endpoints (token-protected, no login)
A "Push" source exposes update URLs (shown on its page):
- Simple: `GET /api/update?token=<token>&ip=<ip>` (omit `ip` to use the caller IP). Returns `good <ip>` / `nochg <ip>`.
- dyndns2 (routers / FRITZ!Box): `GET /nic/update?hostname=...&myip=<ip>` with HTTP Basic auth, password = the token. Returns `good`/`nochg`/`badauth`.
- **Domains** - group = account (DynDNS credentials or Netcup API). Each entry is a record: full FQDN + type (A / AAAA / CNAME); for Netcup as subdomain/record + zone. DynDNS offers provider presets (DuckDNS, No-IP, Dynu, DynDNS.org, Strato, deSEC) that prefill the update URL.
- **Rules** - verbinden 1 Record mit einer geordneten Liste von Source-Eintraegen (Failover). Der Record-Typ kommt vom Ziel-Eintrag.
  - Trigger: **Bei IP-Aenderung** (nur schreiben wenn sich die Quell-IP aendert) oder **festes Intervall**.
  - Failover-Validierung: **keine** (nur IP vorhanden), **Ping** oder **TCP-Port offen** - eine Quelle gilt nur als erreichbar, wenn der Check der Quell-IP besteht.

## Failover
- A / AAAA: erste erreichbare Source gewinnt -> deren IP wird gesetzt.
- CNAME: erste erreichbare Source gewinnt -> der pro Source hinterlegte Zielhostname wird in den CNAME geschrieben.

## IPv6 / Dual-Stack
Jeder Source-Eintrag haelt optional IPv4 und IPv6 (auch IPv6-only moeglich). Public-IP und Unifi lesen beide Familien (Unifi nur global routbare v6); Static und Push (per Familie der gepushten Adresse) ebenfalls. Regeln waehlen passend zum Record-Typ: **A -> IPv4**, **AAAA -> IPv6**; eine Source ohne passende Familie wird im Failover uebersprungen. Die UI zeigt v4 und v6 ueberall an.

## System / Log
- **System**-Tab: Log-Mindestlevel (Debug < Info < Update < Warn < Error) und Retention (Tage, 0 = unbegrenzt) + "Log leeren".
- Routine-Logs (WAN-Polling, Erreichbarkeits-Checks) liegen auf `Debug` und spammen bei Standard-Level `Info` nicht.
- Log-Seite hat einen Level-Filter.

## DynDNS Update-URL Platzhalter
`{hostname}`, `{ip}`, `{user}`, `{password}` - Basic-Auth wird zusaetzlich gesetzt, wenn User/Passwort befuellt sind.
