using Dalamud.Hooking;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
using OmenTools.Interop.Game.Lumina;
using OmenTools.Interop.Game.Models;
using RenderManager = FFXIVClientStructs.FFXIV.Client.Graphics.Render.Manager;
using WindowNative = OmenTools.Interop.Windows.WindowNative;

namespace DailyRoutines.ModulesPublic;

internal unsafe class NoRenderWhenBackground : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("NoRenderWhenBackgroundTitle"),
        Description = Lang.Get("NoRenderWhenBackgroundDescription"),
        Category    = ModuleCategory.System,
        Author      = ["Siren"]
    };

    public override string ModuleName => nameof(NoRenderWhenBackground);

    private static readonly CompSig WindowProcSig =
        new("40 55 53 56 57 41 54 41 56 48 8D 6C 24 ?? 48 81 EC ?? ?? ?? ?? 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 45 E0");
    private delegate nint                      WindowProcDelegate(nint window, uint message, nuint wParam, nint lParam);
    private          Hook<WindowProcDelegate>? WindowProcHook;

    private Config config = null!;

    protected override void Init()
    {
        config = Config.Load(this) ?? new();

        WindowProcHook = WindowProcSig.GetHook<WindowProcDelegate>(WindowProcDetour);
        WindowProcHook.Enable();

        UpdateRendering();
    }

    protected override void Uninit()
    {
        WindowProcHook?.Disable();

        var manager = RenderManager.Instance();
        if (manager != null)
            manager->Is3DRenderingDisabled = false;
    }

    protected override void ConfigUI()
    {
        if (ImGui.Checkbox
            (
                Lang.Get
                (
                    "NoRenderWhenBackground-OnlyProhibitedInIconic",
                    new Dictionary<string, object>
                    {
                        ["configName"] = LuminaWrapper.GetAddonText(4024)
                    }
                ),
                ref config.OnlyProhibitedInIconic
            ))
        {
            config.Save(this);
            UpdateRendering();
        }
    }

    private nint WindowProcDetour(nint window, uint message, nuint wParam, nint lParam)
    {
        var result = WindowProcHook.Original(window, message, wParam, lParam);

        if (window != Framework.Instance()->GameWindow->WindowHandle)
            return result;

        switch (message)
        {
            case WM_ACTIVATE when !config.OnlyProhibitedInIconic:
                UpdateRendering((ushort)wParam == 0);
                break;
            case WM_ACTIVATE_APP when !config.OnlyProhibitedInIconic:
            case WM_NCACTIVATE when !config.OnlyProhibitedInIconic:
                UpdateRendering(wParam == 0);
                break;
            case WM_SETFOCUS when !config.OnlyProhibitedInIconic:
                UpdateRendering(false);
                break;
            case WM_SIZE when config.OnlyProhibitedInIconic:
                UpdateRendering(wParam == SIZE_MINIMIZED);
                break;
        }

        return result;
    }

    private void UpdateRendering(bool? shouldDisableRendering = null)
    {
        var manager = RenderManager.Instance();
        if (manager == null)
            return;

        if (!shouldDisableRendering.HasValue)
        {
            var window = Framework.Instance()->GameWindow->WindowHandle;
            shouldDisableRendering = config.OnlyProhibitedInIconic ?
                                         WindowNative.IsIconic(window) :
                                         !WindowNative.IsForeground(window);
        }

        manager->Is3DRenderingDisabled = shouldDisableRendering.Value;
    }

    private class Config : ModuleConfig
    {
        public bool OnlyProhibitedInIconic;
    }

    #region 常量

    private const uint WM_ACTIVATE     = 0x0006;
    private const uint WM_ACTIVATE_APP = 0x001C;
    private const uint WM_NCACTIVATE   = 0x0086;
    private const uint WM_SIZE         = 0x0005;
    private const uint WM_SETFOCUS     = 0x0007;
    private const uint SIZE_MINIMIZED  = 1;

    #endregion
}
