# BounceV5 开发指南

## 项目概况

这是《KAZUMA KANEKO'S TSUKUYOMI》（Steam appid `4677110`，Unity 2022.3 IL2CPP x64）的 BepInEx 6 IL2CPP Mod。当前插件版本为 5.2.0，活跃源码全部位于 `v5/BounceMod/`。

| 路径 | 用途 |
| --- | --- |
| `v5/BounceMod/` | C# 源码与 `BounceV5.csproj` |
| `image/` | 四张月相 PNG 素材 |
| `DJ.mp3`、`the-rock-sound-effect.mp3` | 默认 DJ 与击杀音效 |
| `releases/package-v5.ps1` | Mod 发布 ZIP 打包脚本 |

## 构建与打包

需要 .NET 6 SDK、NAudio 2.2.1 NuGet 包，以及本机游戏目录中的 BepInEx 6 和 IL2CPP interop DLL。游戏与加载器 DLL 只用于编译引用，不要提交到仓库。

在仓库根目录执行（替换游戏路径）：

```powershell
dotnet restore .\v5\BounceMod\BounceV5.csproj
dotnet build .\v5\BounceMod\BounceV5.csproj -c Release `
  -p:GameDir="C:\Games\TSUKUYOMI" -p:DeployToGame=false
.\releases\package-v5.ps1
```

打包结果为 `releases/BounceV5_v5.2.0.zip`。若仓库位于游戏根目录下的 `bouncingmod` 文件夹，可省略 `GameDir`；默认构建会自动部署到游戏插件目录，部署前关闭游戏。

## 代码架构

- `Plugin.cs`：BepInEx 入口、IL2CPP 类型注册、全部配置及旧默认资源路径迁移。GUID 必须保持 `com.bouncev2.tsukuyomi`。
- `V5Manager.cs`：唯一 DDOL 协调器，处理 Update/OnGUI、快捷键、场景切换和子系统更新。
- `BounceDriver.cs`：每帧先撤销上一帧变换，再应用当前变换，以兼容游戏 Tween；禁用或销毁时恢复 Transform。
- `TargetSystem.cs`：目标发现、过滤和动态 Image/RawImage hooks；避免退回持续全场景扫描。
- `MusicSystem.cs`：NAudio 解码、独立 AudioSource、BGM 抑制与音量同步。
- `KillFxSystem.cs`：击杀 Harmony detour、月相图和音效；场景切换后需要重新 patch。
- `ModUi.cs`：F9 IMGUI 设置和旧素材编辑器。
- `NativeMenuSystem.cs`：原生选项页和 F10 素材浏览器。

## 修改约束

- 保持现有 C# 风格；项目禁用 nullable、允许 unsafe，使用 `LangVersion=latest`。
- 子系统刻意使用 static 共享状态和单管理器，避免第二场战斗出现状态分裂。
- 保留 IL2CPP/Harmony 在场景切换后的 hook 恢复逻辑。
- 不要反射 IL2CPP interop 类型的 Unity 消息方法，尤其 `Awake`，以避免主线程死锁。
- 新配置集中在 `Plugin.cs`，保留旧配置键和 GUID，除非明确要求迁移。
- 做最小改动，不提交 bin/obj、日志、cfg、interop、游戏文件或本机路径配置。

## 资源与验证

默认资源位于 `BepInEx/plugins/BounceV5/assets/`，由 `BepInEx.Paths.PluginPath` 定位。`assets/image/killfx.png` 不存在是正常的，插件使用其父目录中的固定月相文件名。音频和纹理有缓存，替换后重启游戏验证。

至少执行 Release 构建和真实游戏冒烟回归：确认 `BounceV5 loaded.`、hooks 安装成功、DJ/BGM 正常，并验证 F8/F9/F10、原生选项页、击杀图/音效和连续两场战斗的场景切换。

发布前检查 ZIP 仅包含白名单 DLL、NAudio、素材和文档，不包含 cfg、日志、PDB、游戏 runtime/interop 或逆向资料。版本变更时同步更新 `Plugin.cs`、csproj、README、CHANGELOG 和打包脚本默认版本。
