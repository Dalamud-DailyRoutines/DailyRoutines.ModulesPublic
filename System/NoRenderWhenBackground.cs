using System.Runtime.InteropServices;
using System.Threading;
using DailyRoutines.Abstracts;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace DailyRoutines.ModulesPublic;

public unsafe class NoRenderWhenBackground : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("NoRenderWhenBackgroundTitle"),
        Description = GetLoc("NoRenderWhenBackgroundDescription"),
        Category    = ModuleCategories.System,
        Author      = ["Siren"]
    };

    private static readonly CompSig                           DeviceDX11PostTickSig = new("48 89 5C 24 ?? 48 89 6C 24 ?? 48 89 74 24 ?? 57 41 54 41 55 41 56 41 57 B8 ?? ?? ?? ?? E8 ?? ?? ?? ?? 48 2B E0 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 84 24 ?? ?? ?? ?? 8B 15");
    private delegate        void                              DeviceDX11PostTickDelegate(nint instance);
    private static          Hook<DeviceDX11PostTickDelegate>? DeviceDX11PostTickHook;

    private static readonly CompSig                      NamePlateDrawSig = new("0F B7 81 ?? ?? ?? ?? 81 A1 ?? ?? ?? ?? ?? ?? ?? ?? 81 A1 ?? ?? ?? ?? ?? ?? ?? ?? 66 C1 E0 06 0F B7 D0 66 89 91 ?? ?? ?? ?? C1 E2 0D 09 91 ?? ?? ?? ?? 09 91 ?? ?? ?? ?? E9 ?? ?? ?? ?? CC CC CC CC CC CC CC CC CC CC CC CC CC CC CC CC CC CC CC 33 C0");
    private delegate        void                         NamePlateDrawDelegate(AtkUnitBase* addon);
    private static          Hook<NamePlateDrawDelegate>? NamePlateDrawHook;
    
    private static Config ModuleConfig = null!;

    private static bool IsOnNoRender;

    protected override void Init()
    {
        ModuleConfig = LoadConfig<Config>() ?? new();
        
        DeviceDX11PostTickHook ??= DeviceDX11PostTickSig.GetHook<DeviceDX11PostTickDelegate>(DeviceDX11PostTickDetour);
        DeviceDX11PostTickHook.Enable();

        NamePlateDrawHook ??= NamePlateDrawSig.GetHook<NamePlateDrawDelegate>(NamePlateDrawDetour);
        NamePlateDrawHook.Enable();
    }

    protected override void ConfigUI()
    {
        if (ImGui.Checkbox(GetLoc("NoRenderWhenBackground-OnlyProhibitedInIconic", LuminaWrapper.GetAddonText(4024)), ref ModuleConfig.OnlyProhibitedInIconic))
            SaveConfig(ModuleConfig);
    }

    protected override void Uninit()
    {
        base.Uninit();

        IsOnNoRender = false;
    }

    private static void DeviceDX11PostTickDetour(nint instance)
    {
        var framework = Framework.Instance();
        if (framework == null || !DService.ClientState.IsLoggedIn)
        {
            IsOnNoRender = false;
            DeviceDX11PostTickHook.Original(instance);
            return;
        }

        // 每过 5 秒必定渲染一帧, 防止堆积过多
        if (Throttler.Throttle("NoRenderWhenBackground-Detour", 5_000))
        {
            DeviceDX11PostTickHook.Original(instance);
            return;
        }

        var condition0 = ModuleConfig.OnlyProhibitedInIconic  && IsIconic(framework->GameWindow->WindowHandle);
        var condition1 = !ModuleConfig.OnlyProhibitedInIconic && framework->WindowInactive;
        if (condition0 || condition1)
        {
            IsOnNoRender = true;
            // 防止限帧失效
            if (UIModule.Instance()->ShouldLimitFps()) 
                Thread.Sleep(50);
            return;
        }

        IsOnNoRender = false;
        DeviceDX11PostTickHook.Original(instance);
    }

    private static void NamePlateDrawDetour(AtkUnitBase* addon)
    {
        if (IsOnNoRender) return;
        NamePlateDrawHook.Original(addon);
    }
    
    [DllImport("user32.dll")]
    private static extern bool IsIconic(nint hWnd);

    private class Config : ModuleConfiguration
    {
        public bool OnlyProhibitedInIconic;
    }
}
