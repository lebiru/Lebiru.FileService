# Changelog

All notable changes to this project are documented in this file. Versions follow Semantic Versioning.

## [0.0.1-preview.26] - 2026-08-20

### Added

- Added an authorized `/files/{fileId}` page with file preview, total views, last-viewed time, and a compact 14-day activity trend.
- Added generic 64-bit `ViewCount`, nullable UTC `LastViewedAt`, and bounded daily view rollups to managed-file metadata for uploads, fetched pages, historical files, and files in virtual directories.
- Added `GET /api/files/{fileId}` for authorized read-only view summaries without counting metadata requests as page views.
- Added configurable five-minute same-file/same-viewer deduplication using a bounded in-memory cache.
- Added OpenTelemetry counters for counted, deduplicated, and failed view-recording operations.

### Security

- Records views only after file existence and owner-or-administrator authorization succeeds; rejected and missing-file requests cannot inflate counters.
- Keeps all analytics fields server-owned and excludes viewer identities and file identifiers from metric labels.

### Reliability

- Added an atomic metadata-store increment that prevents lost updates and preserves view data through file moves and renames.
- Keeps authorized file pages available when non-critical analytics persistence fails.
- Added deterministic migration, deduplication, authorization, metrics, preservation, and 100-way concurrency tests.

## [0.0.1-preview.25] - 2026-08-20

### Added

- Added persistent Web Page fetch sources and `POST /api/fetch/web-page` for saving bounded HTML/XHTML responses as ordinary owned files in root or a selected virtual directory.
- Added Web Page destination selection, source execution, activity metadata, structured logs, OpenTelemetry metrics, per-user rate limiting, concurrency limits, cancellation, and atomic temporary-file finalization.
- Added deterministic tests for ownership, redirects, HTTP failures, content types, response limits, cancellation cleanup, filename derivation, and SSRF address classification.

### Security

- Added DNS-aware SSRF protection for every initial and redirected destination, blocking local, private, link-local, multicast, reserved, documentation, and cloud metadata address ranges.
- Pinned outbound connections to validated DNS answers to mitigate DNS rebinding, disabled automatic redirects, cookies, and proxy inheritance, and limited requests to standard HTTP/HTTPS ports.
- Scoped persisted fetch sources and activity history to their authenticated owner; administrators retain access to unowned legacy records for migration.

## [0.0.1-preview.24] - 2026-08-20

### Added

- Added a responsive virtual-directory browser to the file dashboard with folder cards, breadcrumbs, creation, rename, move, empty-folder deletion, and folder ZIP downloads.
- Added an accessible destination picker for moving owned files and folders through the UI.
- Added a destination-folder selector to the upload page and preserved the selected directory after upload.

### Changed

- Scoped dashboard pagination, sorting, and file counts to the authenticated user's current virtual directory.

## [0.0.1-preview.23] - 2026-08-20

### Added

- Added user-owned virtual directories with nested creation, metadata-only file and directory moves, renaming, immediate content listing, and ordered breadcrumbs.
- Added recursive, disk-spooled directory ZIP downloads with empty-directory preservation and safe archive entry paths.
- Added stable file metadata IDs and optional `DirectoryId`; null remains the backward-compatible root representation.
- Added empty-directory deletion with conflict responses for non-empty directories.
- Added directory and file-placement APIs with OpenAPI documentation.

### Security

- Centralized directory ownership checks across creation, listing, movement, deletion, breadcrumbs, uploads, and archive generation.
- Added cross-user security, cycle prevention, path traversal, legacy metadata migration, and bounded-memory archive regression tests.

### Changed

- Applied the non-destructive `VirtualDirectoriesV1` JSON metadata migration for historical files without relocating stored objects.

## [0.0.1-preview.22] - 2026-08-20

### Fixed

- Allowed authenticated administrators to open an HTTP Aspire dashboard on loopback addresses even when the AppHost launches Felix with a production environment name.
- Preserved the HTTPS requirement for every non-loopback Aspire dashboard configured in production.

## [0.0.1-preview.21] - 2026-08-20

### Changed

- Upgraded the .NET Aspire AppHost SDK and dashboard from 13.4.6 to 13.5.0, including its CLI bundle execution model and deterministic AppHost discovery configuration.

## [0.0.1-preview.20] - 2026-08-20

### Added

- Added an Aspire dashboard link to the primary navigation for administrators when a dashboard URL is configured.

### Security

- Protected the Aspire redirect endpoint with the `Admin` role and require an HTTPS dashboard URL outside development.
- Explicitly retained Aspire's authenticated dashboard mode and documented production OIDC authorization with a required administrator claim.

## [0.0.1-preview.19] - 2026-08-20

### Fixed

- Explicitly enabled always-on OpenTelemetry trace sampling for Aspire development runs so inbound HTTP spans cannot be suppressed by inherited sampler settings.
- Enabled exception recording on ASP.NET Core spans to make failed requests diagnosable from the Aspire Traces view.

