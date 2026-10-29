# Security policy

HardwareLens reads local hardware/performance information and includes explicit process, startup, and service management actions. It is not exclusively read-only. Start with standard-user privileges; elevate only for a specific need.

For security reports, use GitHub private vulnerability reporting if the repository offers it. Do not post exploit details, credentials, real logs, inventories, recordings, or identifying screenshots in public issues. If private reporting is unavailable, open a minimal issue requesting a private contact route without disclosing the vulnerability.

Settings, incident records, reports, and recordings are not encrypted by HardwareLens. Device identifiers are excluded from report exports by default, but other fields may remain private. See [permissions and privacy](docs/permissions-and-privacy.md).

Process identity checks, critical-process refusals, and essential-service refusals are safeguards, not a complete security boundary. Windows access control remains authoritative. Unsigned portable builds do not have publisher-signature verification or automatic updates. Maintain the .NET runtime and third-party dependencies when distributing builds.
