using System;
using Godot;
using HarmonyLib;

namespace StandardSeedHost;

/// <summary>
/// 实现侧公共入口逻辑。版本目录下的 <c>ModuleInit</c> 只负责声明自己是哪一份实现，
/// 真正的工作都在这里，两个版本共用同一份代码。
///
/// 版本分发机制下，游戏加载的是根目录的 <c>StandardSeedHost.dll</c>
/// （ModVersionLoader 启动器），启动器按当前游戏版本挑中本实现 DLL 后，
/// 反射调用版本目录里 <c>ModuleInit.Initialize()</c>，再由这里统一装配补丁。
///
/// 因此**不需要**再挂 <c>ModManager.Initialize</c> 后置补丁 + 两帧延迟那套启动兜底：
/// 启动器在游戏加载 mod 的同一时机调用本入口，时序与原先的 [ModInitializer] 一致。
/// </summary>
internal static class ModEntry
{
    internal const string ModId = "StandardSeedHost";
    internal const string LogTag = "[StandardSeedHost]";
    private const string HarmonyId = "com.jianbao233.standardseedhost";

    private static bool _initialized;

    internal static void Initialize(string compat)
    {
        if (_initialized) return;
        _initialized = true;

        try
        {
            GD.Print($"{LogTag} ModuleInit.Initialize() called (impl {compat})");
            GD.Print($"{LogTag} version bundle: {SelectedVersionDir()} selected by loader");

            var harmony = new Harmony(HarmonyId);
            harmony.PatchAll(typeof(ModEntry).Assembly);
            GD.Print($"{LogTag} Harmony patches applied. Standard room seed input enabled.");
        }
        catch (Exception ex)
        {
            GD.PushError($"{LogTag} initialization failed: {ex}");
        }
    }

    /// <summary>
    /// 启动器选中的版本目录名（由 ModVersionLoader 通过环境变量传入）。
    /// 启动器自身写游戏日志依赖 Godot 原生绑定，故由实现侧记录这一行。
    /// </summary>
    private static string SelectedVersionDir()
    {
        try
        {
            // 全限定：Godot.Environment 与 System.Environment 同名，`using Godot;` 下会歧义。
            return System.Environment.GetEnvironmentVariable("AMS_LOADER_SELECTED_VERSION") ?? "<direct load>";
        }
        catch
        {
            return "<unknown>";
        }
    }
}
