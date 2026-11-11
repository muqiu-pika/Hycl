# Hyplay Craft Launcher (HYCL)

Hyplay Craft Launcher（简称 **HYCL**）是使用 Avalonia 12 + .NET 10 编写的跨平台 Minecraft 启动器，完整支持 **Windows / Linux / macOS 的 x64 与 arm64**，游戏启动、Java 探测、插件与联机工具均按平台分发。

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

## 多平台发布

`.github/workflows/build.yml` 的 `publish` 矩阵覆盖 6 个 RID：`win-x64 / win-arm64 / linux-x64 / linux-arm64 / osx-x64 / osx-arm64`。每个 RID 产出两份单文件：

| 产物 | 说明 |
| --- | --- |
| `Hycl-<rid>`（Windows 为 `.exe`） | 自包含单文件，启用压缩、关闭 R2R 与裁剪，目标机器无需安装 .NET |
| `Hycl-<rid>-framework-dependent` | 框架依赖单文件，需目标机器已装 .NET 10 Desktop Runtime |
| 体积参考（win-x64，原生库已嵌入） | 自包含 ≈ 43.5 MB（剔除未使用的 Xml/ADO.NET 运行时后）、框架依赖 ≈ 29.7 MB |
| `Hycl-osx-*-app` | macOS 额外打包的 `.app` bundle（裸可执行文件没有 Dock 图标 / 菜单栏） |

- **自包含单文件必须关闭裁剪（`PublishTrimmed=false`）**：插件 dll 在运行时通过 `Assembly.LoadFrom` 动态加载，不参与宿主裁剪图。裁剪器会移除 `System.Runtime` / `netstandard` 等 type-forwarder facade 并重写宿主 IL 的引用，而插件仍引用 `System.Runtime, Version=10.0.0.0`，实测裁剪版加载插件必然抛 `FileNotFoundException`，表现为「插件已启用但工具页没有入口」。这是动态插件系统的硬性约束，不要为了缩小体积重新开启裁剪。
- **R2R 默认关闭**：`PublishReadyToRun` 会把每个程序集的 IL 预编译一份本机码，win-x64 自包含单文件（原生库嵌入）因此从 47.9 MB 涨到 59.2 MB（实测）。收益只是首帧从约 2.9s 提前到约 1.1s。需要更快冷启动时单独加 `-p:PublishReadyToRun=true` 即可，不要改回默认值。
- 交叉 RID 发布（含跨 OS 与跨架构）已实测：关闭 R2R 后 `linux-x64` 与 `osx-arm64` 均只输出单个可执行文件，体积与 Windows 同量级。
- `app.manifest` / `hycl.ico` 是 Win32 资源，仅在无 RID 或 `win-*` 目标时应用；非 Windows 目标跳过。
- macOS 的 `.app` 未签名，首次打开若被 Gatekeeper 拦截，执行 `xattr -dr com.apple.quarantine Hycl.app`。
- 陶瓦联机插件（`Hycl.Online.dll`）是 anycpu 托管程序集，与平台无关；真正的平台差异在 Hycl-online 清单的 6 个 Terracotta 资产（win/linux/mac × x64/arm64），由插件按 `HyclOnlineTools.ToolAssetKey` 挑选，Unix 上自动 `chmod +x`。
- 游戏启动的跨平台处理集中在 `PlatformInfo` / `JavaDetector` / `RuleEvaluator`：Java 可执行文件与搜索路径、classpath 分隔符、`java.library.path`、natives 目录均按平台取值；Win32 窗口标题注入 / 窗口探测只在 Windows 分支生效。macOS 上的 LWJGL 2 版本（1.12.2 及更早）会自动补 `-XstartOnFirstThread`。

本地交叉发布示例：

```bash
dotnet publish src/Hycl.Launcher.UI/Hycl.Launcher.UI.csproj -c Release -r osx-arm64 \
  --self-contained true -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true -o publish
```

## 配置

配置文件位于 `%APPDATA%\HYCL\config.json`（macOS / Linux 使用对应平台的用户数据目录）。除界面可设置的项外，下面两个配置可直接编辑：

| 键 | 说明 |
| --- | --- |
| `curseForgeApiKey` | 可选。覆盖内置的 CurseForge API Key；留空使用内置密钥。 |
| `communitySourceSolution` | 社区资源源策略：`0` 镜像优先（默认）、`1` 官方优先、`2` 仅官方。 |

**CurseForge 密钥覆盖**：内置密钥已加密存储（源码中只有 Base64 密文分片与分散的掩码材料，明文不入库），默认无需配置即可使用官方 API。若需更换密钥而不重新编译，可设置环境变量 `HYCL_CURSEFORGE_API_KEY`（优先级最高），或在配置文件中填写 `curseForgeApiKey`。日志中只会输出掩码（如 `$2a$10$***`），不会记录明文。

官方 API 返回 403 时按响应体区分处理：**空响应体**判定为密钥无效，记录一条日志后直接回退镜像源；**非空响应体**（如边缘节点返回的 `Forbidden: API Key missing or invalid` 文本）判定为边缘节点临时拒绝，退避 400 ms 后重试一次，仍失败再回退镜像。

## 来源与许可

- 业务行为（下载、版本安装、实例管理、账号、Java 探测、启动参数）参考 **PCL-CE-dev**，该项目使用 Apache License 2.0；本仓库的 [LICENSE](LICENSE) 保留同一许可文本。
- 界面组织与视觉风格参考 **PCL.Neo-main**。该仓库使用自定义的 “All Rights Reserved” 许可（并非 OSI 认可的开源许可），其中“重度使用”条款要求基于它的作品名称以 `Plain Craft Launcher (PCL)` 开头。本项目品牌为 Hyplay Craft Launcher / HYCL，无法满足该条款，因此**没有复制其源码、XAML、资源或图标**，仅借鉴布局与配色思路，界面全部以 Avalonia 原生方式独立实现。
- 本项目不使用 WPF / `System.Windows.*`，不使用 WebView，`Hycl.Launcher.Core` 不依赖任何 UI 框架。
