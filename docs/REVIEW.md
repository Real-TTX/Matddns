# Review of Matddns (October 2026)

Every page was run locally (temporary data directory, management API to seed sources, a DynDNS group and two rules) and looked at in
light, dark and at 390 px with Edge. Measured against **ddns-updater** (qdm12) and **ddclient**.

## Findings and fixes

| Area | Finding | Fix |
|---|---|---|
| Security | The forms took any text as the address of a UniFi console, a FRITZ!Box, a peer or a DynDNS update URL (`file:`, `gopher:` …), and the API did not check `dynDns.updateUrl` | Only absolute http(s) URLs (`InputCheck`), in the forms, the connection test and the API (`400`). Private addresses stay allowed on purpose (FRITZ!Box, UniFi are local) |
| Security | Provider tokens, push and peer tokens | Checked: never in the log, never returned by `/api/v1`, password fields keep the stored value. The new SMTP password and webhook URL follow the same rule (the URL is shown as `https://host/…`) |
| Security | Authorization | Every page sits behind the fallback policy; only `/Login` and the dashboard (setting) are anonymous; `/api/v1` needs the key; peer endpoints the token. Export of the configuration needs a sign-in (all accounts are administrators, Matddns has no roles) |
| Data | A record could be any text and be added twice | Records must be DNS names and are added once per group |
| Data | The System page saved without the checks of `/api/configure` (any retention, unknown time zones) and without a log entry | It saves through `SettingsService`; clearing the log is logged |
| Data | The IP-change history came from `Update` lines of the log: gone with log level *Warn*, the retention or *Clear log* | Own history file (see below) |
| UI | AAAA rules showed the IPv4 of their sources in the failover rows (dashboard, rules, rule page) | The address that counts for the record type |
| UI | Dashboard repeated the Domains list without any state | Removed; the rules show the records |
| UI | *New dynamic rule* showed an empty form without its prerequisites; labels "Target zone (Netcup)" next to "Zone" | Message with links and *Back*; "Domain group" / "Zone"; zone and prefix are checked |
| UI | Rule page: the raw provider answer as one long pill ran off a phone screen | Pill with the state, answer wrapped below; next retry shown |
| UI | "Unifi" and "UniFi", "Domain" for a record, "Run forced", "Dynamic rule" (no verb), notices without full stops | One spelling, "Record", "The rule runs within 15 seconds.", "New dynamic rule", tidied messages |
| Texts | System → API repeated the README (long JSON example, management API paragraph) | One table and two sentences |
| Dead code | `AddEntry` handler of sources without a form; DynDNS preset "Matddns (another instance)" duplicating the Matddns peer target | Removed |
| Robustness | System messages in the language of the host (a German Windows wrote German errors into the rule state) | Invariant culture for the app (the socket texts of Windows itself still follow the OS; in the container they are English) |

Kept as decided: `/api/health` answers 200 when degraded (`?strict=1` = 503); anonymous `/api/state` keeps the old monitoring shape.
`/api/v1` and the peer endpoints are unchanged (the pinning tests pass); the IP-change counts of `/api/health` and `/api/state` now come
from the history, same JSON shape.

Done after the review: mail through MatSMTP, notifications for sources, a limit for wrong keys and new screenshots (see "Done" below).

## Gaps against ddns-updater / ddclient

| Feature | State | Note |
|---|---|---|
| Many providers | partly → better | 5 APIs (Cloudflare, Netcup, Hetzner, GoDaddy, Matddns peer) + 29 DynDNS presets (7 new, `{host}`/`{domain}`); ddns-updater has ~50 native APIs |
| IPv4 and IPv6 | has | Per source entry, A and AAAA records, IPv6-only works |
| Several IP detection methods with fallback | has | 3 IPv4 / 2 IPv6 echo services, UniFi, FRITZ!Box, DNS, push, peer; failover order per rule with ping/TCP check |
| Per-record status | has | Rules list, rule page, dashboard, `/api/state` |
| History of IP changes | **done** | `history.jsonl` (value, before, source), rule and dynamic-rule pages, dashboard |
| Update only on change | has | Same value and source = no write; no drift check against the live DNS (see plan) |
| Retry / backoff | **done** | 30 s doubling to 30 min per rule, next try shown, reset on save |
| Notifications | **done** | E-mail (own SMTP or MatSMTP), webhook JSON / ntfy / Gotify; change, failure, recovery of records and of sources |
| Web UI with the state of every record | has | |
| Health endpoint | has | `/healthz`, `/api/health` (+ `?strict=1`), platform contract |
| Config import / export | **done** | System → Backup |
| Dynamic hostnames (one token, many records) | **done (extended)** | Was Netcup only; now every zone-based provider |
| Drift check (compare with what DNS answers) | missing | |
| Notifications for sources (a WAN down) | **done** | Once at the first failure of a source entry, once on its recovery |
| Shoutrrr-style targets (Telegram, Discord, Pushover …) | partly | Reachable through the JSON webhook or ntfy/Gotify relays |

## Done (after the review)

| Item | What |
|---|---|
| Mail through MatSMTP | MatPlatform 1.3.0; with `MATSMTP_*` set, `NotificationService.EffectiveMail()` takes server, port, security, user, password and sender from `MatSmtpSettings`. System → Notifications shows the `Describe` sentence and the server fields disabled (nothing saved, password never shown); `/api/configure` now carries the notification settings and marks the server fields `fixed` (`Fix()`, a change is `400`). On/off and the recipients stay editable. README and compose name the six variables |
| Notifications for sources | A polled source entry that starts failing sends one `source-failure`, its recovery one `source-recovery` (same channels; switch *A source fails*, `notifyOnSource`) |
| Wrong keys | `/api/v1` and `/api/state`, `/api/configure`, `/api/actions`: after 10 `401` per address and minute the address gets `429` (`Retry-After`) for the rest of the minute; a valid key or token is never limited; the pinned answers are unchanged |
| Screenshots | All of `docs/images` taken again (dashboard without the Domains table, rule page, phone), plus `history.png` and `notifications.png` |

## Plan (what is left)

| Priority | Item | Effort |
|---|---|---|
| 1 | Drift check: resolve the record (or ask the provider) every N hours and rewrite when it differs | M |
| 2 | More native APIs where a DynDNS URL is not enough: DigitalOcean, Porkbun, deSEC token API, IONOS, Gandi | M each |
| 3 | History as a chart per record (changes per day) | M |

Dropped:

- ddclient's config-file format and its many router "use=" scrapers: the sources of Matddns already cover the routers that matter (UniFi, FRITZ!Box) and push.
- A CLI mode without the web UI: Matddns is a container with a UI and an API; ddclient fills that niche.
- Shoutrrr as a dependency: a Go library; the webhook formats cover the receivers people run (ntfy, Gotify, anything with JSON).
