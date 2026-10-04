# ITSeti Central Server

ASP.NET Core 10 service for machine enrollment, diagnostic report ingestion, history, package publishing, and Okdesk catalog synchronization.

## Services

- `server`: authenticated web portal and JSON API.
- `database`: PostgreSQL; not published outside the Docker network.
- `proxy`: Caddy HTTPS reverse proxy and automatic certificate management.

The portal uses the bootstrap account supplied through environment variables. The password is hashed with ASP.NET Core's `PasswordHasher`; only the hash is stored in PostgreSQL. Data Protection keys and package files live in the `app-data` Docker volume.

## Start

Copy `.env.example` to `.env`, replace the secret placeholders with unique random values, and set the public DNS name. Keep `.env` outside source control. Docker builds the .NET 10 service from source. From this directory run:

```sh
docker compose up -d --build
docker compose logs -f server proxy
```

Ports 80 and 443 must reach this host from the public network for Caddy to issue and renew its TLS certificate. The PostgreSQL port is private to Compose.

Set `OKDESK_API_TOKEN` in the server `.env` (mode `600`) when an employee API key is available. The server uses the official Okdesk API to read companies and service objects and to create issues. It does not sign in through the website. Catalog refresh runs every three days; failed refreshes retry after six hours. With no key, the existing catalog remains available and incoming ticket requests stay in the local queue. Restart the service after adding the key. Never commit the token; API URLs and response bodies must not be logged.

## API outline

- `POST /api/v1/catalog` and `POST /api/v1/enroll` accept the shared installation password in the `X-Registration-Password` HTTPS header. The server stores only a salted PBKDF2-SHA256 hash in `REGISTRATION_PASSWORD_HASH`, formatted as `iterations:base64-salt:base64-digest`; it never logs the supplied password. The installer asks for it once, lets the engineer choose a company and service object, and stores the resulting per-device key with access restricted to Administrators and SYSTEM. If the server is unavailable, installation continues locally and the Start menu provides a retry action. The existing clients do not yet upload check results automatically.
- `POST /api/v1/check-runs` accepts the diagnostic JSON with `X-Device-Id` and `X-Device-Key`. A report ID makes retries idempotent.
- `GET /api/v1/ticket-services` lists allowed service codes. `POST /api/v1/tickets` accepts `requestId`, `title`, `description`, and `serviceCode` from an enrolled device. Company and service object come from the server-side device record. The response is `202 queued` after durable storage, **not** confirmation from Okdesk. Poll `GET /api/v1/tickets/{requestId}` for `created`, `rejected`, or `unknown`; `created` includes the Okdesk issue ID. The request ID is an idempotency key.
- `GET /api/v1/updates` lists the currently published packages; `GET /api/v1/packages/{id}/download` downloads a file.
- `/api/admin` endpoints and the browser portal require the administrator cookie session.
- `GET /api/admin/monitoring` lists new diagnostic findings and critical/error journal event types, with period, company, object, type, and page filters.
- `GET /api/admin/tickets` lists queued, sending, created, rejected, and uncertain tickets with company/object filters and pagination. The browser's **Заявки** view shows the request text, source serial number, status, and a link to Okdesk after creation.

Ticket submission is asynchronous. A definitive Okdesk 4xx response is marked `rejected`. Timeouts, server errors, interrupted sends, and malformed success replies are marked `unknown` and are **not automatically retried**, because Okdesk may have created the issue. An engineer should check Okdesk before submitting a new ticket. The Windows 10/11 beta client supports ticket creation, attachments and public replies. Windows 7 intentionally does not offer tickets.

The server records each report as JSONB and keeps a compact indexed summary for browsing. Detailed event and process comparison is computed against the previous report for that computer. The first report establishes a baseline; later reports add monitoring alerts only for issue types absent from the preceding check. Journal events use the last report that captured events as their baseline, so an intervening quick check does not mark every journal error as new. Repeated instances of the same event type do not create another alert merely because their Windows record ID changes. Registration requests are rate-limited; changing the shared installation password does not invalidate keys already issued to computers.

The browser portal has Computers, Monitoring, Tickets, and Software/Updates views. Computers are displayed by the inventory number entered during installation, with company, service object and PC selectors. BIOS serial numbers and Windows host names are not the visible device identity. Enrollment requires a usable BIOS serial or hardware UUID as an internal fingerprint so reinstalls cannot create an unkeyed duplicate. On startup, the server recalculates fingerprints from usable serials, merges records for the same fingerprint, and reattaches their check history, alerts, and tickets to the canonical device record. Inventory numbers alone are not used to merge devices because they may be unset or repeated.

## Pilot limits

The current VPS has 1 GiB of memory. Compose limits database, API, and proxy memory and connections for a pilot. Before enrolling all 3,000 computers, monitor peak memory, report upload bursts, package egress, database growth, and backup restore time. Roll out large packages by company/object batches.

The server does not execute arbitrary commands on clients. The Windows 10/11 beta worker uses fixed SYSTEM tasks and whitelisted silent-install recipes, bounded HTTPS downloads and SHA-256 validation. Automatic software updates affect already installed programs only. Install All excludes WinRAR and Yandex. Unknown package keys require a reviewed installation recipe; server-provided command lines are never executed. The installer retains an optional offline payload without publishing it as application content. Real vendor installation and Windows 7 execution still require pilot validation; see AUDIT-2026-10-05.md.
