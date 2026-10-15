# Getting started

## Install

Download the installer for your operating system from the GitHub release. On Windows, run the setup executable. On Linux, extract the setup archive and run `ArkServerManager.Installer`.

Choose a parent folder and a new dedicated installation folder. The app includes its runtime. Setup offers running-backup support and optional ASA/ASE server downloads. You can defer game downloads and install them when creating a server. Existing prerequisites are checked before missing tools are installed. System permission may be required for backup or compatibility tools.

## Create a server or cluster

Game files downloaded during installation are reused. New maps share the physical game files while keeping their own saves, configuration, logs, and downloaded mods. If the chosen ARK edition is missing, its package is downloaded once. Shared files live under `GameFiles` in your management folder; do not edit or remove them manually. File Explorer may count linked game files in each folder's logical size even though the disk stores those files once.

Use **Create Server / Cluster**, choose a standalone server or cluster, then choose ASA or ASE. A map runs as its own server process. A cluster connects several maps from the same edition; ASA and ASE cannot share a cluster.

Choose a map, enter its name and player limit, and set PvE/PvP and local-network or internet access. Enter or generate an administrator password. A join password and welcome message are optional. Gameplay, access, networking and mods have separate setup categories.

At Review, check the location and ports, install missing server files, then create the server. New servers remain stopped until you choose Start. When creating a cluster, add another map or finish the cluster after each server is created.

## Use existing server files

Open **Installations** to search automatically or choose a folder manually. Inspect the detected installation, then choose its current location, a copy, or a move into the managed folder. Copy preserves the original. Move removes the original after a verified transfer.

Confirm the active map and launch settings, including ports and mod IDs. Saved filenames are suggestions; they do not prove which map the previous launcher used. The server must be stopped before registration or transfer. Unfinished recorded imports appear under **Unfinished imports** on the Installations page.

## Manage your worlds

Open a server or cluster card for controls and details. Use Start, Stop or Restart; cluster controls operate on its maps, while individual map controls affect only that map.

Settings are grouped by category. Saving a running server asks whether to restart now or apply the changes later. Queued settings apply at the next start or restart. The advanced editor provides direct access to configuration files.

Add mods by ID and review their order. Optional metadata and pictures depend on the provider being available. Supported player platforms depend on ARK edition, server build and installed mods; ASE Workshop mods require Steam clients.

Backups are created hourly while servers run and retained by runtime rather than wall-clock age. World/server state and shared cluster player/transfer state have separate backups; configuration settings remain separate from world restoration. Restoring one map stops only that map. A successful restart removes the replaced pre-restore state.

Internet hosting may need firewall permission and router setup. The app provides local network information, Windows firewall setup and router guidance; not every router supports automatic mapping.

## Connect over your local network (LAN)

Start the map and wait for it to finish loading. On the server's Overview page, copy the connection address. Use the hosting computer's LAN address, such as `192.168.40.70`, rather than `127.0.0.1` when playing from another computer. The address shown here is an example; use your own server's address.

For ARK: Survival Evolved on Steam:

1. On the computer you play on, open Steam > View > Game Servers > Favorites.
2. Click **+** and enter the hosting computer's LAN IP followed by the map's **Query port**, for example `192.168.40.70:27015`. Find that port in the map's Settings > Network. Each map can use a different query port.
3. Save the favorite, open ARK's server browser, and select Favorites. Refresh if necessary, select your server, and join. Enter the join password if one is configured.

The Overview connection address uses the **game port** (for example `192.168.40.70:7777`); Steam's Add server by IP uses the **query port** (typically `27015`). For a direct ASE connection without a join password, open the in-game console and enter `open 192.168.40.70:7777`, substituting your actual LAN address and game port.

The playing computer must be able to reach the hosting computer on the local network. Guest Wi-Fi or device isolation can prevent this. On Windows, apply the app's firewall setup for local-network access. LAN play does not require router port forwarding. The app's Local/Internet choice controls its firewall rule's remote-address scope; it does not guarantee placement in ARK's LAN browser list or change router configuration by itself.

A server can respond in Favorites while automatic LAN discovery does not list it. Use the saved LAN address to connect in that situation. Favorites are configured on each playing computer; adding one on the host does not add it on another PC. A responding listing confirms a query response, not a successful game join. Client edition, version and supported platforms must match the server. These Steam instructions apply to ASE; ASA uses its own in-game browser.

Reference: [ARK console commands](https://ark.wiki.gg/wiki/Server_commands) documents the direct connection command and game port.

## App updates and limits

The app checks for updates when launched. If an update fails, it opens the installed version with a notice. Closing the window can leave background server management running. Optional startup selections choose which servers or clusters start when the app opens.

See [Deferred work](DEFERRED-WORK.md) for unverified environments and further hardening, including the intentionally untested Linux runtime.
