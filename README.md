# 小米电视 CleanYourTV 定制版

基于 [Gathin23/CleanYourTV](https://github.com/Gathin23/cleanyourtv) 2.5.0 的个人电视桌面定制版本，保留原有首页布局，面向遥控器操作和小米电视使用场景进行调整。

## 当前功能

- 首页长按应用可调整位置、隐藏和找回，常用栏可指定位置。
- 优先使用应用自带横版封面；缺失时生成横版封面，部分应用有专属样式。
- 常用栏使用半透明底板；支持品牌柔光及内置图片壁纸。
- 主页控制中心包含输入源、网络、音量、电视设置和个性化入口。
- 主页屏保可选择时钟或图片轮播；实现为空闲 10 分钟启动、图片每 30 秒切换。
- 新增 HomeReturnService，针对部分小米电视打开原主页后跳转回本桌面。此功能依赖设备授权和系统行为。

## 构建

源码在 `launcher/`。需要 JDK 17、Android SDK 及 Android SDK 34。按标准 Android/Gradle 环境配置后运行：

```powershell
cd launcher
.\gradlew.bat assembleRelease
```

Release 构建配置使用本机 Android 调试签名。不同电脑的签名可能不同，覆盖安装时必须先确认签名兼容；不要为了解决签名冲突随意卸载现有桌面或清除数据。

## 使用与验证边界

本项目是个人设备的定制快照，不保证适配其他电视。已在原设备检查主页和部分遥控器操作；屏保完整计时及最新版本的重启、待机流程仍需实机确认。更改默认主页或系统设置前，请先保留原设置和恢复方式。

手机上传壁纸或 APK 目前只是讨论中的功能，尚未实现。项目不包含个人设备的 ADB 地址、备份和构建签名密钥。

## 壁纸素材

内置壁纸源自 Unsplash 与 Pexels 的公开图片，处理为 16:9 以供桌面展示。请在进一步分发或商业使用时核对原图及其各自许可：

- 雪山秋湖：https://images.unsplash.com/photo-1633923200488-a36486b9c15e
- 金色沙丘：https://images.unsplash.com/photo-1538419751423-564a86744b9c
- 冰湖极光：https://www.pexels.com/photo/5438527/
- 海边落日：https://www.pexels.com/photo/20512620/

## 许可和致谢

本项目基于 Gathin 的 CleanYourTV；项目代码沿用仓库中的 MIT 许可证，详见 [LICENSE](LICENSE)。字体及其他第三方素材遵循各自许可，详见对应资源文件。图片和应用品牌标识的权利归各自权利人所有。

