using System.Numerics;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using OmenTools.Interop.Game.Models;

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

    private static readonly CompSig AtkUldManagerUpdateFromParentNodeSig = new("48 89 5C 24 ?? 55 56 57 48 83 EC ?? 41 0F B6 F1");
    private delegate void AtkUldManagerUpdateFromParentNodeDelegate
    (
        AtkUldManager* manager,
        AtkResNode*    node,
        AtkResNode*    parentNode,
        byte           onlyDirty
    );
    private AtkUldManagerUpdateFromParentNodeDelegate AtkUldManagerUpdateFromParentNode = null!;

    private readonly Vector2[] basePositions    = new Vector2[AddonNamePlate.NumNamePlateObjects];
    private readonly Vector2[] appliedPositions = new Vector2[AddonNamePlate.NumNamePlateObjects];
    private readonly bool[]    applied          = new bool[AddonNamePlate.NumNamePlateObjects];
    
    private Config config = null!;

    protected override void Init()
    {
        config = Config.Load(this) ?? new();

        AtkUldManagerUpdateFromParentNode = AtkUldManagerUpdateFromParentNodeSig.GetDelegate<AtkUldManagerUpdateFromParentNodeDelegate>();

        IAddonLifecycle.Instance().RegisterListener(AddonEvent.PostRequestedUpdate, "NamePlate", OnAddonEvent);
        IAddonLifecycle.Instance().RegisterListener(AddonEvent.PostUpdate,          "NamePlate", OnAddonEvent);
    }

    protected override void Uninit()
    {
        IAddonLifecycle.Instance().UnregisterListener(OnAddonEvent);
        ResetIcons();
    }

    protected override void ConfigUI()
    {
        if (ImGui.InputFloat(Lang.Get("Scale"), ref config.Scale, 0f, 2f, "%.2f"))
            config.Save(this);

        if (ImGui.InputFloat2($"{Lang.Get("IconOffset")}", ref config.Offset, -100f, 100f, "%.1f"))
            config.Save(this);
    }

    private void OnAddonEvent
    (
        AddonEvent type,
        AddonArgs? args
    )
    {
        var unitBase = NamePlate;
        if (unitBase == null || !unitBase->IsAddonAndNodesReady()) return;

        var addon = (AddonNamePlate*)unitBase;
        if (addon->NamePlateObjectArray == null || addon->RootNode == null) return;

        for (var i = 0; i < AddonNamePlate.NumNamePlateObjects; i++)
        {
            var icon = addon->NamePlateObjectArray[i].MarkerIcon;
            if (icon == null || !icon->IsVisible()) continue;

            ApplyIcon(icon, i, config.Scale, config.Offset);
        }

        RefreshNodeTransforms(addon);
    }

    private void ApplyIcon
    (
        AtkImageNode* icon,
        int           index,
        float         scale,
        Vector2       offset
    )
    {
        var current = new Vector2(icon->X, icon->Y);
        if (current != appliedPositions[index])
            basePositions[index] = current;

        var target = basePositions[index] + offset;

        icon->SetOrigin(icon->Width / 2f, icon->Height / 2f);
        icon->SetScale(scale, scale);
        icon->SetPositionFloat(target.X, target.Y);

        appliedPositions[index] = target;
        applied[index]          = true;
    }

    private void RefreshNodeTransforms
    (
        AddonNamePlate* addon
    )
    {
        addon->RootNode->DrawFlags |= 1;
        AtkUldManagerUpdateFromParentNode(&addon->UldManager, addon->RootNode, addon->RootNode->ParentNode, 1);
    }

    private void ResetIcons()
    {
        var unitBase = NamePlate;
        if (unitBase == null) return;

        var addon = (AddonNamePlate*)unitBase;
        if (addon->NamePlateObjectArray == null || addon->RootNode == null) return;

        var modified = false;

        for (var i = 0; i < AddonNamePlate.NumNamePlateObjects; i++)
        {
            if (!applied[i]) continue;

            var icon = addon->NamePlateObjectArray[i].MarkerIcon;
            if (icon == null) continue;

            icon->SetOrigin(0f, 0f);
            icon->SetScale(1f, 1f);
            icon->SetPositionFloat(basePositions[i].X, basePositions[i].Y);

            applied[i] = false;
            modified   = true;
        }

        if (modified)
            RefreshNodeTransforms(addon);
    }

    private class Config : ModuleConfig
    {
        public Vector2 Offset;
        public float   Scale = 1f;
    }
}
