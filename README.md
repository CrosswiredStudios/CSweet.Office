# C-Sweet Satellite Office

C-Sweet Satellite Office is the independently installed execution plane for C-Sweet agents. It runs the unprivileged control client (`CSweet.SatelliteOffice.Node`) and privileged virtualization service (`CSweet.SatelliteOffice.RuntimeHost`) on Windows, Linux, and macOS.

This repository was extracted from C-Sweet commit `a85a19be588c82b1d7a6b9b4ed174bf3cd204409`. The import contains the former execution node, RuntimeHost, runtime abstractions and providers, native helpers, builder/runtime guests, payload tools, installer scripts, and certification utilities. Earlier history remains authoritative in the C-Sweet repository.

The headquarters gateway, scheduling, enrollment approval, certificates, artifact authorization/storage, guest broker streaming, and fleet UI remain in C-Sweet. Cross-repository messages are versioned in `CSweet.SatelliteOffice.Contracts`.

## Development

```powershell
dotnet build CSweet.SatelliteOffice.slnx -c Release
dotnet build CSweet.SatelliteOffice.slnx -c Release -p:UseLocalSatelliteOfficeContracts=false
```

Install Satellite Office independently, create a one-use enrollment in C-Sweet, connect it to the gateway, verify its fingerprint, and approve it. C-Sweet AppHost does not launch Satellite Office.

## Releases

Satellite Office follows semantic versioning and has independent `vX.Y.Z` tags. GitHub Releases is the canonical immutable asset origin; c-sweet.com links to those assets. Satellite Office never self-updates. Administrators drain an office to zero active assignments before running an upgrade.
