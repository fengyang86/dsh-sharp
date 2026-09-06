# 平台图标打包

DSH-Sharp 使用同一套品牌图形，但按平台分别注册应用身份：

- Windows：`src/DSHSharp/Assets/avalonia-logo.ico` 通过项目的 `ApplicationIcon` 嵌入可执行文件。
- Linux：安装 `linux/dsh-sharp.desktop`，并将 `linux/icons/hicolor` 安装到系统图标目录。
- macOS：将 `macos/Info.plist` 与 `src/DSHSharp/Assets/Brand/DSHSharp.icns` 放入 `.app` 包。

窗口、托盘和 Windows 可执行文件统一使用原 DSH 黑色鲸鱼图标 `avalonia-logo.ico`；ICO 内含多尺寸资源，系统会按显示尺寸选择清晰版本。
