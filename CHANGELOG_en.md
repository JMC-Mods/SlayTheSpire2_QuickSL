# Changelog

## [1.6.0] - 2026-7-11
### Added
- Added a master toggle for multiplayer features. Disabling it at runtime prevents QuickSL from affecting multiplayer compatibility; enabling it activates multiplayer SL and displays its detailed settings.

### Changed
- JmcModLib 1.7.0 or later is now required.

## [1.5.0] - 2026-07-03
### Added
- Added a configurable option to skip fade-in and fade-out transition animations. It is enabled by default.

## [1.4.1] - 2026-06-20
### Fixed
- Optimized the replay path used while saving to avoid compatibility issues caused by the ported 107.1 bug.

## [1.4.0] - 2026-06-19
### Changed
- Migrated to the official MOD release format.

## [1.3.4] - 2026-06-10
### Fixed
- Fixed an issue where the current turn could continue resolving after quick SL.

## [1.3.2] - 2026-06-06
### Fixed
- Fixed official ABI compatibility issues introduced by game version `0.107`.

## [1.3.0] - 2026-06-05
### Added
- Added an `S & L` entry to the pause menu, allowing quick SL from singleplayer or multiplayer runs.

### Changed
- JmcModLib must be upgraded to `1.3.0` or later.

## [1.2.0] - 2026-06-05
### Fixed
- Fixed quick SL failing to reload and return to the main menu after game version `0.107` renamed `RunManager.SetUpSavedSinglePlayer` / `RunManager.SetUpSavedMultiPlayer` to `RunManager.SetUpSavedSingleplayer` / `RunManager.SetUpSavedMultiplayer`.

## [1.1.0] - 2026-05-14
### Fixed
- Fixed compatibility with game version `0.105.1`.

## [1.0.1] - 2026-05-01
### Changed
- When the multiplayer confirmation popup is disabled, clients now silently validate their current executable state before syncing SL.

### Fixed
- Fixed multiplayer quick SL keeping the network connection alive while the old run synchronizer was not cleaned up, causing duplicate sync messages, progressive stutter, and black screens on later SL attempts.
- Fixed missing load-start barriers when host and clients loaded at the same time, which could leave one side stuck waiting in `CombatStateSynchronizer` and result in a black screen.
- Fixed clients failing to respond when they were in scene transitions or other non-executable states, causing the host to wait or send a cancel message after disconnection and hit errors.

## [1.0.0] - 2026-04-30
### Added
- Added multiplayer quick SL: in multiplayer runs, the host can trigger synchronized reloads with the same quick SL hotkey.

## [0.0.1] - 2026-04-30
### Added
- Added quick SL, allowing the current run save to be reloaded with a configurable hotkey.
- Used JmcModLib hotkey configuration with an enable checkbox.
- Added localized text for the settings UI.

