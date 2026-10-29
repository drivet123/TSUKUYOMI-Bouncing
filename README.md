# TSUKUYOMI Q 弹 Mod（BounceV5）

让《KAZUMA KANEKO'S TSUKUYOMI》中的立绘、敌人、卡牌和大型 UI 图片随节拍 Q 弹跳动，并提供 DJ 音乐、击杀音效、月相演出与游戏内设置界面。

**当前发布版：v5.2.0。**

## 功能

- 按 DJ 的 BPM 或自定义频率弹跳，支持压扁、拉伸、弹高和底部锚定。
- 自动发现常见角色、敌人、卡牌与剧情图片，并排除地图、背景和遮罩。
- DJ 音乐循环播放，可替换游戏 BGM，并跟随游戏 BGM 音量设置。
- 击杀时播放音效并显示月相图片，支持固定或自动月相。
- F9 设置面板、F10 素材排除编辑器，以及游戏原生选项页集成。

## 环境要求

- Windows x64 版《KAZUMA KANEKO'S TSUKUYOMI》（Steam appid `4677110`）。
- 已安装 **BepInEx 6 IL2CPP x64**；已知可用版本为 `6.0.0-be.788`。
- 本 Mod 使用 Unity 2022.3 IL2CPP 接口；游戏大版本更新后请先确认兼容性。

## 安装

1. 将 BepInEx 6 IL2CPP x64 安装到游戏根目录，并至少启动游戏一次。
2. 如果装过旧版，请删除 `BepInEx/plugins/BounceV3`、`BepInEx/plugins/BouncingMod` 和旧 `BounceV5`，不要同时保留多个版本。
3. 将发布 ZIP 中的 `BepInEx` 文件夹解压到游戏根目录，最终应存在：
   `BepInEx/plugins/BounceV5/BounceV5.dll`。
4. 启动游戏。配置文件会在首次运行时自动生成。

从旧 V5 升级时可直接替换 `BepInEx/plugins/BounceV5`。现有配置会保留；三项旧默认素材路径会自动迁移到插件目录，自定义路径不会被覆盖。

## 按键与设置

- **F8**：总开关，同时控制弹跳和 DJ。
- **F9**：打开/关闭设置面板；调整弹跳、目标过滤、DJ 和击杀演出。
- **F10**：打开/关闭原生素材浏览器，用于选择并排除不希望弹跳的素材。
- 也可在游戏的选项菜单中找到 BounceV5 设置页。

配置文件：`BepInEx/config/com.bouncev2.tsukuyomi.cfg`。关闭 UI 后会保存设置；手动编辑配置前请先退出游戏。

## 素材与自定义

默认素材位于：

```text
BepInEx/plugins/BounceV5/assets/DJ.mp3
BepInEx/plugins/BounceV5/assets/the-rock-sound-effect.mp3
BepInEx/plugins/BounceV5/assets/image/十六夜月.png
BepInEx/plugins/BounceV5/assets/image/新月.png
BepInEx/plugins/BounceV5/assets/image/满月.png
BepInEx/plugins/BounceV5/assets/image/半月.png
```

可直接替换同名文件，或在配置中修改 `MusicFilePath`、`KillFxImagePath`、`KillFxSoundPath`。音频和纹理会缓存，**替换素材或修改路径后必须重启游戏**。

`KillFxImagePath` 默认指向不实际存在的 `assets/image/killfx.png`，这是正常的：插件使用其所在目录寻找固定文件名的四张月相图。

## 自动月相校准

`KillFxPhase=auto` 会优先使用 `KillFxCharaMap` 的形态映射，再尝试按名称判断；未校准时可能随机回退，并不保证所有形态都能自动识别。

需要精确校准时，游玩对应形态并查看日志中的 `kill form id=N unmapped`，再在配置中填写，例如：

```text
KillFxCharaMap=3=满月;7=新月
```

可用月相值为 `十六夜`、`新月`、`满月`、`半月`。修改后重启游戏，再逐形态验证。

## 卸载与排错

- 卸载 Mod：删除 `BepInEx/plugins/BounceV5`；可选删除上述配置文件。不要因此删除整个 BepInEx。
- 日志：`BepInEx/LogOutput.log`。正常加载应出现 `BounceV5 loaded.`。
- 没有弹跳或声音：确认 F8 已开启、DJ 与击杀演出已启用、素材存在，且没有同时安装旧版插件。
- 首次安装 BepInEx 时，生成 IL2CPP interop 程序集可能需要数分钟。

## 从源码构建

需要 .NET 6 SDK，以及本机游戏目录中的 BepInEx 6 和 IL2CPP interop DLL。这些游戏/加载器文件仅用于编译引用，不包含在源码仓库中。

仓库可放在任意目录。将下面的游戏路径替换成自己的路径，在仓库根目录执行：

```powershell
dotnet restore .\v5\BounceMod\BounceV5.csproj
dotnet build .\v5\BounceMod\BounceV5.csproj -c Release `
  -p:GameDir="C:\Games\TSUKUYOMI" -p:DeployToGame=false
.\releases\package-v5.ps1
```

打包脚本生成 `releases/BounceV5_v5.2.0.zip`。如果仓库位于游戏根目录下的 `bouncingmod` 文件夹，可省略 `GameDir`；省略 `DeployToGame=false` 时，构建会自动部署 DLL 到游戏插件目录，此时请先关闭游戏。

## 许可与致谢

- Mod 代码使用 [MIT License](LICENSE)。
- 音频解码使用 [NAudio 2.2.1](https://github.com/naudio/NAudio)，详见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。
- 加载框架为 [BepInEx](https://github.com/BepInEx/BepInEx)，发布包不包含 BepInEx 或游戏文件。
- 开发协助：Kimi Code CLI。
