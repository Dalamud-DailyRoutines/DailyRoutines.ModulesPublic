using System.Runtime.InteropServices;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using Dalamud.Hooking;
using OmenTools.Interop.Game.Models;

namespace DailyRoutines.ModulesPublic;

public class PFPageSizeCustomize : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("PFPageSizeCustomizeTitle"),
        Description = Lang.Get("PFPageSizeCustomizeDescription"),
        Category    = ModuleCategory.Recruitment,
        Author      = ["逆光"]
    };

    public override ModulePermission Permission { get; } = new() { AllDefaultEnabled = true };
    
    private static readonly CompSig PartyFinderDisplayAmountSig =
        new("48 89 5C 24 ?? 55 56 57 48 ?? ?? ?? ?? ?? ?? ?? 48 ?? ?? ?? ?? ?? ?? 48 ?? ?? ?? ?? ?? ?? 48 ?? ?? 48 89 85 ?? ?? ?? ?? 48 ?? ?? 0F");
    private delegate byte                                    PartyFinderDisplayAmountDelegate(nint a1, int a2);
    private          Hook<PartyFinderDisplayAmountDelegate>? PartyFinderDisplayAmountHook;

    private Config config = null!;

    protected override void Init()
    {
        config = Config.Load(this) ?? new();

        PartyFinderDisplayAmountHook ??= PartyFinderDisplayAmountSig.GetHook<PartyFinderDisplayAmountDelegate>(PartyFinderDisplayAmountDetour);
        PartyFinderDisplayAmountHook.Enable();
    }

    protected override void ConfigUI()
    {
        ImGui.SetNextItemWidth(100f * GlobalUIScale);
        if (ImGui.InputShort(Lang.Get("PFPageSizeCustomize-DisplayAmount"), ref config.PageSize, 1, 10))
            config.PageSize = Math.Clamp(config.PageSize, (short)1, (short)100);
        if (ImGui.IsItemDeactivatedAfterEdit())
            config.Save(this);
    }

    private byte PartyFinderDisplayAmountDetour(nint a1, int a2)
    {
        Marshal.WriteInt16(a1 + 1128, config.PageSize);
        return PartyFinderDisplayAmountHook.Original(a1, a2);
    }
    
    private class Config : ModuleConfig
    {
        public short PageSize = 100;
    }
}
