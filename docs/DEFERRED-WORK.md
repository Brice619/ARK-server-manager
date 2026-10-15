# Deferred work and verification limits

The user requested finishing the working application instead of continuing to expand unusual failure scenarios. The following work is recorded for later; these limits must remain visible in release notes.

- A reported ASE LAN-browser regression on another PC remains unresolved. The server responds to direct queries and host-side LAN broadcast probes; these do not verify remote discovery or a game join. Steam Favorites instructions provide an alternate way to locate the server.
- Native Linux testing is intentionally deferred. Linux is cross-built; ASA compatibility setup and Linux backup/system adapters have not been exercised on a Linux machine.
- Windows signing is not configured. Installer and update files include checksums and dependency notices, but no purchased signing identity is bundled.
- Full live player joining, transfers and every mod/map/platform combination have not been exercised. Crossplay remains constrained by ARK and mod support.
- Automatic mod-specific settings forms require verified definitions from each mod. Direct configuration-file editing is available instead.
- Live privileged Windows snapshot capture and an actual Windows shutdown with running game servers have not been exercised. Controlled tests cover the associated services and Windows session messages.
- Complete interactive installer folder selection and installed update handoff have not been exercised. Installed Windows startup, creation-guide navigation and location/port review have evidence.
- Extended power-loss, lost configuration-directory and elevated-helper interruption scenarios need further work. Normal settings edits, queued changes, restart application and recorded restore/import recovery remain implemented.
- An abruptly killed import before publication may leave an unregistered temporary copy beside the destination. Recovery retries from the unchanged original; comprehensive cleanup of abandoned staging folders is deferred.
- If cleanup of a moved source stopped partway through deleting its retired folder, automatic recovery may refuse its changed inventory. The registered destination is preserved and the remaining retired files require inspection.
- Linux automatic backup-tool installation is limited to the distributions and storage types implemented by the tool planner. Unsupported systems report the missing prerequisite instead of silently installing unrelated packages.
- An initial GitHub CI test attempt failed; two later checks of the tagged release source passed. Further intermittent-test diagnosis is deferred.

These are specific limits, not a claim that the application has been tested in every environment. Report practical problems through [GitHub Issues](https://github.com/Brice619/ARK-server-manager/issues).
