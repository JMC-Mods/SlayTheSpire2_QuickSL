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

---
## 🧠 1. 简介
QuickSL 是一个《杀戮尖塔 2》MOD，用于通过可配置热键快速重新载入当前局存档。

实际效果等同于在当前局中执行“保存并退出”，再从主菜单点击“继续游戏”，但会跳过主菜单交互流程。

[演示视频（B站）](https://www.bilibili.com/video/BV1BnwXziEsc)

[Github仓库](https://github.com/JMC2002/SlayTheSpire2_QuickSL)
## ⚙️ 2. 功能
- 在 JmcModLib 的 MOD 设置界面中提供带启用复选框的热键配置
- 默认热键为 `F5`
- 当前版本仅支持单人局
 
## 🔔 3. 提醒
- **本模组强依赖于模组[JmcModLib](https://github.com/JMC2002/JmcModLib_STS2/releases)**
- 当前版本仅支持单人局。
 
## 🧩 4. 兼容性
- 由于游戏处于EA阶段，可能会随着游戏版本更新而失效

## 🧭 5. TODO
- 待定

**如果你喜欢这个 Mod 的话，希望可以点一个star~**

如果你真的很有钱，可以考虑给我赞助，给我赞助你得不到任何东西，但是可以吓我一跳。

![图片描述](pic/wechat_qrcode.png)
