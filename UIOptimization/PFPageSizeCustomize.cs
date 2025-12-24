using System;
using System.Runtime.InteropServices;
using DailyRoutines.Abstracts;
using Dalamud.Hooking;

namespace DailyRoutines.ModulesPublic;

public class PFPageSizeCustomize : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("PFPageSizeCustomizeTitle"),
        Description = GetLoc("PFPageSizeCustomizeDescription"),
        Category    = ModuleCategories.UIOptimization,
        Author      = ["逆光"]
    };
    
    public override ModulePermission Permission { get; } = new() { AllDefaultEnabled = true };

    private static readonly CompSig PartyFinderDisplayAmountSig =
        new("48 89 5C 24 ?? 55 56 57 48 ?? ?? ?? ?? ?? ?? ?? 48 ?? ?? ?? ?? ?? ?? 48 ?? ?? ?? ?? ?? ?? 48 ?? ?? 48 89 85 ?? ?? ?? ?? 48 ?? ?? 0F");
    private delegate byte                                    PartyFinderDisplayAmountDelegate(nint a1, int a2);
    private static   Hook<PartyFinderDisplayAmountDelegate>? PartyFinderDisplayAmountHook;

    private static Config ModuleConfig = null!;

    protected override void Init()
    {
        ModuleConfig = LoadConfig<Config>() ?? new();

        PartyFinderDisplayAmountHook ??= PartyFinderDisplayAmountSig.GetHook<PartyFinderDisplayAmountDelegate>(PartyFinderDisplayAmountDetour);
        PartyFinderDisplayAmountHook.Enable();
    }

    protected override void ConfigUI()
    {
        ImGui.SetNextItemWidth(100f * GlobalFontScale);
        if (ImGui.InputShort(GetLoc("PFPageSizeCustomize-DisplayAmount"), ref ModuleConfig.PageSize, 1, 10))
            ModuleConfig.PageSize = Math.Clamp(ModuleConfig.PageSize, (short)1, (short)100);
        if (ImGui.IsItemDeactivatedAfterEdit())
            SaveConfig(ModuleConfig);
    }

    private static byte PartyFinderDisplayAmountDetour(nint a1, int a2)
    {
        Marshal.WriteInt16(a1 + 1128, ModuleConfig.PageSize);
        return PartyFinderDisplayAmountHook.Original(a1, a2);
    }

    private class Config : ModuleConfiguration
    {
        public short PageSize = 100;
    }
}
