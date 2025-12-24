using System.Runtime.InteropServices;
using DailyRoutines.Abstracts;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace DailyRoutines.ModulesPublic;

public unsafe class CompanyCreditExchangeMore : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("CompanyCreditExchangeMoreTitle"),
        Description = GetLoc("CompanyCreditExchangeMoreDescription"),
        Category    = ModuleCategories.System,
    };
    
    public override ModulePermission Permission { get; } = new() { AllDefaultEnabled = true };

    private static readonly CompSig AddonFreeCompanyCreditShopRefreshSig = new("41 56 41 57 48 83 EC ?? 0F B6 81 ?? ?? ?? ?? 4D 8B F8");
    [return: MarshalAs(UnmanagedType.U1)]
    private delegate        bool   AddonFreeCompanyCreditShopRefreshDelegate(AtkUnitBase* addon, uint atkValueCount, AtkValue* atkValues);
    private static          Hook<AddonFreeCompanyCreditShopRefreshDelegate> AddonFreeCompanyCreditShopRefreshHook;

    private static Config ModuleConfig = null!;
    
    protected override void Init()
    {
        ModuleConfig = LoadConfig<Config>() ?? new();

        AddonFreeCompanyCreditShopRefreshHook = AddonFreeCompanyCreditShopRefreshSig.GetHook<AddonFreeCompanyCreditShopRefreshDelegate>(AddonRefreshDetour);
        AddonFreeCompanyCreditShopRefreshHook.Enable();
        
        GamePacketManager.RegPreSendPacket(OnPreSendPacket);
    }

    private static bool AddonRefreshDetour(AtkUnitBase* addon, uint atkValueCount, AtkValue* atkValues)
    {
        if (addon == null) return false;
        
        var orig = AddonFreeCompanyCreditShopRefreshHook.Original(addon, atkValueCount, atkValues);

        if (!ModuleConfig.OnlyActiveInWorkshop || HousingManager.Instance()->WorkshopTerritory != null)
        {
            for (var i = 110; i < 130; i++)
            {
                if (addon->AtkValues[i].Type != ValueType.Int) continue;
                addon->AtkValues[i].Int = 255;
            }
        }

        return orig;
    }

    protected override void ConfigUI()
    {
        if (ImGui.Checkbox(GetLoc("CompanyCreditExchangeMore-OnlyActiveInWorkshop"), ref ModuleConfig.OnlyActiveInWorkshop))
            ModuleConfig.Save(this);
    }

    private static void OnPreSendPacket(ref bool isPrevented, int opcode, ref byte* packet, ref ushort priority)
    {
        if (opcode != GamePacketOpcodes.HandOverItemOpcode) return;
        if (ModuleConfig.OnlyActiveInWorkshop && HousingManager.Instance()->WorkshopTerritory == null) return;
        if (FreeCompanyCreditShop == null) return;
        
        var data = (HandOverItemPacket*)packet;
        if (data->Param0 < 99) return;
        
        data->Param0 = 255;
    }

    protected override void Uninit() => 
        GamePacketManager.Unreg(OnPreSendPacket);

    private class Config : ModuleConfiguration
    {
        public bool OnlyActiveInWorkshop = true;
    }
}
