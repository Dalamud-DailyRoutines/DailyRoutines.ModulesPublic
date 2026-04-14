using System.Numerics;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace DailyRoutines.ModulesPublic;

public unsafe class NameplateIconAdjustment : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("NameplateIconAdjustmentTitle"),
        Description = Lang.Get("NameplateIconAdjustmentDescription"),
        Category    = ModuleCategory.Combat,
        Author      = ["Marsh"]
    };
    
    private Config config = null!;

    protected override void Init()
    {
        config = Config.Load(this) ?? new();

        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PreDraw, "NamePlate", OnAddon);
    }
    
    protected override void Uninit() =>
        DService.Instance().AddonLifecycle.UnregisterListener(OnAddon);

    protected override void ConfigUI()
    {
        if (ImGui.SliderFloat(Lang.Get("Scale"), ref config.Scale, 0f, 2f, "%.2f"))
            config.Save(this);

        if (ImGui.SliderFloat2($"{Lang.Get("IconOffset")}", ref config.Offset, -100f, 100f, "%.1f"))
            config.Save(this);
    }

    private void OnAddon(AddonEvent type, AddonArgs? args)
    {
        var addon = NamePlate;
        if (!NamePlate->IsAddonAndNodesReady()) return;

        {
            var componentNode = addon->GetComponentNodeById(2);
            if (componentNode == null) return;

            var imageNode = (AtkImageNode*)componentNode->Component->UldManager.SearchNodeById(9);
            if (imageNode == null) return;

            imageNode->SetScale(config.Scale, config.Scale);

            var posX = (1.5f - config.Scale * 0.5f) * 96f + config.Offset.X * config.Scale;
            var posY = 4                                        + config.Offset.Y * config.Scale;
            imageNode->SetPositionFloat(posX, posY);
        }

        for (uint i = 0; i < 49; i++)
        {
            var componentNode = addon->GetComponentNodeById(i + 20001);

            if (componentNode == null) return;

            var imageNode = (AtkImageNode*)componentNode->Component->UldManager.SearchNodeById(9);
            if (imageNode == null) return;

            imageNode->SetScale(config.Scale, config.Scale);

            var posX = (1.5f - config.Scale * 0.5f) * 96f + config.Offset.X * config.Scale;
            var posY = 4                                        + config.Offset.Y * config.Scale;
            imageNode->SetPositionFloat(posX, posY);
        }
    }

    public class Config : ModuleConfig
    {
        public Vector2 Offset;
        public float   Scale = 1f;
    }
}
