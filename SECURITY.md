# Security Policy

## Reporting a Vulnerability

Security issues and vulnerabilities should be reported privately and responsibly:

1. **Do not disclose the vulnerability publicly** in GitHub issues, forums, or social media until it has been investigated and addressed.
2. Please report findings directly to the repository maintainer or via GitHub Private Vulnerability Reporting.
3. Provide a clear description including:
   - Steps to reproduce
   - Potential impact
   - Affected versions or components
   - Any suggested remediations

## Security Architecture & Practices

- **Token Storage**: Microsoft OAuth tokens, Minecraft session tokens, and third-party API keys are strictly protected using Windows Data Protection API (DPAPI - `DataProtectionScope.CurrentUser`). No credentials or tokens are stored in plain text.
- **Log Sanitization**: Command-line arguments, session tokens (`--accessToken`, `--session`, `token:...`), and OAuth responses are automatically redacted before being written to logs or displayed in the live debug console.
- **No Password Interception**: The launcher uses OAuth 2.0 (Device Code Flow and browser redirection). User Microsoft passwords are entered directly on official Microsoft authentication servers and never touch the launcher.
- **Integrity Validation**: All downloaded game binaries and assets from Mojang CDN are verified against official SHA-1 checksums and expected byte sizes.
- **License Verification**: Only official licensed profiles verified through Mojang and Xbox Live / Minecraft Services APIs are permitted to launch the game. Unlicensed / cracked offline bypass modes are not permitted.
