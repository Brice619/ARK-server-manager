# ARK Server Manager

A free desktop application for managing local ARK: Survival Ascended (ASA) and ARK: Survival Evolved (ASE) servers and clusters. Windows is the primary platform. Linux implementation and installer packaging are included; native Linux runtime testing has been deferred.

Installers are distributed through [GitHub Releases](https://github.com/Brice619/ARK-server-manager/releases). Read [Getting started](docs/GETTING-STARTED.md) for setup and [Deferred work](docs/DEFERRED-WORK.md) for verification limits and further hardening.

## What the app manages

- Guided standalone server and cluster creation and local file installation through SteamCMD. Existing installations can be registered in place, copied or moved with confirmation. Recorded import recovery is available from Installations.
- Start, save, stop, restart, scheduled restarts and bounded crash recovery, with independent member status and background management after the window closes.
- Map settings, join/admin passwords, welcome messages, supported player-platform options, shared cluster defaults and per-map overrides.
- Mod IDs, optional metadata lookup, load order, presets and queued changes applied at startup or restart. Shared preset exports remove passwords.
- Hourly backups while servers run, retention based on runtime, separate shared-cluster data, archive verification, selected-map restore and interrupted-operation recovery.
- Local network information, confirmed Windows Firewall setup, router guidance and optional router mapping.
- Application update checks before opening, recovery to the installed version when an update fails, notifications and optional server/cluster startup selections.

The interface uses spacious, scrolling cards and separate settings pages. Additional servers do not shrink existing cards.

## Installation and updates

The intended distribution is a standalone installer. The .NET runtime and application files are bundled; the installer checks required tools and asks about their installation. Each ARK edition's game package downloads once into the chosen management folder and is reused for new servers. Large game files share physical disk storage; each server has separate saves, settings, logs, backups, and downloaded mods. ASA on Linux requires the managed Windows compatibility environment. See [0.1.1 changes](docs/RELEASE-0.1.1.md).

The public repository is `Brice619/ARK-server-manager`. Automatic updates use stable three-part version tags such as `v0.1.0`, and these assets:

| Purpose | Windows | Linux |
| --- | --- | --- |
| Installer | `ARK-server-manager-setup-win-x64.exe` | `ARK-server-manager-setup-linux-x64.tar.gz` |
| Automatic update | `ARK-server-manager-win-x64.zip` | `ARK-server-manager-linux-x64.zip` |

Update archives are consumed by the app; they are not an alternative installation method. GitHub's release asset SHA-256 digest is required by the update client.

## Building from source

Install the .NET 10 SDK. Run builds and tests sequentially because Windows integration fixtures use compiled Host files.

```powershell
dotnet restore ArkServerManager.slnx
dotnet build ArkServerManager.slnx -c Release --no-restore
dotnet test tests/ArkServerManager.Core.Tests/ArkServerManager.Core.Tests.csproj -c Release --no-build --no-restore
```

Tests cover process/lifecycle behavior, configuration queues and recovery, cluster isolation, private records, archive verification, runtime retention, provider responses and selected native Windows adapters. They do not replace live game or installed UI checks. Real Windows ASA/ASE startup, authenticated saves and stops, ASE world restore and selected mod installation have separate development evidence.

For a new release-candidate folder outside the source tree:

```powershell
./scripts/Build-ReleaseArtifacts.ps1 -Version 1.0.0 -Destination C:/ARKBuilds/candidate-1.0.0
```

The script restores, builds and runs Windows checks before packaging both platforms. It produces the four assets, a release manifest and checksum list. Linux is cross-built without native execution. Building candidates does not publish them or certify an installed release.

GitHub Actions provides Windows checks on changes; the tagged 0.1.0 source passed its public checks. A separate workflow provides manually requested installer candidate builds.

## Current verification limits

The installed Windows app has opened and connected to its background manager, and its creation guide has been exercised through location/port review. Additional installer/update, live privileged backup, interruption and full game/player/mod/platform checks are recorded in [Deferred work](docs/DEFERRED-WORK.md). Native Linux runtime behavior is unverified by user choice. Windows session shutdown has targeted native message checks; an actual computer shutdown with live game servers has not been exercised.

The docs folder contains setup instructions, release notes and deferred work. The desktop layout and included original map artwork follow the approved visual direction.

## License

Application source is licensed under MIT. Dependency licenses remain applicable; included third-party notices are preserved in packaged files. ARK names and trademarks belong to their respective owners. This project is independently developed.
