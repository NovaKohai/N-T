# Security policy

## Supported versions
Only the latest release receives security fixes.

## Reporting a vulnerability
Do not open a public issue for security problems. Use the "Report a vulnerability" option on the repository's Security tab on GitHub (private advisory). Include what you found, how to reproduce it, and the NeoDM version.

We acknowledge reports within a few days. Give us time to fix the issue before you disclose it.

## Scope notes
- The browser extension talks to the app only through a native messaging host. The host accepts requests only from the NeoDM extension and connects to the app over a current-user-only pipe.
- Proxy passwords are stored encrypted with Windows DPAPI for the current account.
- The extension's signing key is never part of this repository.
