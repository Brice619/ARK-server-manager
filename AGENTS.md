# ARK Server Manager working instructions

## Screen transitions

- Every screen change must have a transition animation, including forward navigation, back navigation, and switching tabs or settings pages.
- When the user has not specified the transition for a screen change, ask which animation they want before implementing it. Do not choose an unspecified transition on their behalf.
- Reuse a transition already explicitly approved for that screen or for an applicable group of screens; do not ask again about an approved choice unless the scope changes.
- Approved choice: fade-in for the solo-server and cluster detail screens, their tabs and clickable panels, and return navigation to All Servers.
- Tab navigation fades only the content controlled by that tab. Persistent header, server title, lifecycle buttons, badges and tab navigation remain visible and stationary. Selected tabs use normal-weight text and a cyan underline, without clipping.

## Installing changes

- Install completed, verified updates into the user's local app as part of the work. A source-only change or preview is not the completed update.
- Preserve server settings, saves, mod choices, and VirtualBox/VMs. Back up replaced application files and verify the installed result.
- Limit UI checks to the screen being changed and its immediate navigation destinations unless the user asks for broader testing.

## Visual fidelity

- Follow the user's approved screen images, supplied artwork, dimensions, spacing, placement, colors, and lettering. Do not add unsolicited visual controls or redesign approved layouts.
- Compare the rendered screen at the reference dimensions and inspect the installed screen before claiming visual completion.
- Check centering explicitly: button icon/text groups, title/action rows, overview panels, card rows, and navigation controls. A successful build alone does not verify layout.
- All Servers fits vertically without page scrolling; its server and cluster lists scroll horizontally. Center the back-link icon with its text, keep its hover background transparent, and center both cluster scroll chevrons in their buttons.
- Remove replaced or unnecessary code only after confirming its remaining dependencies.

## Settings screens

- General and the remaining settings menus follow the supplied references except the user's explicit correction: all settings group cards stack in one vertical scrolling column. Do not put group cards side by side. Keep stat tables and override rows expanded; do not replace them with collapsed dropdowns. Preserve space between groups and controls.
- Settings category navigation fades only the category content. The search area, category navigation, footer, server header and tabs remain visible.
- Every editable field must read and save its actual ARK configuration key or launch preference. Preserve unrelated INI entries, repeated override objects and unknown tuple members.
- The production app does not include a dummy server or preview-server entry. Any settings visual-review fixture is development-only and must write exclusively to its own isolated files; never apply fixture edits to a registered server.
- Settings scrollbars use the approved blue track and thumb with arrows; the Mods list uses its reference's thin scrollbar without arrows.
- Numeric values must be centered horizontally and vertically; inspect normal, compact stat, and engram inputs after padding/template changes.
- Engram rows contain item names without pictures, share identical column sizing with their header, and use one virtualized scrolling list. Keep the auto-unlock control clearly labeled above the list, outside the search field.
- Creature overrides show the bundled catalog without requiring pre-existing overrides. Preserve loaded custom classes and name tags. Untouched catalog defaults must not create INI entries.
- Editable list rows have no hover, selection, or focus background highlight. Keep the controls themselves interactive.

## Backups, Players, and Console

- Follow the three approved solo management references; retain the existing header and normal-weight main tabs. Fade only the changing content.
- Backups contain actual world/player archives, verified before restoration or deletion. Preserve configuration files and shared cluster data. Daily schedules persist their time zone and retain only scheduled managed archives; manual archives are not removed by scheduled retention.
- Development management fixtures use only their own isolated archives and player access files. Production builds exclude fixture entry points and sample data; never create or edit a registered server from a fixture.
- Player access and exclusive join lists save their documented ARK files while the map is verified stopped. Explain next-start behavior. Live kick/ban/unban use RCON; stopped bans are persisted and clearly labeled as queued until the map is running.
- Console filters actual log output and RCON replies. Clear View never deletes log files. Unknown command results are checked without resending the command. Do not claim live game checks from fixture tests.

- Cluster overview has no bottom Transfers/Backups shortcuts; use its existing tabs. Map-scroll arrows are equal-sized, centered, aligned with the map-section title, separated by 12 pixels, and spaced clear of the cards.

## Cluster tabs

- Transfers, Backups, and Activity follow the approved cluster-tab references. Retain the app's existing logo size, no redundant Partial badge, normal-weight main tabs, and body-only fade. Bottom tab shortcuts remain removed.
- Transfers saves six documented upload/download keys and the master download flag. Keep per-map Inherit/Allow/Block independent for both directions, and preserve unrelated settings. Running maps queue changes for next start; stopped writes use the existing verified writer.
- Cluster Backups supports selected map scope and optional shared transfer data, actual coordinated capture, daily timezone scheduling, scheduled-only retention, verified restoration, and recorded rollback. Excluded maps and server configuration stay unchanged. Restore requires the whole cluster verified stopped and leaves it stopped. Existing automatic captures remain listed.
- Activity scopes actual member-map and cluster events, filters/searches/sorts, follows new entries, exports CSV, and clears filters without altering the journal. Preview events exist only under designPreview.

## Development fixtures

- EnableDesignReview is an explicit development build option. The standard installed app must not expose --design-preview, dummy navigation, simulated lifecycle actions, or seeded sample records.
- Retain isolated visual and action fixtures when they verify actual controls; do not treat them as production features. Production cleanup preserves existing user files, including prior preview files, unless the user explicitly requests their removal.

## Filesystem boundaries

- Never create, allocate, or map drive letters for game launches or tools. Launch from the registered original directory. Do not replace drive mappings with hidden filesystem aliases or relocate user installations without explicit instructions.
- Removal-only migration may retire a mapping recorded as owned by an older app version after every dependent server is verified stopped; preserve unexpected targets and unrelated drives.

## Necessity and startup behavior

- Establish the concrete need for a change and what the existing system already handles before implementing it. Tests must verify the user requirement, not merely successful execution of the chosen mechanism.
- Normal start, restart, and recovery must not scan, repair, republish, or synchronize the shared game-file installation. Keep game installation work in explicit creation, installation, update, or repair operations.
- Do not start the user's server while diagnosing or verifying this startup change; use isolated process fixtures.
