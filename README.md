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

## Datenmodell
- **Sources** - Gruppe = Verbindung (z. B. ein Unifi Controller). Eine Unifi-Gruppe legt automatisch je einen Eintrag pro WAN an (Display-Name + IP werden aus der UDM gelesen). Public-IP Gruppe hat genau einen Eintrag.
- **Domains** - Gruppe = Account (DynDNS-Credentials oder Netcup API). Jeder Eintrag ist ein Record: voller FQDN + Typ (A / AAAA / CNAME); fuer Netcup als Subdomain/Record + Zone.
- **Rules** - verbinden 1 Record mit einer geordneten Liste von Source-Eintraegen (Failover). Der Record-Typ kommt vom Ziel-Eintrag.
  - Trigger: **Bei IP-Aenderung** (nur schreiben wenn sich die Quell-IP aendert) oder **festes Intervall**.
  - Failover-Validierung: **keine** (nur IP vorhanden), **Ping** oder **TCP-Port offen** - eine Quelle gilt nur als erreichbar, wenn der Check der Quell-IP besteht.

## Failover
- A / AAAA: erste erreichbare Source gewinnt -> deren IP wird gesetzt.
- CNAME: erste erreichbare Source gewinnt -> der pro Source hinterlegte Zielhostname wird in den CNAME geschrieben.

## System / Log
- **System**-Tab: Log-Mindestlevel (Debug < Info < Update < Warn < Error) und Retention (Tage, 0 = unbegrenzt) + "Log leeren".
- Routine-Logs (WAN-Polling, Erreichbarkeits-Checks) liegen auf `Debug` und spammen bei Standard-Level `Info` nicht.
- Log-Seite hat einen Level-Filter.

## DynDNS Update-URL Platzhalter
`{hostname}`, `{ip}`, `{user}`, `{password}` - Basic-Auth wird zusaetzlich gesetzt, wenn User/Passwort befuellt sind.
