using System.Runtime.InteropServices;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using OmenTools.Interop.Game.Lumina;
using OmenTools.Interop.Game.Models;

namespace DailyRoutines.ModulesPublic;

public unsafe class NoRenderWhenBackground : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("NoRenderWhenBackgroundTitle"),
        Description = Lang.Get("NoRenderWhenBackgroundDescription"),
        Category    = ModuleCategory.System,
        Author      = ["Siren"]
    };
    
    private static readonly CompSig DeviceDX11PostTickSig = new
    (
        "48 89 5C 24 ?? 48 89 6C 24 ?? 48 89 74 24 ?? 57 41 54 41 55 41 56 41 57 B8 ?? ?? ?? ?? E8 ?? ?? ?? ?? 48 2B E0 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 84 24 ?? ?? ?? ?? 8B 15"
    );
    private delegate void                              DeviceDX11PostTickDelegate(nint instance);
    private          Hook<DeviceDX11PostTickDelegate>? DeviceDX11PostTickHook;

    private static readonly CompSig NamePlateDrawSig = new
    (
        "0F B7 81 ?? ?? ?? ?? 81 A1 ?? ?? ?? ?? ?? ?? ?? ?? 81 A1 ?? ?? ?? ?? ?? ?? ?? ?? 66 C1 E0 06 0F B7 D0 66 89 91 ?? ?? ?? ?? C1 E2 0D 09 91 ?? ?? ?? ?? 09 91 ?? ?? ?? ?? E9 ?? ?? ?? ?? CC CC CC CC CC CC CC CC CC CC CC CC CC CC CC CC CC CC CC 33 C0"
    );
    private delegate void                         NamePlateDrawDelegate(AtkUnitBase* addon);
    private          Hook<NamePlateDrawDelegate>? NamePlateDrawHook;

    private Config config = null!;

    private long nextRenderTick;
    private bool isOnNoRender;

    protected override void Init()
    {
        config = Config.Load(this) ?? new();

        DeviceDX11PostTickHook ??= DeviceDX11PostTickSig.GetHook<DeviceDX11PostTickDelegate>(DeviceDX11PostTickDetour);
        DeviceDX11PostTickHook.Enable();

        NamePlateDrawHook ??= NamePlateDrawSig.GetHook<NamePlateDrawDelegate>(NamePlateDrawDetour);
        NamePlateDrawHook.Enable();
    }
    
    protected override void Uninit() =>
        isOnNoRender = false;

    protected override void ConfigUI()
    {
        if (ImGui.Checkbox(Lang.Get("NoRenderWhenBackground-OnlyProhibitedInIconic", LuminaWrapper.GetAddonText(4024)), ref config.OnlyProhibitedInIconic))
            config.Save(this);
    }
    
    private void DeviceDX11PostTickDetour(nint instance)
    {
        var framework = Framework.Instance();

        if (framework == null || !DService.Instance().ClientState.IsLoggedIn)
        {
            isOnNoRender = false;
            DeviceDX11PostTickHook.Original(instance);
            return;
        }

        // 每过 5 秒必定渲染一帧, 防止堆积过多
        var currentTick = Environment.TickCount64;

        if (nextRenderTick - currentTick < 0)
        {
            nextRenderTick = currentTick + 5_000;
            DeviceDX11PostTickHook.Original(instance);
            return;
        }

        var condition0 = config.OnlyProhibitedInIconic  && IsIconic(framework->GameWindow->WindowHandle);
        var condition1 = !config.OnlyProhibitedInIconic && framework->WindowInactive;

        if (condition0 || condition1)
        {
            isOnNoRender = true;
            // 防止限帧失效
            if (UIModule.Instance()->ShouldLimitFps())
                Thread.Sleep(50);
            return;
        }

        isOnNoRender = false;
        DeviceDX11PostTickHook.Original(instance);
    }

    private void NamePlateDrawDetour(AtkUnitBase* addon)
    {
        if (isOnNoRender) return;
        NamePlateDrawHook.Original(addon);
    }

    [DllImport("user32.dll")]
    private static extern bool IsIconic(nint hWnd);

    private class Config : ModuleConfig
    {
        public bool OnlyProhibitedInIconic;
    }
}
