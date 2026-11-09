# 电视助手 Windows 便携版

完整解压 Release 中的便携 ZIP，然后双击「电视助手.exe」。

- 连接电视前，从 [Android Developers](https://developer.android.com/tools/releases/platform-tools) 下载 Windows 版 Platform-Tools，将解压后的内容放入便携包的 `files` 文件夹，让 `adb.exe` 与 `setup.ps1` 同级。电视开启 ADB 调试，电脑和电视连接同一局域网。
- 要安装播放器，从 [TiviMate 官网](https://tivimate.com/) 获取 APK，命名为 `tivimate.apk` 放入 `files`，然后点「安装 TVM 播放器」或「更换主页并安装播放器」。
- 点「导入播放列表」选择自己的 M3U 或 M3U8 文件。节目表可单独导入。导入后点「启动直播源」，再将订阅地址填到电视。电视首次导入和刷新时保持助手运行。
- 恢复主页时，使用助手里的「恢复原主页」。恢复记录放在便携文件夹旁边。
- 从浏览器下载后，Windows 可能显示 SmartScreen 提示。遇到提示时，先确认文件来自本仓库 Release。

公开版随包提供主页和 Home 替身，直播源、节目表、播放器和 ADB 由用户自行准备。使用「查看更多候选源」前，可在 `files/sources.json` 中配置有权使用的候选清单，每项包含 `name`、`url` 和 `enabled`。

## 编译 Windows 客户端

在 Windows 上用 .NET Framework 4.8 运行 `windows/build.ps1 -OutputDirectory <输出目录>`。脚本生成 EXE、配置和运行脚本。电视端主页源码在 `launcher/`，Home 替身源码在 `home-bridge/`。便携 ZIP 已提供该版本的两个 APK。
