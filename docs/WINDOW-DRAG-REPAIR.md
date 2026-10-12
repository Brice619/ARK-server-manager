# Installed window-drag repair — 2026-10-12

The custom Windows header now captures the pointer and moves the window using screen coordinates. Header buttons are excluded from dragging; double-click toggles maximize/restore, and dragging restores a maximized window.

Built successfully with no warnings or errors. Installed the desktop DLL in C:\Ark Servers\Ark Server Manager and refreshed its payload-manifest checksum. Native UI checks confirmed dragging from the app name and empty header space, double-click maximize, dragging from maximized state, and App Settings/Dashboard navigation. The background manager stayed running; server files were not changed.

This is a local installed repair. The published v0.1.0 installer remains unchanged.
