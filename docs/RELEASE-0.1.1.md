# ARK Server Manager 0.1.1

New servers now reuse the installed game package instead of downloading another full installation for each map. Large shipped game files share the same physical disk storage through filesystem hard links. Saves, configuration, logs, backups, and downloaded mods remain separate for each server.

- Reuses packages downloaded during setup, including completed downloads from 0.1.0.
- Keeps the shared package under `GameFiles` in the chosen management folder. File Explorer may report the logical game-file size in each server folder; the linked files occupy disk space once.
- Detaches a stopped server's shared files before SteamCMD writes an update or repair, so running maps retain their files. Successful updates are deduplicated again; other maps receive the updated package at their next start.
- Fixes dragging the custom Windows header, double-click maximize/restore, and dragging from a maximized window.

An update or repair can temporarily require space for private copies of the shared files. Normal server creation reuses the package without that temporary full copy. Shared files require a filesystem supporting hard links on the same volume, such as NTFS on Windows. Unsupported filesystems report the requirement rather than silently duplicating the game.

Existing server state is preserved. Completed old managed installations can be converted to shared storage without another game download. External imported installations retain their original ownership.

Linux packages are included; native Linux runtime testing remains deferred. Installers remain unsigned. Mod-specific settings forms remain deferred as documented.
