# Hyplay Craft Launcher (HYCL)

Hyplay Craft Launcher（简称 **HYCL**）是使用 Avalonia 12 + .NET 10 编写的跨平台 Minecraft 启动器，目标是在 Windows 上完整可用，同时保持 macOS / Linux 可构建。

## 名称约定

- 对外全称：**Hyplay Craft Launcher**
- 对外简称：**HYCL**（统一大写）
- 代码标识符（命名空间、项目名、程序集名）沿用 `Hycl.` 前缀，不随展示名称改变。

## 构建与运行

```bash
dotnet build Hycl.Launcher.sln -c Debug
dotnet run --project src/Hycl.Launcher.UI
```

要求 .NET SDK 10 与 Avalonia 12.1.4；`Directory.Build.props` 已启用 `TreatWarningsAsErrors` 与 `Nullable=enable`，构建应为 0 警告 0 错误。

## 来源与许可

- 业务行为（下载、版本安装、实例管理、账号、Java 探测、启动参数）参考 **PCL-CE-dev**，该项目使用 Apache License 2.0；本仓库的 [LICENSE](LICENSE) 保留同一许可文本。
- 界面组织与视觉风格参考 **PCL.Neo-main**。该仓库使用自定义的 “All Rights Reserved” 许可（并非 OSI 认可的开源许可），其中“重度使用”条款要求基于它的作品名称以 `Plain Craft Launcher (PCL)` 开头。本项目品牌为 Hyplay Craft Launcher / HYCL，无法满足该条款，因此**没有复制其源码、XAML、资源或图标**，仅借鉴布局与配色思路，界面全部以 Avalonia 原生方式独立实现。
- 本项目不使用 WPF / `System.Windows.*`，不使用 WebView，`Hycl.Launcher.Core` 不依赖任何 UI 框架。
