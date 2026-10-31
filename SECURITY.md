# Security policy

## Reporting a vulnerability

Please **don't** open a public issue for security problems.

Report it privately instead. On this repository, open the **Security** tab, then **Report a vulnerability**. That opens a private advisory that only the maintainers can see. Please include:

- what's affected (MM Launcher, MM Server Agent, or the release process) and the version
- steps to reproduce it, or a proof of concept
- what an attacker could do with it

You'll get a reply within 7 days. Please give us a reasonable amount of time to release a fix before you talk about it publicly.

## Supported versions

Only the latest release gets security fixes. The launcher updates itself. The server agent is updated by its admin with `update-agent.bat`.

## Scope

In scope:
- the launcher (`MMLauncher.exe`): self-update, launching DayZ, mod repair, problem reports
- the server agent (`MMServerAgent.exe`): its HTTP endpoints, report handling, Discord forwarding, install and update
- the GitHub Actions release workflow and the published release files

Out of scope:
- DayZ, BattlEye, Steam and the Steam Workshop themselves
- the content of Workshop mods
- denial of service that needs more traffic than one ordinary connection can send

The latest audit and its findings are in [docs/SECURITY-AUDIT.md](docs/SECURITY-AUDIT.md).
