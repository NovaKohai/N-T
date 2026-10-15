# Security policy

## Supported versions
Only the latest release receives security fixes.

## Reporting a vulnerability
Please do not open a public issue for security problems. Use GitHub's **Report a vulnerability** option on the repository's Security tab (private advisory). Include what you found, how to reproduce it, and the NeoDM version.

You can expect an acknowledgement within a few days. Please give us reasonable time to fix the issue before disclosing it.

## Scope notes
- The browser extension talks to the app only through a native messaging host. The host accepts requests only from the NeoDM extension and connects to the app over a current-user-only pipe.
- Proxy passwords are stored encrypted with Windows DPAPI for the current account.
- The extension's signing key is never part of this repository.
