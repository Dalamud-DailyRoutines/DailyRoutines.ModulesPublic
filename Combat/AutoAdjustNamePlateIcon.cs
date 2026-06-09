using System.Numerics;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using FFXIVClientStructs.FFXIV.Client.UI;

namespace DailyRoutines.ModulesPublic;

public unsafe class AutoAdjustNamePlateIcon : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoAdjustNamePlateIconTitle"),
        Description = Lang.Get("AutoAdjustNamePlateIconDescription"),
        Category    = ModuleCategory.Combat,
        Author      = ["Marsh"]
    };

    public override ModulePermission Permission { get; } = new() { AllDefaultEnabled = true };

    private Config config = null!;

    protected override void Init()
    {
        config = Config.Load(this) ?? new();

        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PreRequestedUpdate, "NamePlate", OnAddon);
    }
    
    protected override void Uninit() =>
        DService.Instance().AddonLifecycle.UnregisterListener(OnAddon);

    protected override void ConfigUI()
    {
        if (ImGui.InputFloat(Lang.Get("Scale"), ref config.Scale, 0f, 2f, "%.2f"))
            config.Save(this);

        if (ImGui.InputFloat2($"{Lang.Get("IconOffset")}", ref config.Offset, -100f, 100f, "%.1f"))
            config.Save(this);
    }

    private void OnAddon(AddonEvent type, AddonArgs? args)
    {
        var addon = (AddonNamePlate*)NamePlate;
        if (!NamePlate->IsAddonAndNodesReady()) return;

        for (var i = 0; i < 50; i++)
        {
            var obj = addon->NamePlateObjectArray[i];
            if (!obj.IsVisible || !obj.MarkerIcon->IsVisible())
                continue;
            
            var imageNode = obj.MarkerIcon;
            if (imageNode == null) return;

            var scale  = config.Scale;
            var iconW  = imageNode->Width;
            var iconH  = imageNode->Height;
            var compX  = iconW * (1f - scale) / 2f;
            var compY  = iconH * (1f - scale) / 2f;
            imageNode->SetScale(scale, scale);
            imageNode->SetPositionFloat(96f + config.Offset.X + compX, 4f + config.Offset.Y + compY);
        }
    }

    private class Config : ModuleConfig
    {
        public Vector2 Offset;
        public float   Scale = 1f;
    }
}
