# Changelog

## [1.3.0] - 2026-06-05
### Added
- 新增暂停菜单 `S & L` 入口，可在单人或多人局中通过暂停菜单触发快速 SL。

### Changed
- 前置模组 JmcModLib 需要升级到 `1.3.0` 或更高版本。

## [1.2.0] - 2026-06-05
### Fixed
- 修复游戏 `0.107` 将 `RunManager.SetUpSavedSinglePlayer` / `RunManager.SetUpSavedMultiPlayer` 重命名为 `RunManager.SetUpSavedSingleplayer` / `RunManager.SetUpSavedMultiplayer` 后，快速 SL 无法重新载入并回到主界面的问题。

## [1.1.0] - 2026-05-14
### Fixed
- 修复了`0.105.1`版本兼容性问题


## [1.0.1] - 2026-05-01

### Changed

- 关闭多人确认弹窗时，客机会先静默确认当前可执行状态，通过后再同步 SL。

### Fixed

- 修复多人快速 SL 保留网络连接时旧局同步器未清理，导致重复同步消息、逐渐卡顿以及再次 SL 黑屏的问题。
- 修复主客机同时载入时缺少开始载入屏障，导致一方进入 `CombatStateSynchronizer` 等待后黑屏的问题。
- 修复客机处于场景切换等不可执行状态时不回复主机请求，导致主机等待或断线后发送取消消息报错的问题。

## [1.0.0] - 2026-04-30

### Added

- 新增多人快速 SL：多人局中主机可使用同一个快速 SL 热键发起同步重载。

## [0.0.1] - 2026-04-30

### Added

- 新增快速 SL 功能：通过可配置热键重新载入当前局存档。
- 使用 JmcModLib 的带启用复选框热键配置。
- 新增设置界面本地化文本。
