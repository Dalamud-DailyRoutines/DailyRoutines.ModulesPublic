using System.Numerics;
using DailyRoutines.Abstracts;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace DailyRoutines.ModulesPublic;

public unsafe class NameplateIconAdjustment : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("NameplateIconAdjustmentTitle"),
        Description = GetLoc("NameplateIconAdjustmentDescription"),
        Category    = ModuleCategories.Combat,
        Author      = ["Marsh"]
    };

    private static Config ModuleConfig = null!;

    protected override void Init()
    {
        ModuleConfig = new Config().Load(this);

        DService.AddonLifecycle.RegisterListener(AddonEvent.PreDraw, "NamePlate", OnAddon);
    }

    protected override void ConfigUI()
    {
        if (ImGui.SliderFloat(GetLoc("Scale"), ref ModuleConfig.Scale, 0f, 2f, "%.2f"))
            ModuleConfig.Save(this);

        if (ImGui.SliderFloat2($"{GetLoc("IconOffset")}", ref ModuleConfig.Offset, -100f, 100f, "%.1f"))
            ModuleConfig.Save(this);
    }
    
    private static void OnAddon(AddonEvent type, AddonArgs? args)
    {
        var addon = NamePlate;
        if (!IsAddonAndNodesReady(NamePlate)) return;

        {
            var componentNode = addon->GetComponentNodeById(2);
            if (componentNode == null) return;

            var imageNode = (AtkImageNode*)componentNode->Component->UldManager.SearchNodeById(9);
            if (imageNode == null) return;

            imageNode->SetScale(ModuleConfig.Scale, ModuleConfig.Scale);
            
            var posX = ((1.5f - (ModuleConfig.Scale * 0.5f)) * 96f) + (ModuleConfig.Offset.X * ModuleConfig.Scale);
            var posY = 4                                            + (ModuleConfig.Offset.Y * ModuleConfig.Scale);
            imageNode->SetPositionFloat(posX, posY);
        }

        for (uint i = 0; i < 49; i++)
        {
            var componentNode = addon->GetComponentNodeById(i + 20001);

            if (componentNode == null) return;

            var imageNode = (AtkImageNode*)componentNode->Component->UldManager.SearchNodeById(9);
            if (imageNode == null) return;

            imageNode->SetScale(ModuleConfig.Scale, ModuleConfig.Scale);
            
            var posX = ((1.5f - (ModuleConfig.Scale * 0.5f)) * 96f) + (ModuleConfig.Offset.X * ModuleConfig.Scale);
            var posY = 4                                            + (ModuleConfig.Offset.Y * ModuleConfig.Scale);
            imageNode->SetPositionFloat(posX, posY);
        }
    }

    protected override void Uninit() => 
        DService.AddonLifecycle.UnregisterListener(OnAddon);

    public class Config : ModuleConfiguration
    {
        public float   Scale  = 1f;
        public Vector2 Offset;
    }
}
