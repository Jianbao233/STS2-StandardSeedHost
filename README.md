# StandardSeedHost / 联机指定种子开局

> **状态**：独立 Git 仓库 [`Jianbao233/STS2-StandardSeedHost`](https://github.com/Jianbao233/STS2-StandardSeedHost)（2026-10-04 自 `STS2_mod` 主仓拆分）｜v0.2.0｜已可用（2026-08-17 首测通过）｜**未发布 / 未上传工坊**
> **包体形态**：版本分发 —— `ModVersionLoader` 启动器 + `bin/g<游戏版本>/` 各一份实现，一份包体同时支持正式版与 public-beta

在 STS2 **标准模式（Standard）多人开房**与**单机开局**的选人界面加入一个种子输入框（视觉复刻自定义模式种子框），房主可指定本局种子。

## 功能

- 房主 / 单机：输入种子后开局即用该种子——自动经过游戏原生 `SeedHelper.CanonicalizeSeed`（大写、O→0、I→1、去空白）；留空 = 随机种子。
- 客机：只读显示房主当前种子，不可编辑；房主修改实时同步（原生 `LobbySeedChangedMessage`）。
- 种子最终由房主经 `LobbyBeginRunMessage` 下发给全队，**无需全员安装本 mod**（客机不装也能正常游玩）。

## 包体结构与版本分发

工坊条目只有一份内容，所有 Steam 分支拿到的字节完全相同：

```
StandardSeedHost/
├── mod_manifest.json                    # 一份版本号 + min_game_version = 最低支持版本
├── StandardSeedHost.dll                 # ModVersionLoader 启动器（游戏只加载这一个）
└── bin/
    ├── g0.107.1/StandardSeedHost.Impl.dll
    └── g0.111.0/StandardSeedHost.Impl.dll
```

游戏只加载 `<mod.path>/<manifest.id>.dll`（`ModManager.TryLoadMod`），`bin/` 下的实现不会被误加载，由启动器读游戏根目录 `release_info.json` 挑选后加载。

挑选规则（启动器内置）：精确匹配游戏版本 → 否则取不高于游戏版本的**最大**版本 → 否则 `bin/latest` → 都不成立则显式报错。

**为什么必须分两份编译**：实现 DLL 静态引用了 `sts2.dll`，必须针对各版本各自的 `sts2.dll` 编译，运行时才能绑定到当前加载的游戏装配件。本 mod 的补丁代码两个版本完全一致（共用 `src/`），版本目录只放一个入口文件。

**SDK 与版本目录是两件事**：

| 版本目录 | 编译用 SDK | 说明 |
|---|---|---|
| `g0.107.1` | `tools/sts2_sdk_by_version/v0.107.1` | 正式版分支（`sts2` 装配件 0.1.0.0） |
| `g0.111.0` | `tools/sts2_sdk_by_version/v0.111.0+build24724944` | public-beta。**Steam 以同版本号重发过二进制**，当前安装的是 build24724944（9,757,184 B，装配件 0.1.0.0）；旧的 `v0.111.0` 快照（10,655,744 B，装配件 1.0.0.0）不是同一份，不可混用 |

`min_game_version` 由 `build.ps1` 自动写为所有实现里的最低游戏版本（当前 `0.107.1`）——写高了会让老分支被游戏自身的版本检查整包拒绝加载。

启动器本体是共享工具（`D:\A-Developing\main\sts2\tools\ModVersionLoader`），不属于本项目；本项目的 `build.ps1` 用 `/p:LoaderAssemblyName=StandardSeedHost` 为它产出本项目专属的启动器 DLL。

## 原理（Harmony 三补丁，仅改 NCharacterSelectScreen）

| 补丁 | 作用 |
|---|---|
| `_Ready` Postfix | 代码动态构建种子框（HBoxContainer + Label + NMegaLineEdit），复用游戏字体 / 本地化键（`CUSTOM_RUN_SCREEN.SEED_LABEL` / `SEED_RANDOM_PLACEHOLDER`）；`ConditionalWeakTable` 防重复构建 |
| `SeedChanged` Prefix | 替换标准房原生 `NotImplementedException`（不改必崩）；**主机/单机不回写文本**（避免光标归零 bug），客机刷新只读显示 |
| `OnSubmenuOpened` Postfix | 刷新种子显示 + 按主机/客机锁定 `Editable`；把种子行定位到屏幕左下角 |

关键实现事实（供后续维护参考）：

- **复用依据**：自定义房种子框是 `custom_run_screen.tscn` 内联节点（`SeedContainer` → `SeedLabel` + `SeedInput`），不是独立场景；mod 不能改游戏打包 tscn，因此用代码动态构建同款节点。
- **光标 bug 根因**：Godot `LineEdit.set_text()` 无条件 `caret_column = 0`（`line_edit.cpp`）。主机每次按键回写文本会把光标拉回第一位，故主机侧不回写。
- **定位 bug 根因**：`Control.position/size` 设值器会按锚点换算 offsets（`control.cpp _compute_offsets`），锚点非默认时坐标会被算错（如 BottomLeft + `Position=(24,-64)` → 出屏）。定位使用默认锚点 + 视口绝对坐标（`PositionRow`）。
- **权限**：`StartRunLobby.SetSeed()` 仅 Host / Singleplayer 可调（Client 调用会 throw），所以客机必须锁输入。
- **不再需要启动兜底**：版本分发下由启动器在游戏加载 mod 的同一时机反射调用 `ModuleInit.Initialize()`，时序与原 `[ModInitializer]` 一致，故原先照抄的 `ModManager.Initialize` 后置补丁 + 两帧延迟已删除。

## 安装 / 构建

```
cd D:/A-Developing/main/sts2/STS2_mod/StandardSeedHost
powershell -ExecutionPolicy Bypass -File build.ps1 -StageWorkshop
```

`build.ps1` 依次完成：读 csproj 版本 → 逐个 `GameCompat` 编译实现（各用对应 SDK）→ 编译启动器 → 写 manifest（含 `min_game_version`）→ 包体自检（根目录只允许有启动器一个 dll）→ 部署到 `F:/Steam/steamapps/common/Slay the Spire 2/mods/StandardSeedHost/` → 快照 `torelease/` → 同步工坊 workspace `content/`。

参数：`-NoLocalDeploy` 跳过部署；`-Config Debug` 换 Debug 构建；`-StageWorkshop` 同步工坊 content。

依赖：.NET 9 SDK、Godot 4.5.1 Mono（`Godot.NET.Sdk/4.5.1`）；编译期按 `Sts2DataDir` 引用该版本游戏目录下的 `sts2.dll` / `0Harmony.dll` / `GodotSharp.dll`。

## 联机说明

- 仅房主需要安装；客机可选（装了会多一个只读种子显示）。
- manifest `affects_gameplay=false`：不改协议、不改变原版可表达的状态（自定义房本来就能指定种子），无 desync 风险。
- 版本分发不改变联机比对口径：`mod_manifest.json` 的 `version` 在所有版本目录之间是同一个值（联机比对看的是它）。

## 测试与验证记录

**2026-08-17 首轮实机（v0.1.0，单实现包体）**

1. **manifest JSON 损坏**：description 换行被写坏成真实 0x0A 导致解析失败、mod 未被识别 → 用 `JSON.stringify` 重建，三处（源码/部署/快照）修复。
2. **光标永远在第一位**：主机回写 `Input.Text` 触发 `set_text` 光标归零 → 主机/单机跳过回写。
3. **种子行消失**：`SetAnchorsPreset(BottomLeft)` + `Position` 被锚点换算推出屏幕 → 改用默认锚点 + 视口绝对坐标。
4. **位置**：左下角（距左 24px、距底 64px、440×52），避开人物介绍面板与返回按钮。

**2026-10-04 版本分发改造（v0.2.0）**

5. **两个实现均编译通过（0 错误 0 警告）** —— 这同时证明共用补丁代码在 v0.107.1 的 `sts2.dll` 上 API 完全可用（`NCharacterSelectScreen` 的 `_Ready`/`SeedChanged`/`OnSubmenuOpened`、`StartRunLobby.SetSeed`、`NMegaLineEdit`、`MegaLabel`、两个本地化键在两个版本都存在）。
6. **启动器挑选逻辑实测**（`ModVersionLoader.Tests` harness，dry-run）：

   | 模拟游戏版本 | 选中实现 |
   |---|---|
   | 0.111.0 | `g0.111.0` |
   | 0.107.1 | `g0.107.1` |
   | 0.110.1（无精确匹配） | `g0.107.1`（回退到不高于游戏版本的最大者） |

   三次均成功定位入口 `StandardSeedHost.ModuleInit.Initialize()`。启动器逻辑自检 19/19 通过。
7. **`Environment` 歧义**：`using Godot;` 下 `Environment` 在 `Godot.Environment` 与 `System.Environment` 之间歧义（CS0104）→ 全限定 `System.Environment`。

**尚未做**：v0.2.0 双分支实机跑测（启动器在真实游戏进程内的日志、正式版分支下的种子框行为）。

## 已知限制

- 读档多人房（MP_LOAD）不支持修改种子（种子固化在存档 RNG 中，改种子会破坏全队同步）。
- 自定义房 / 每日房种子行为由游戏原生逻辑负责，本 mod 不干预。
- 客机加入瞬间显示为空（占位"随机"）属正常：标准房加入时原生同步消息不带种子，房主修改后即实时显示。
- 游戏版本低于 `0.107.1` 时不加载（`min_game_version` 门槛），启动器也会因无可用实现而显式报错。

## 目录结构

```
StandardSeedHost/
├── project.godot                  # Godot 4.5 C# 项目配置
├── StandardSeedHost.csproj        # net9.0 + Lib.Harmony 2.4.2；产出 StandardSeedHost.Impl.dll
├── mod_manifest.json              # mod 清单（作者 @Bilibili我叫煎包）
├── build.ps1                      # 多版本实现 + 启动器 + manifest + 部署 + 工坊同步
├── README.md
├── art/
│   ├── cover-flat-icon.png        # 工坊封面母版（Impact 英文主标题 + 42px 中文副标题）
│   ├── cover-C-zh42.png           # 封面备选：中文副标题 42px（= 当前母版）
│   ├── cover-C-zh52.png           # 封面备选：中文副标题 52px（中文更抢眼）
│   └── image.png                  # 工坊上传用（≤1 MB）
├── src/
│   ├── ModEntry.cs                      # 实现侧公共入口（装配 Harmony 补丁）
│   ├── Patches/
│   │   └── CharacterSelectSeedPatch.cs  # 功能核心（三补丁 + 定位）
│   └── versions/
│       ├── v107/ModuleInit.cs           # v0.107.x 入口（启动器反射调用）
│       └── v111/ModuleInit.cs           # v0.111.0 入口
├── build/mods/StandardSeedHost/   # 组装出的包体（gitignore）
├── torelease/StandardSeedHost/    # 发布快照（gitignore，不入库）
└── .godot/                        # 构建产物（gitignore，不入库）
```
