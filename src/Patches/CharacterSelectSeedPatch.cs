using System.Runtime.CompilerServices;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect;

namespace StandardSeedHost.Patches;

/// <summary>
/// 每个 NCharacterSelectScreen 实例对应的种子输入 UI 状态。
/// </summary>
internal sealed class SeedUiState
{
    public HBoxContainer Container = null!;
    public NMegaLineEdit Input = null!;
    /// <summary>程序化刷新 Text 时置位，防止 TextChanged → SetSeed 回环。</summary>
    public bool Syncing;
}

/// <summary>
/// 标准模式（多人房/单机）选人界面种子输入框。
///
/// 复用自定义房种子框的视觉配方（custom_run_screen.tscn 内联节点）：
/// SeedContainer(HBoxContainer) → SeedLabel(Label) + SeedInput(NMegaLineEdit)。
/// mod 无法修改游戏打包的 tscn，故在 _Ready 后置补丁中用代码动态构建同款节点，
/// 脚本/字体/文案全部使用游戏内资源：
///   - NMegaLineEdit（src/Core/Nodes/GodotExtensions/NMegaLineEdit.cs）
///   - kreon_bold_shared.tres（标签字体）
///   - LocString("main_menu_ui", "CUSTOM_RUN_SCREEN.SEED_LABEL" / "SEED_RANDOM_PLACEHOLDER")
/// </summary>
[HarmonyPatch(typeof(NCharacterSelectScreen), "_Ready")]
internal static class CharacterSelectSeedUiPatch
{
    /// <summary>按屏幕实例缓存 UI 状态（NSubmenuStack 会复用实例，防重复构建）。</summary>
    private static readonly ConditionalWeakTable<NCharacterSelectScreen, SeedUiState> States = new();

    /// <summary>种子行布局常量：屏幕左下角（默认锚点 + 视口绝对坐标，避开 InfoPanel 与 BackButton）。</summary>
    private const float SeedRowMarginLeft = 24f;
    private const float SeedRowMarginBottom = 64f;
    private const float SeedRowWidth = 440f;
    private const float SeedRowHeight = 52f;

    static void Postfix(NCharacterSelectScreen __instance)
    {
        if (States.TryGetValue(__instance, out _))
        {
            return;
        }

        HBoxContainer container = new HBoxContainer
        {
            Name = "SeedContainer",
            MouseFilter = Control.MouseFilterEnum.Stop
        };
        container.AddThemeConstantOverride("separation", 12);

        Label label = new Label();
        label.AddThemeColorOverride("font_color", new Color(0.937255f, 0.784314f, 0.317647f, 1f));
        label.AddThemeColorOverride("font_shadow_color", new Color(0f, 0f, 0f, 0.501961f));
        label.AddThemeConstantOverride("shadow_offset_x", 4);
        label.AddThemeConstantOverride("shadow_offset_y", 3);
        label.AddThemeFontOverride("font", GD.Load<FontVariation>("res://themes/kreon_bold_shared.tres"));
        label.AddThemeFontSizeOverride("font_size", 28);
        label.VerticalAlignment = VerticalAlignment.Center;
        label.Text = new LocString("main_menu_ui", "CUSTOM_RUN_SCREEN.SEED_LABEL").GetFormattedText();

        NMegaLineEdit input = new NMegaLineEdit();
        input.AddThemeFontSizeOverride("font_size", 24);
        input.CustomMinimumSize = new Vector2(320f, 0f);
        input.PlaceholderText = new LocString("main_menu_ui", "CUSTOM_RUN_SCREEN.SEED_RANDOM_PLACEHOLDER").GetFormattedText();

        SeedUiState state = new SeedUiState
        {
            Container = container,
            Input = input
        };

        input.TextChanged += text => OnSeedTextChanged(__instance, state, text);

        container.AddChild(label);
        container.AddChild(input);
        __instance.AddChild(container);

        States.Add(__instance, state);
        PositionRow(__instance, state);
    }

