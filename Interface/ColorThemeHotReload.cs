using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.Config;
using FFXIVClientStructs.FFXIV.Component.GUI;
using OmenTools.Interop.Game.Models;

namespace DailyRoutines.ModulesPublic.Interface;

public unsafe class ColorThemeHotReload : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("ColorThemeHotReloadTitle"),
        Description = Lang.Get("ColorThemeHotReloadDescription"),
        Category    = ModuleCategory.Interface
    };

    public override ModulePermission Permission { get; } = new()
    {
        AllDefaultEnabled = true
    };

    private static readonly CompSig LoadColorThemeSig = 
        new("E8 ?? ?? ?? ?? B0 ?? C6 83 ?? ?? ?? ?? ?? 48 8B 5C 24 ?? 48 8B 74 24 ?? 48 83 C4 ?? 5F C3 80 BB");
    private delegate void         LoadColorThemeDelegate(AtkUIColorHolder* holder, byte theme);
    private          LoadColorThemeDelegate LoadColorTheme = LoadColorThemeSig.GetDelegate<LoadColorThemeDelegate>();

    private int currentTheme = -1;
    
    protected override void Init()
    {
        IGameConfig.Instance().SystemChanged += OnConfigChanged;
        IAddonLifecycle.Instance().RegisterListener(AddonEvent.PostClose, "ConfigSystem", OnAddon);
    }

    protected override void Uninit()
    {
        IGameConfig.Instance().SystemChanged -= OnConfigChanged;
        IAddonLifecycle.Instance().UnregisterListener(OnAddon);
    }

    private void OnConfigChanged
    (
        object?           sender,
        ConfigChangeEvent e
    )
    {
        if (e.Name != nameof(SystemConfigOption.ColorThemeType))
            return;
        
        currentTheme = (int)IGameConfig.Instance().System.GetUInt(nameof(SystemConfigOption.ColorThemeType));
        currentTheme = -1;
    }
    
    private void OnAddon
    (
        AddonEvent type,
        AddonArgs  args
    )
    {
        if (currentTheme is < 0 or > byte.MaxValue)
            return;

        LoadColorTheme(AtkStage.Instance()->AtkUIColorHolder, (byte)currentTheme);
    }
}
