# Changelog

All notable changes to this project are documented in this file. Versions follow Semantic Versioning.

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
