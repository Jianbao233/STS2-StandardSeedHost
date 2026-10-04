namespace StandardSeedHost;

/// <summary>
/// 游戏 v0.111.0（public-beta 分支）实现入口。
///
/// 注意：本类**不要**标注 <c>[ModInitializer]</c>。版本分发机制下，游戏加载的是
/// 根目录的 <c>StandardSeedHost.dll</c>（ModVersionLoader 启动器），启动器按当前
/// 游戏版本挑中本实现 DLL 后，反射调用这里的 <see cref="Initialize"/>。
///
/// 因此本方法必须是 <c>public static void</c> 且无参数 —— 启动器按此签名查找。
/// </summary>
public static class ModuleInit
{
    public static void Initialize() => ModEntry.Initialize("v0.111.0");
}