    /// <summary>OnSubmenuOpened 后置刷新：同步显示 + 按主机/客机锁定输入。</summary>
    internal static void RefreshUi(NCharacterSelectScreen screen)
    {
        if (!States.TryGetValue(screen, out SeedUiState? state))
        {
            return;
        }

        StartRunLobby? lobby = screen.Lobby;
        state.Syncing = true;
        state.Input.Text = lobby?.Seed ?? string.Empty;
        state.Syncing = false;

        NetGameType type = lobby?.NetService.Type ?? NetGameType.None;
        state.Input.Editable = type is NetGameType.Host or NetGameType.Singleplayer;

        // 打开界面时（尺寸已定）重新定位到左下角，防止分辨率/布局变化后跑位。
        PositionRow(screen, state);
    }

    /// <summary>
    /// 把种子行放到屏幕左下角：距左 24px，底边距底部 64px，宽 440 高 52。
    /// 使用默认锚点（左上 0,0）+ 视口绝对坐标——Godot 的 position/size 设值器会
    /// 按锚点换算 offsets（control.cpp _compute_offsets），锚点非默认时会算错位置
    /// （例如 BottomLeft + Position=(24,-64) 会得到 offset_top = -64 - H，直接出屏），
    /// 因此这里直接写绝对坐标。
    /// </summary>
    private static void PositionRow(NCharacterSelectScreen screen, SeedUiState state)
    {
        Vector2 vp = screen.GetViewportRect().Size;
        if (vp.X < 1f || vp.Y < 1f)
        {
            vp = screen.Size;
        }

        state.Container.OffsetLeft = SeedRowMarginLeft;
        state.Container.OffsetTop = vp.Y - SeedRowMarginBottom - SeedRowHeight;
        state.Container.OffsetRight = SeedRowMarginLeft + SeedRowWidth;
        state.Container.OffsetBottom = vp.Y - SeedRowMarginBottom;
    }

    /// <summary>SeedChanged 前缀刷新显示（跳过原 NotImplementedException）。</summary>
    /// <remarks>
    /// 主机/单机的输入框本身就是权威文本（用户刚敲进去的），每次按键回写 Input.Text
    /// 会被 Godot LineEdit.set_text 无条件重置光标到 0（line_edit.cpp: caret_column = 0），
    /// 造成"永远输入在第一位"。因此主机/单机侧跳过回写，只有客机（只读显示）需要刷新。
    /// </remarks>
    internal static void RefreshText(NCharacterSelectScreen screen)
    {
        if (!States.TryGetValue(screen, out SeedUiState? state))
        {
            return;
        }

        StartRunLobby? lobby = screen.Lobby;
        if (lobby == null)
        {
            return;
        }

        NetGameType type = lobby.NetService.Type;
        if (type is NetGameType.Host or NetGameType.Singleplayer)
        {
            return;
        }

        state.Syncing = true;
        state.Input.Text = lobby.Seed ?? string.Empty;
        state.Syncing = false;
    }

    private static void OnSeedTextChanged(NCharacterSelectScreen screen, SeedUiState state, string text)
    {
        if (state.Syncing)
        {
            return;
        }

        StartRunLobby? lobby = screen.Lobby;
        if (lobby == null)
        {
            return;
        }

        NetGameType type = lobby.NetService.Type;
        if (type != NetGameType.Host && type != NetGameType.Singleplayer)
        {
            return; // Client 输入框已锁定，双保险
        }

        lobby.SetSeed(string.IsNullOrEmpty(text) ? null : text);
    }
}

/// <summary>
/// 替换标准模式 SeedChanged 的 NotImplementedException：
/// 原实现见 NCharacterSelectScreen.cs:734，host 修改种子时 host 与全员都会触发崩溃。
/// </summary>
[HarmonyPatch(typeof(NCharacterSelectScreen), "SeedChanged")]
internal static class CharacterSelectSeedChangedPatch
{
    static bool Prefix(NCharacterSelectScreen __instance)
    {
        CharacterSelectSeedUiPatch.RefreshText(__instance);
        return false; // 永远跳过原方法
    }
}

/// <summary>
/// 打开选人界面时刷新种子显示并锁定/解锁输入。
/// </summary>
[HarmonyPatch(typeof(NCharacterSelectScreen), "OnSubmenuOpened")]
internal static class CharacterSelectOnOpenedPatch
{
    static void Postfix(NCharacterSelectScreen __instance)
    {
        CharacterSelectSeedUiPatch.RefreshUi(__instance);
    }
}
