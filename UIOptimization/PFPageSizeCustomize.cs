using DailyRoutines.Abstracts;
using Dalamud.Hooking;
using System;
using System.Runtime.InteropServices;

namespace DailyRoutines.Modules;

public class PFPageSizeCustomize : DailyModuleBase
{
    public override ModuleInfo Info => new()
    {
        Title = GetLoc("PFPageSizeCustomizeTitle"),
        Description = GetLoc("PFPageSizeCustomizeDescription"),
        Category = ModuleCategories.UIOptimization,
        Author = ["逆光"]
    };

    private static readonly CompSig PartyFinderDisplayAmountSig = new("48 89 5C 24 ?? 55 56 57 48 ?? ?? ?? ?? ?? ?? ?? 48 ?? ?? ?? ?? ?? ?? 48 ?? ?? ?? ?? ?? ?? 48 ?? ?? 48 89 85 ?? ?? ?? ?? 48 ?? ?? 0F");
    private delegate byte PartyFinderDisplayAmountDelegate(nint a1, int a2);
    private static Hook<PartyFinderDisplayAmountDelegate>? PartyFinderDisplayAmountHook;

    private static int ConfigDisplayAmount = 100;

    public override void Init()
    {
        AddConfig("DisplayAmount", 100);
        ConfigDisplayAmount = GetConfig<int>("DisplayAmount");

        PartyFinderDisplayAmountHook ??= DService.Hook.HookFromSignature<PartyFinderDisplayAmountDelegate>(
            PartyFinderDisplayAmountSig.Get(), PartyFinderDisplayAmountDetour);
        PartyFinderDisplayAmountHook.Enable();
    }

    public override void ConfigUI()
    {
        ImGui.SetNextItemWidth(100f * GlobalFontScale);
        if (ImGui.InputInt(Lang.Get("PFPageSizeCustomize-DisplayAmount"), ref ConfigDisplayAmount, 10, 10,
                           ImGuiInputTextFlags.EnterReturnsTrue))
        {
            ConfigDisplayAmount = Math.Clamp(ConfigDisplayAmount, 1, 100);
            UpdateConfig("DisplayAmount", ConfigDisplayAmount);
        }
    }

    private static byte PartyFinderDisplayAmountDetour(nint a1, int a2)
    {
        Marshal.WriteInt16(a1 + 1128, (short)ConfigDisplayAmount);
        return PartyFinderDisplayAmountHook.Original(a1, a2);
    }
}
