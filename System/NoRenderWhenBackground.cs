using System.Runtime.InteropServices;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using OmenTools.Interop.Game.Lumina;
using OmenTools.Interop.Game.Models;
using OmenTools.OmenService;

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
    private delegate void                              DeviceDX11PostTickDelegate(Device* device);
    private          Hook<DeviceDX11PostTickDelegate>? DeviceDX11PostTickHook;

    private static readonly CompSig AddonNamePlateDrawSig = new("0F B7 81 ?? ?? ?? ?? 81 A1 ?? ?? ?? ?? ?? ?? ?? ?? 81 A1 ?? ?? ?? ?? ?? ?? ?? ?? 66 C1 E0 ?? 0F B7 D0 66 89 91 ?? ?? ?? ?? C1 E2 ?? 09 91 ?? ?? ?? ?? 09 91 ?? ?? ?? ?? E9 ?? ?? ?? ?? CC CC CC CC CC CC CC CC CC CC CC CC CC CC CC CC CC CC CC 33 C0");
    private delegate void                              AddonNamePlateDrawDelegate(AddonNamePlate* addon);
    private          Hook<AddonNamePlateDrawDelegate>? AddonNamePlateDrawHook;
    
    private Config config = null!;

    private long nextRenderTick;
    private bool isNoRender;

    protected override void Init()
    {
        config = Config.Load(this) ?? new();

        AddonNamePlateDrawHook = AddonNamePlateDrawSig.GetHook<AddonNamePlateDrawDelegate>(AddonNamePlateDrawDetour);
        AddonNamePlateDrawHook.Enable();
        
        DeviceDX11PostTickHook = DeviceDX11PostTickSig.GetHook<DeviceDX11PostTickDelegate>(DeviceDX11PostTickDetour);
        DeviceDX11PostTickHook.Enable();
    }

    protected override void ConfigUI()
    {
        if (ImGui.Checkbox(Lang.Get("NoRenderWhenBackground-OnlyProhibitedInIconic", LuminaWrapper.GetAddonText(4024)), ref config.OnlyProhibitedInIconic))
            config.Save(this);
    }

    private void DeviceDX11PostTickDetour(Device* device)
    {
        if (GameState.IsForeground || !GameState.IsLoggedIn)
        {
            isNoRender = false;
            DeviceDX11PostTickHook.Original(device);
            return;
        }

        if (config.OnlyProhibitedInIconic)
        {
            if (!IsIconic(Framework.Instance()->GameWindow->WindowHandle))
            {
                isNoRender = false;
                DeviceDX11PostTickHook.Original(device);
                return;
            }
        }

        // 每过 5 秒必定渲染一帧, 防止渲染管线堆积
        var currentTick = Environment.TickCount64;
        if (currentTick - nextRenderTick > 0)
        {
            nextRenderTick = currentTick + 5_000;
            isNoRender     = false;
            
            DeviceDX11PostTickHook.Original(device);
            return;
        }
        
        isNoRender = true;

        if (UIModule.Instance()->ShouldLimitFps())
            Thread.Sleep(50);
    }
    
    private void AddonNamePlateDrawDetour(AddonNamePlate* addon)
    {
        if (isNoRender)
            return;

        AddonNamePlateDrawHook.Original(addon);
    }

    [DllImport("user32.dll")]
    private static extern bool IsIconic(nint hWnd);

    private class Config : ModuleConfig
    {
        public bool OnlyProhibitedInIconic;
    }
}