## [0.0.1-preview.18] - 2026-08-20

### Fixed

- Fixed empty configured OTLP endpoints incorrectly overriding Aspire's injected `OTEL_EXPORTER_OTLP_ENDPOINT`, which left the Structured Logs, Traces, and Metrics dashboard pages empty.
- Tuned Aspire development export intervals so logs and traces arrive within about one second and metrics within five seconds.

## [0.0.1-preview.17] - 2026-08-20

### Fixed

- Updated the repository and GitHub Actions SDK pin to the current .NET 10.0.400 release so local SDK resolution succeeds after installing the latest .NET 10 SDK.
- Documented the correct hot-reload command: `dotnet watch --project app run`.

## [0.0.1-preview.16] - 2026-08-20

### Changed

- Migrated the application, test suite, and Aspire AppHost from .NET 8 to .NET 10 LTS.
- Updated the Aspire development dashboard to 13.4.6 and the .NET test SDK to 18.8.1.
- Updated Docker build/runtime images and GitHub Actions test tooling to .NET 10.
- Added an SDK pin for reproducible .NET 10.0.302 builds.
- Removed obsolete `System.Net.Http` and `System.Text.RegularExpressions` package overrides in favor of the serviced .NET 10 framework implementations.
- Removed the ineffective legacy `ServicePointManager` connection-limit setting; HTTP connections remain managed by `IHttpClientFactory`.

## [0.0.1-preview.15] - 2026-08-20

### Added

- Added a .NET Aspire AppHost that launches Felix File Service with an authenticated local diagnostics dashboard.
- Added OTLP structured-log export alongside the existing traces, runtime metrics, and custom application metrics.

### Changed

- OpenTelemetry now honors the standard `OTEL_SERVICE_NAME` environment variable and automatically uses Aspire-provided OTLP configuration.

## [0.0.1-preview.14] - 2026-08-20

### Added

- Added dual container publishing to Docker Hub and GitHub Container Registry so images appear under GitHub Packages.
- Added automatic GitHub Release creation for pushed `v*` tags, with prerelease detection for hyphenated semantic versions.

## [0.0.1-preview.13] - 2026-08-20

### Added

- Added automatic synchronization of `README.md` to the `lebiru/fileservice` Docker Hub overview after each successful image publication.

## [0.0.1-preview.12] - 2026-08-19

### Fixed

- Made file-path validation platform-independent by rejecting Windows and Unix separators, drive/stream syntax, UNC paths, and their encoded traversal variants on every operating system.

## [0.0.1-preview.11] - 2026-08-19

### Changed

- Updated the README dashboard screenshot to show the current Felix File Service interface and sortable file grid.

## [0.0.1-preview.10] - 2026-08-19

### Added

- Added a GitHub Actions pipeline that tests the application and publishes cached, multi-architecture Docker images to `lebiru/fileservice` with edge, semantic-version, commit-SHA, provenance, and SBOM metadata.

## [0.0.1-preview.9] - 2026-08-19

### Changed

- Replaced the home-page sort dropdown with accessible, bidirectional sortable headers for file name, size, upload time, expiry time, server name, and owner.

## [0.0.1-preview.8] - 2026-08-19

### Changed

- Renamed the user-facing application brand from Lebiru File Service to Felix File Service across navigation, login, footer, API documentation, background-job dashboard, page titles, and generated archive names.

## [0.0.1-preview.7] - 2026-08-19

### Added

- OpenTelemetry tracing, metrics, runtime instrumentation, OTLP export, and an authenticated telemetry dashboard with time-series charts.
- Focused security regression tests for path traversal and SSRF protections.

### Changed

- Modernized the shared application layout, navigation, typography, gradients, responsive behavior, accessibility, and telemetry presentation.
- Restricted destructive and integration-management actions to administrator and contributor roles, with administrator-only cleanup and metadata synchronization.
- Replaced generated and logged default credentials with an explicit one-time administrator bootstrap password.
- Migrated user passwords to salted ASP.NET Core Identity hashes and protected stored integration credentials with ASP.NET Core Data Protection.
- Added global antiforgery validation, login throttling, OAuth state validation, safer redirects, POST-only logout and token refresh, and security response headers.
- Enforced canonical storage paths, stricter MIME/signature validation, regex timeouts, and outbound HTTP destination validation.
- Disabled TLS certificate bypasses, unrestricted development CORS, dangerous active-content uploads, and client exposure of OAuth tokens.
- Updated vulnerable transitive packages to patched versions.

### Fixed

- Prevented directory traversal across upload, download, archive, rename, delete, transformation, expiry, and Gmail attachment paths.
- Prevented SSRF access to loopback, private, link-local, metadata, and nonstandard-port destinations.
- Prevented regex denial-of-service from unbounded user-defined patterns.
