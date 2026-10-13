# ARK Server Manager 0.1.2

Fixes shared game-file preparation for long ARK asset paths on Windows. Native hard-link creation now uses extended Windows paths for both the game file and its temporary destination. This also fixes paths that exceed the traditional limit only after adding the temporary filename.

Adds a regression check that registers and reuses a package with asset paths longer than 260 characters and verifies that both servers reference the same physical file.

Shared storage, per-server state isolation, and window-drag repairs from 0.1.1 remain included. Windows and Linux installers and update archives are provided. Linux runtime testing remains deferred.
