using DailyRoutines.Abstracts;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Text.SeStringHandling;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Component.GUI;
using System;
using System.Collections.Generic;

namespace DailyRoutines.Modules;

public class ShowStatusRemainingTime : DailyModuleBase
{
    public override ModuleInfo Info => new()
    {
        Title = GetLoc("ShowStatusRemainingTimeTitle"),
        Description = GetLoc("ShowStatusRemainingTimeDescription"),
        Category = ModuleCategories.UIOptimization,
        Author = ["Due"],
    };

    private static readonly string[] StatusAddons = ["_StatusCustom0", "_StatusCustom2"];

    public override void Init()
    {
        DService.AddonLifecycle.RegisterListener(AddonEvent.PostRequestedUpdate, StatusAddons, OnPostUpdate);
    }

    private unsafe void OnPostUpdate(AddonEvent type, AddonArgs args)
    {
        if (DService.Condition[ConditionFlag.InCombat]) return;

        var addon = DService.Gui.GetAddonByName(args.AddonName);
        if (addon == nint.Zero) return;

        var UnitBase = (AtkUnitBase*)addon;

        for (var i = 2; i <= 21; i++)
        {
            var Component = UnitBase->GetComponentByNodeId((uint)i);
            if (!Component->OwnerNode->IsVisible()) return;

            var ImageNode = Component->AtkResNode->GetAsAtkImageNode();
            var TextNode = ImageNode->PrevSiblingNode->GetAsAtkTextNode();
            var time = SeString.Parse(TextNode->GetText()).ToString();

            if (!time.Contains('h') && !time.Contains("小时")) continue;

            var IconID = (uint)0;
            try
            {
                IconID = ImageNode->PartsList[0].Parts[0].UldAsset[0].AtkTexture.Resource->IconId;
            }
            catch (Exception)
            {
                continue;
            }

            if (IconID == 0) continue;

            if (iconStatusPair.TryGetValue(IconID, out var value))
            {
                var remainingTime = GetRemainingTime(value);
                if (string.IsNullOrEmpty(remainingTime)) continue;

                time = remainingTime;
                TextNode->SetText(time);
            }
        }
    }

    private static unsafe string GetRemainingTime(uint type)
    {
        if (DService.ClientState.LocalPlayer == null) return string.Empty;

        var statusManager = ((Character*)DService.ClientState.LocalPlayer.Address)->GetStatusManager();
        if (!statusManager->HasStatus(type)) return string.Empty;

        return TimeSpan.FromSeconds(statusManager->GetRemainingTime(statusManager->GetStatusIndex(type)))
                       .ToString(@"hh\:mm");
    }

    private readonly Dictionary<uint, uint> iconStatusPair = new()
    {
        { 16106, 45 },
        { 16006, 46 },
        { 16202, 48 },
        { 16203, 49 },
        { 16513, 1080 },
        { 216106, 45 },
        { 216006, 46 },
        { 216202, 48 },
        { 216203, 49 },
        { 216513, 1080 }
    };

    public override void Uninit()
    {
        DService.AddonLifecycle.UnregisterListener(OnPostUpdate);
        base.Uninit();
    }

}