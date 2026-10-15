# Version 0.2.0

- Document LAN connections for ASE Steam players, including Favorites on the playing PC, LAN IP addresses, query versus game ports, and direct connections.
- Remove repeated shared game-package synchronization, Workshop compatibility lookups and full mod readiness scans from normal startup. Installation work remains in explicit install/update operations.
- Launch servers from their registered directory without allocating drive letters. Legacy mapping cleanup only removes verified app-owned mappings when dependent servers are stopped.
- Apply the selected Windows Firewall scope when saving network settings, retaining unsaved choices if setup fails. Prefer a local IPv4 address on an interface with a gateway for the displayed connection address.
- Starting a map no longer requires running-backup helper preparation. Running backups still require their separate Windows permissions setup.

## Verification and limits

Release packaging runs the Windows build and automated tests. Native Linux execution is unverified. Automated fixtures do not establish actual player joins or in-game effects. The reported regression where a server appears in Favorites but is absent from another PC's ARK LAN list remains unresolved; this release does not claim to fix that discovery issue.
