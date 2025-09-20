using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using DailyRoutines.Abstracts;
using DailyRoutines.Managers;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.Gui.Dtr;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using InteropGenerator.Runtime;
using Lumina.Excel.Sheets;
using RowStatus = Lumina.Excel.Sheets.Status;

namespace DailyRoutines.ModulesPublic;

public unsafe class ShowMoreIDInfomation : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title = GetLoc("ShowMoreIDInfomationTitle"),
        Description = GetLoc("ShowMoreIDInfomationDescription"),
        Category = ModuleCategories.UIOptimization,
        Author = ["Middo"]
    };

    private static Config ModuleConfig = null!;

    private static IDtrBarEntry? MapIDEntry;

    private static Guid ItemTooltipModityGuid = Guid.Empty;
    private static Guid ActionTooltipModityGuid = Guid.Empty;
    private static Guid StatuTooltipModityGuid = Guid.Empty;
    private static Guid WeatherTooltipMidifyGuid = Guid.Empty;

    protected override void Init()
    {
        ModuleConfig = LoadConfig<Config>() ?? new();
        MapIDEntry ??= DService.DtrBar.Get("ShowMoreIDInfomation-MapID");

        GameTooltipManager.RegGenerateItemTooltipModifier(ModifyItemTooltip);
        GameTooltipManager.RegGenerateActionTooltipModifier(ModifyActionTooltip);
        GameTooltipManager.RegTooltipShowModifier(ModifyStatuTooltip);
        GameTooltipManager.RegTooltipShowModifier(ModifyWeatherTooltip);

        DService.AddonLifecycle.RegisterListener(AddonEvent.PostDraw,          "ActionDetail", OnAddon);
        DService.AddonLifecycle.RegisterListener(AddonEvent.PostDraw,            "ItemDetail", OnAddon);
        DService.AddonLifecycle.RegisterListener(AddonEvent.PreDraw,  "_TargetInfoMainTarget", OnAddon);
        DService.AddonLifecycle.RegisterListener(AddonEvent.PostDraw,              "_NaviMap", OnAddon);
    }

    protected override void ConfigUI()
    {
        using (ImRaii.Group())
        {
            if (ImGui.Checkbox(GetLoc("ShowMoreIDInfomation-ShowItemID"), ref ModuleConfig.ShowItemID))
                SaveConfig(ModuleConfig);
            ImGui.SameLine();
            if (ModuleConfig.ShowItemID)
            {
                if (ImGui.Checkbox(GetLoc("ShowMoreIDInfomation-ItemIDUseHexID"), ref ModuleConfig.ItemIDUseHexID))
                    SaveConfig(ModuleConfig);
                if (ModuleConfig.ItemIDUseHexID)
                {
                    ImGui.SameLine();
                    if (ImGui.Checkbox(GetLoc("ShowMoreIDInfomation-ItemIDUseBothHexAndDecimal"), ref ModuleConfig.ItemIDUseBothHexAndDecimal))
                        SaveConfig(ModuleConfig);
                }
            }
        }

        using (ImRaii.Group())
        {
            var prevAction = ModuleConfig.ShowActionID;
            if (ImGui.Checkbox(GetLoc("ShowMoreIDInfomation-ShowActionID"), ref ModuleConfig.ShowActionID))
                SaveConfig(ModuleConfig);
            if (ModuleConfig.ShowActionID)
            {
                ImGui.SameLine();
                if (ImGui.Checkbox(GetLoc("ShowMoreIDInfomation-ActionIDUseHex"), ref ModuleConfig.ActionIDUseHex))
                    SaveConfig(ModuleConfig);
                ImGui.SameLine();
                if (ImGui.Checkbox(GetLoc("ShowMoreIDInfomation-ShowResolvedActionID"), ref ModuleConfig.ShowResolvedActionID))
                    SaveConfig(ModuleConfig);               
            }

            ImRaii.PushIndent(2);
            if (ModuleConfig.ActionIDUseHex && ModuleConfig.ShowActionID)
            {
                if (ImGui.Checkbox(GetLoc("ShowMoreIDInfomation-ActionIDUseBothHexAndDecimal"), ref ModuleConfig.ActionIDUseBothHexAndDecimal))
                {
                    ModuleConfig.ShowOriginalActionID = false;
                    SaveConfig(ModuleConfig);
                }
            }
            if (ModuleConfig.ShowResolvedActionID && ModuleConfig.ActionIDUseHex) 
                ImGui.SameLine();             
            if (ModuleConfig.ShowResolvedActionID && ModuleConfig.ShowActionID)
            {
                if (ImGui.Checkbox(GetLoc("ShowMoreIDInfomation-ShowOriginalActionID"), ref ModuleConfig.ShowOriginalActionID))
                {
                    ModuleConfig.ActionIDUseBothHexAndDecimal = false;
                    SaveConfig(ModuleConfig);
                }  
            }
        }

        using (ImRaii.Group())
        {
            if (ImGui.Checkbox(GetLoc("ShowMoreIDInfomation-ShowTargetID"), ref ModuleConfig.ShowTargetID))
                SaveConfig(ModuleConfig);
            ImGui.SameLine();
            if (ModuleConfig.ShowTargetID)
            {
                if (ImGui.Checkbox(GetLoc("ShowMoreIDInfomation-ShowBattleNPCTargetID"), ref ModuleConfig.ShowBattleNPCTargetID))
                    SaveConfig(ModuleConfig);
                ImGui.SameLine();
                if (ImGui.Checkbox(GetLoc("ShowMoreIDInfomation-ShowEventNPCTargetID"), ref ModuleConfig.ShowEventNPCTargetID))
                    SaveConfig(ModuleConfig);
                ImGui.SameLine();
                if (ImGui.Checkbox(GetLoc("ShowMoreIDInfomation-ShowCompanionTargetID"), ref ModuleConfig.ShowCompanionTargetID))
                    SaveConfig(ModuleConfig);
                ImGui.SameLine();
                if (ImGui.Checkbox(GetLoc("ShowMoreIDInfomation-ShowOthersTargetID"), ref ModuleConfig.ShowOthersTargetID))
                    SaveConfig(ModuleConfig);
            }
        }

        using (ImRaii.Group())
        {
            if (ImGui.Checkbox(GetLoc("ShowMoreIDInfomation-ShowBuffID"), ref ModuleConfig.ShowStatuID))
                SaveConfig(ModuleConfig);
            ImGui.SameLine();
            if (ImGui.Checkbox(GetLoc("ShowMoreIDInfomation-ShowWeatherID"), ref ModuleConfig.ShowWeatherID))
                SaveConfig(ModuleConfig);
            ImGui.SameLine();
            if (ImGui.Checkbox(GetLoc("ShowMoreIDInfomation-ShowMapID"), ref ModuleConfig.ShowMapID))
                SaveConfig(ModuleConfig);
        }
    }

    private void OnAddon(AddonEvent type, AddonArgs args)
    {
        switch (args.AddonName)
        {
            case "ActionDetail":
                if (ActionDetail== null) return;

                var actionTextNode = ActionDetail->GetTextNodeById(6);
                if (actionTextNode == null) return;
                
                actionTextNode->TextFlags |= TextFlags.MultiLine;
                break;
            case "ItemDetail":
                if (ItemDetail== null) return;

                var itemTextnode = ItemDetail->GetTextNodeById(35);
                if (itemTextnode == null) return;

                itemTextnode->TextFlags |= TextFlags.MultiLine;
                break;
            case "_TargetInfoMainTarget":
                if (TargetInfoMainTarget == null) return;
                
                var targetNameNode = TargetInfoMainTarget->GetNodeById(10)->GetAsAtkTextNode();
                var target = DService.Targets.Target;
                if (targetNameNode == null || target == null) return;

                var id = target.DataId;
                var name = targetNameNode->NodeText.ExtractText();
                var show = target.ObjectKind switch
                {
                    ObjectKind.BattleNpc => ModuleConfig.ShowBattleNPCTargetID,
                    ObjectKind.EventNpc => ModuleConfig.ShowEventNPCTargetID,
                    ObjectKind.Companion => ModuleConfig.ShowCompanionTargetID,
                    _ => ModuleConfig.ShowOthersTargetID,
                };
                
                if (!show || !ModuleConfig.ShowTargetID)
                {
                    targetNameNode->NodeText.SetString(name.Replace($"  [{id}]",""));
                    return;
                }

                if (!name.Contains($"[{id}]"))
                    targetNameNode->NodeText.SetString($"{name}  [{id}]");
                break;
            case "_NaviMap":
                MapIDEntry.Shown = ModuleConfig.ShowMapID;
                var mapID = DService.ClientState.MapId;
                if (mapID != 0)
                    MapIDEntry.Text = $"{GetLoc("ShowMoreIDInfomation-CurrentMapIDIs")}{mapID}";
                break;
        }
    }

    private void ModifyItemTooltip(AtkUnitBase* addonItemDetail, NumberArrayData* numberArrayData, StringArrayData* stringArrayData)
    {
        if (ItemTooltipModityGuid != Guid.Empty)
        {
            GameTooltipManager.RemoveItemDetailTooltipModify(ItemTooltipModityGuid);
            ItemTooltipModityGuid = Guid.Empty;
        }

        if (!ModuleConfig.ShowItemID) return;

        var itemID = AgentItemDetail.Instance()->ItemId;
        if (itemID < 2000000)
            itemID %= 500000;

        var payloads = new List<Payload>()
        {
            new UIForegroundPayload(3),
            new TextPayload("   [")
        };

        if (!ModuleConfig.ItemIDUseHexID || ModuleConfig.ItemIDUseBothHexAndDecimal)
            payloads.Add(new TextPayload($"{itemID}"));

        if (ModuleConfig.ItemIDUseHexID)
        {
            if (ModuleConfig.ItemIDUseBothHexAndDecimal)
                payloads.Add(new TextPayload(" - "));
            payloads.Add(new TextPayload($"0x{itemID:X}"));
        }

        payloads.Add(new TextPayload("]"));
        payloads.Add(new UIForegroundPayload(0));

        ItemTooltipModityGuid = GameTooltipManager.AddItemDetailTooltipModify(itemID, TooltipItemType.ItemUICategory, new SeString(payloads), TooltipModifyMode.Append);
    }

    private void ModifyActionTooltip(AtkUnitBase* addonActionDetail, NumberArrayData* numberArrayData, StringArrayData* stringArrayData)
    {
        if (ActionTooltipModityGuid != Guid.Empty)
        {
            GameTooltipManager.RemoveItemDetailTooltipModify(ActionTooltipModityGuid);
            ActionTooltipModityGuid = Guid.Empty;
        }

        if (!ModuleConfig.ShowActionID) return;

        var hoveredID = AgentActionDetail.Instance()->ActionId;
        var id = ModuleConfig is { ShowResolvedActionID: true, ShowOriginalActionID: false } 
               ? hoveredID 
               : AgentActionDetail.Instance()->OriginalId;

        var payloads = new List<Payload>();
        var needNewLine = ModuleConfig is { ShowResolvedActionID: true, ShowOriginalActionID: true, ActionIDUseBothHexAndDecimal: false } && id != hoveredID;

        payloads.Add(needNewLine ? new NewLinePayload() : new TextPayload("   "));
        payloads.Add(new UIForegroundPayload(3));
        payloads.Add(new TextPayload("["));

        if (ModuleConfig is { ActionIDUseHex: false } or { ActionIDUseBothHexAndDecimal: true })
            payloads.Add(new TextPayload($"{id}"));

        if (ModuleConfig.ActionIDUseHex)
        {
            if (ModuleConfig.ActionIDUseBothHexAndDecimal)
                payloads.Add(new TextPayload(" - "));
            payloads.Add(new TextPayload($"0x{id:X}"));
        }

        if (ModuleConfig is { ShowResolvedActionID: true, ShowOriginalActionID: true, ActionIDUseBothHexAndDecimal: false } && id != hoveredID)
        {
            var arrowText = ModuleConfig.ActionIDUseHex 
                          ? $" → 0x{hoveredID:X}" 
                          : $" → {hoveredID}";
            payloads.Add(new TextPayload(arrowText));
        }

        payloads.Add(new TextPayload("]"));
        payloads.Add(new UIForegroundPayload(0));

        ActionTooltipModityGuid = GameTooltipManager.AddActionDetailTooltipModify(hoveredID, TooltipActionType.ActionKind, new SeString(payloads), TooltipModifyMode.Append);
    }

    private void ModifyStatuTooltip(AtkTooltipManager* manager, AtkTooltipManager.AtkTooltipType type, ushort parentID, AtkResNode* targetNode, AtkTooltipManager.AtkTooltipArgs* args)
    {
        if (StatuTooltipModityGuid != Guid.Empty)
        {
            GameTooltipManager.RemoveItemDetailTooltipModify(StatuTooltipModityGuid);
            StatuTooltipModityGuid = Guid.Empty;
        }

        if (!ModuleConfig.ShowStatuID) return;

        var localPlayer = DService.ObjectTable.LocalPlayer;
        if (localPlayer == null || targetNode == null) return;

        var imageNode = targetNode->GetAsAtkImageNode();
        if (imageNode == null) return;

        var iconID = imageNode->PartsList->Parts[imageNode->PartId].UldAsset->AtkTexture.Resource->IconId;
        if (iconID < 210000 || iconID > 230000) return;

        var map = new Dictionary<uint, uint>();
        void AddStatuses(StatusManager sm) => AddStatusesToMap(sm, ref map);

        if (DService.Targets.Target is { } target && target.Address != localPlayer.Address)
            AddStatuses(target.ToBCStruct()->StatusManager);

        if (DService.Targets.FocusTarget is { } focus)
            AddStatuses(focus.ToBCStruct()->StatusManager);

        foreach (var member in AgentHUD.Instance()->PartyMembers.ToArray().Where(m => m.Index != 0))
        {
            if (member.Object != null)
                AddStatuses(member.Object->StatusManager);
        }
        AddStatuses(localPlayer.ToBCStruct()->StatusManager);

        if (!map.TryGetValue(iconID, out var statuID) || statuID == 0) return;

        StatuTooltipModityGuid = GameTooltipManager.AddstatuTooltipModify(statuID, $"  [{statuID}]", TooltipModifyMode.Regex, @"^(.*?)(?=\(|（|\n|$)");
    }

    private void ModifyWeatherTooltip(AtkTooltipManager* manager, AtkTooltipManager.AtkTooltipType type, ushort parentID, AtkResNode* targetNode, AtkTooltipManager.AtkTooltipArgs* args)
    {
        if (WeatherTooltipMidifyGuid != Guid.Empty)
        {
            GameTooltipManager.RemoveWeatherTooltipModify(WeatherTooltipMidifyGuid);
            WeatherTooltipMidifyGuid = Guid.Empty;
        }

        if (!ModuleConfig.ShowWeatherID) return;

        var weatherID = WeatherManager.Instance()->WeatherId;
        if (!LuminaGetter.TryGetRow<Weather>(weatherID, out var weather)) return;

        WeatherTooltipMidifyGuid = GameTooltipManager.AddWeatherTooltipModify($" [{weatherID}]", TooltipModifyMode.Append);
    }

    protected override void Uninit()
    {
        MapIDEntry?.Remove();
        MapIDEntry = null;

        GameTooltipManager.Unreg((GameTooltipManager.GenerateItemTooltipModifierDelegate)ModifyItemTooltip);
        GameTooltipManager.Unreg((GameTooltipManager.GenerateActionTooltipModifierDelegate)ModifyActionTooltip);
        GameTooltipManager.Unreg(ModifyStatuTooltip);
        GameTooltipManager.Unreg(ModifyWeatherTooltip);

        DService.AddonLifecycle.UnregisterListener(OnAddon);
    }

    protected static void SetTooltipCStringPointer(ref CStringPointer text, SeString seString)
    {
        var bytes = seString.EncodeWithNullTerminator();
        var ptr = (byte*)Marshal.AllocHGlobal(bytes.Length);
        for (var i = 0; i < bytes.Length; i++)
            ptr[i] = bytes[i];
        text = ptr;
    }

    private static unsafe void AddStatusesToMap(StatusManager statusManager, ref Dictionary<uint, uint> map)
    {
        foreach (var s in statusManager.Status)
        {
            if (s.StatusId == 0) continue;
            if (!LuminaGetter.TryGetRow<RowStatus>(s.StatusId, out var row))
                continue;
            map.TryAdd(row.Icon, row.RowId);
            for (var i = 1; i <= s.Param; i++)
                map.TryAdd((uint)(row.Icon + i), row.RowId);
        }
    }

    public class Config : ModuleConfiguration
    {
        public bool ShowItemID                   = true;
        public bool ItemIDUseHexID               = false;
        public bool ItemIDUseBothHexAndDecimal   = false;

        public bool ShowActionID                 = true;
        public bool ActionIDUseHex               = false;
        public bool ActionIDUseBothHexAndDecimal = false;
        public bool ShowResolvedActionID         = false;
        public bool ShowOriginalActionID         = false;

        public bool ShowStatuID                  = false;
        public bool ShowWeatherID                = false;
        public bool ShowMapID                    = false;
        public bool ShowTargetID                 = false;

        public bool ShowBattleNPCTargetID        = false;
        public bool ShowCompanionTargetID        = false;
        public bool ShowEventNPCTargetID         = false;
        public bool ShowOthersTargetID           = false;
    }
}
