**🌐[ 中文 | [English](README_en.md) ]**

[📝更新日志](CHANGELOG.md)

[📦 Releases](https://github.com/JMC2002/SlayTheSpire2_QuickSL/releases)

# QuickSL
##  0. 安装

### Mod本体安装
Steam版本直接在创意工坊订阅即可（暂未开放）

其他版本可以自行编译，或者在[📦 Releases](https://github.com/JMC2002/SlayTheSpire2_QuickSL/releases)界面下载.zip后解压到游戏安装目录下的Mods
目录下（没有就新建一个）

### 前置安装
**此外，本模组强依赖于模组[JmcModLib](https://github.com/JMC2002/JmcModLib_STS2/releases)**，安装方法同上

安装完成后的目录结构如下：

```sh
-- Slay the Spire 2
    |-- SlayTheSpire2.exe
        |-- mods
             |-- JmcModLib
             |-- QuickSL
                  |-- QuickSL.dll
                  |-- QuickSL.pck
                  |-- QuickSL.json
```

### 存档迁移
> 当你第一次安装MOD，游戏会默认将开启Mod的存档与没开启的隔离，可以按下面的方法迁移存档：

在安装好MOD后第一次打开游戏会询问是否启用MOD，启用并再次打开游戏一次后，退出游戏，将`%appdata%\SlayTheSpire2\steam\`下面的数字文件夹下的你对应的存档文件粘贴到该文件夹的`modded`文件夹中，以同步使用MOD前后的存档

迁移完成后的目录结构如下：

```sh
-- %appdata%\SlayTheSpire2
    |-- logs                                # 日志文件夹
    |-- steam
        |-- <steamId>
             |-- profile1
             |-- profile2
             |-- profile3
             |-- modded
                  |-- profile1
                  |-- profile2
                  |-- profile3
```
---
## 🧠 1. 简介
QuickSL 是一个《杀戮尖塔 2》MOD，用于通过可配置热键快速重新载入当前局存档。

实际效果等同于在当前局中执行“保存并退出”，再从主菜单点击“继续游戏”，但会跳过主菜单交互流程。

[演示视频（B站）](https://www.bilibili.com/video/BV1BnwXziEsc)

[Github仓库](https://github.com/JMC2002/SlayTheSpire2_QuickSL)
## ⚙️ 2. 功能
- 在 JmcModLib 的 MOD 设置界面中提供带启用复选框的热键配置
- 默认键盘热键为 `F5`，默认手柄热键为按下右摇杆
- 支持单人局快速重新载入当前局存档
- 支持多人局由主机或客机发起同步快速 SL，单人和多人复用同一个热键
- 多人局可配置主机是否允许客机发起快速 SL
- 多人局可配置客机发起时是否先弹窗询问主机
- 多人局可配置是否在执行前弹窗询问其他客机
- 关闭询问后，客机会在后台静默确认当前可执行状态，通过后同步执行快速 SL

### 多人快速 SL 流程
1. 主机或客机按下快速 SL 热键。
2. 如果由客机发起，主机会先检查是否允许客机发起；关闭时会直接拒绝本次请求。
3. 如果由客机发起且允许继续，主机会按“客机发起 SL 时询问主机”设置决定是否弹窗确认；关闭时主机会直接进入下一步。
4. 如果启用了“多人 SL 前询问客机”，除发起 SL 的客机外，其他已连接客机会弹出确认窗口；关闭时则自动进行状态确认。
5. 所有需要确认的玩家同意或静默确认可执行后，主机把当前多人存档作为本次加载的权威数据发送给客机，并同步执行快速 SL。
6. 任意玩家拒绝、超时，或当前状态不适合加载时，本次快速 SL 会取消。

### 多人存档同步说明
多人快速 SL 以主机当前多人存档为准，客机不需要拥有本地 `current_run_mp.save`。主机会在执行时发送一次序列化后的当前局存档，客机收到后按自己的玩家 ID 进行原版存档归一化，再载入同一局状态。

当前实现发送的是未压缩 JSON 存档。本机测试中的多人存档约为 `66.9 KiB`，一般情况下预计在几十到数百 KiB 之间；单次同步上限为 `1 MiB`。如果后续遇到体积问题，可以进一步改为压缩后传输。
 
## 🔔 3. 提醒
- **本模组强依赖于模组[JmcModLib](https://github.com/JMC2002/JmcModLib_STS2/releases)**
- 多人局中所有玩家都需要安装 最新版的QuickSL和前置
- 多人快速 SL 会使用 QuickSL 自定义网络消息，不同版本之间可能不兼容。
 
## 🧩 4. 兼容性
- 由于游戏处于EA阶段，可能会随着游戏版本更新而失效

## 🧭 5. TODO
- 待定

**如果你喜欢这个 Mod 的话，希望可以点一个star~**

如果你真的很有钱，可以考虑给我赞助，给我赞助你得不到任何东西，但是可以吓我一跳。

![图片描述](pic/wechat_qrcode.png)
