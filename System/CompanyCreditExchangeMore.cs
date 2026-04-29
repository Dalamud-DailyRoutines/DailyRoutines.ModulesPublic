using System.Runtime.InteropServices;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Component.GUI;
using OmenTools.Interop.Game.Models;
using OmenTools.Interop.Game.Models.Packets.Upstream;
using OmenTools.OmenService;

namespace DailyRoutines.ModulesPublic;

public unsafe class CompanyCreditExchangeMore : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("CompanyCreditExchangeMoreTitle"),
        Description = Lang.Get("CompanyCreditExchangeMoreDescription"),
        Category    = ModuleCategory.System
    };

    public override ModulePermission Permission { get; } = new() { AllDefaultEnabled = true };
    
    private static readonly CompSig AddonFreeCompanyCreditShopRefreshSig = new("41 56 41 57 48 83 EC ?? 0F B6 81 ?? ?? ?? ?? 4D 8B F8");
    [return: MarshalAs(UnmanagedType.U1)]
    private delegate bool AddonFreeCompanyCreditShopRefreshDelegate(AtkUnitBase* addon, uint atkValueCount, AtkValue* atkValues);
    private          Hook<AddonFreeCompanyCreditShopRefreshDelegate> AddonFreeCompanyCreditShopRefreshHook;

    private Config config = null!;

    protected override void Init()
    {
        config = Config.Load(this) ?? new();

        AddonFreeCompanyCreditShopRefreshHook = AddonFreeCompanyCreditShopRefreshSig.GetHook<AddonFreeCompanyCreditShopRefreshDelegate>(AddonRefreshDetour);
        AddonFreeCompanyCreditShopRefreshHook.Enable();

        GamePacketManager.Instance().RegPreSendPacket(OnPreSendPacket);
    }
    
    protected override void Uninit() =>
        GamePacketManager.Instance().Unreg(OnPreSendPacket);
    
    protected override void ConfigUI()
    {
        if (ImGui.Checkbox(Lang.Get("CompanyCreditExchangeMore-OnlyActiveInWorkshop"), ref config.OnlyActiveInWorkshop))
            config.Save(this);
    }

    private bool AddonRefreshDetour(AtkUnitBase* addon, uint atkValueCount, AtkValue* atkValues)
    {
        if (addon == null) return false;

        var orig = AddonFreeCompanyCreditShopRefreshHook.Original(addon, atkValueCount, atkValues);

        if (!config.OnlyActiveInWorkshop || HousingManager.Instance()->WorkshopTerritory != null)
        {
            for (var i = 110; i < 130; i++)
            {
                if (addon->AtkValues[i].Type != AtkValueType.Int) continue;
                addon->AtkValues[i].Int = 255;
            }
        }

        return orig;
    }
    
    private void OnPreSendPacket(ref bool isPrevented, int opcode, ref nint packet, ref bool isPrioritize)
    {
        if (opcode != UpstreamOpcode.HandOverItemOpcode) return;
        if (config.OnlyActiveInWorkshop && HousingManager.Instance()->WorkshopTerritory == null) return;
        if (FreeCompanyCreditShop == null) return;

        var data = (HandOverItemPacket*)packet;
        if (data->Param0 < 99) return;

        data->Param0 = 255;
    }
    
    private class Config : ModuleConfig
    {
        public bool OnlyActiveInWorkshop = true;
    }
}
