# ARK Server Manager 0.1.3

Adds the approved native server and cluster management screens, with the existing ARK artwork, consistent spacing, centered controls, and content-only fade-in when changing tabs. Cluster overview uses its tabs directly; the redundant bottom shortcuts are removed.

## Server management

- Settings categories save documented ARK configuration keys and launch preferences, preserving unrelated entries. Numeric controls are centered, settings groups remain expanded in one scrolling column, and the bundled engram and creature catalogs are available without first creating overrides.
- Backups create actual world/player archives, verify them before restore or deletion, and support daily schedules with a saved time zone. Scheduled retention keeps manual archives. Restores preserve configuration files and shared cluster data.
- Players shows recorded identities and live observations, with search and filters. Admin and exclusive join lists save the documented files while the map is verified stopped. Live kick, ban, and unban use RCON; stopped bans are visibly queued for the next running session.
- Console shows actual logs and RCON replies, with search, level filtering, export, follow output, and clear view. Clear view leaves log files intact. An unknown command result is checked without automatically resending the command.

## Cluster management

- Transfers saves upload/download rules for survivors, creatures, and items, with independent per-map Inherit, Allow, or Block choices. Running maps queue configuration changes for next start.
- Cluster Backups supports a selected map scope and optional shared transfer data, coordinated captures, daily schedules, verified restoration, and recorded rollback. Restore requires the whole cluster to be verified stopped and leaves it stopped. Existing automatic captures remain available.
- Activity shows events belonging to the cluster and its member maps, with search, map/event filters, time sorting, follow activity, CSV export, and clear filters. Filtering does not alter the journal.

## Production cleanup

The normal installed application no longer offers the dummy server feature. Development visual-review fixtures are compiled only when explicitly enabled; their sample data and actions are isolated from registered servers. Existing preview files are not deleted as part of this cleanup. Superseded player, console, cluster map-tool, and announcement UI code has been removed after checking its remaining callers.

## Verification limits

Native page checks and isolated file/operation tests cover these management controls. They do not prove every setting, transfer, player action, or restore outcome in a running ARK game. The current work does not add live production game tests. Native Linux runtime testing remains deferred, and the existing limits in [Deferred work](DEFERRED-WORK.md) still apply.
