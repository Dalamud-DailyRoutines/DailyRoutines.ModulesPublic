using System;
using System.Collections.Generic;
using System.Linq;
using DailyRoutines.Abstracts;
using DailyRoutines.Managers;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.Gui.Dtr;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Plugin.Services;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using RowStatus = Lumina.Excel.Sheets.Status;

namespace DailyRoutines.ModulesPublic;

public unsafe class AutoDisplayIDInfomation : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("AutoDisplayIDInfomationTitle"),
        Description = GetLoc("AutoDisplayIDInfomationDescription"),
        Category    = ModuleCategories.UIOptimization,
        Author      = ["Middo"]
    };

    private static Config ModuleConfig = null!;

    private static IDtrBarEntry? ZoneInfoEntry;

    private static Guid ItemTooltipModityGuid    = Guid.Empty;
    private static Guid ActionTooltipModityGuid  = Guid.Empty;
    private static Guid StatuTooltipModityGuid   = Guid.Empty;
    private static Guid WeatherTooltipModifyGuid = Guid.Empty;

    protected override void Init()
    {
        ModuleConfig = LoadConfig<Config>() ?? new();
        ZoneInfoEntry ??= DService.DtrBar.Get("AutoDisplayIDInfomation-ZoneInfo");

        GameTooltipManager.RegGenerateItemTooltipModifier(ModifyItemTooltip);
        GameTooltipManager.RegGenerateActionTooltipModifier(ModifyActionTooltip);
        GameTooltipManager.RegTooltipShowModifier(ModifyStatuTooltip);
        GameTooltipManager.RegTooltipShowModifier(ModifyWeatherTooltip);

        DService.AddonLifecycle.RegisterListener(AddonEvent.PostDraw,         "ActionDetail", OnAddon);
        DService.AddonLifecycle.RegisterListener(AddonEvent.PostDraw,           "ItemDetail", OnAddon);
        DService.AddonLifecycle.RegisterListener(AddonEvent.PreDraw,           "_TargetInfo", OnAddon);
        DService.AddonLifecycle.RegisterListener(AddonEvent.PreDraw, "_TargetInfoMainTarget", OnAddon);

        FrameworkManager.Reg(OnUpdate, throttleMS: 1000);
    }

    private static void OnUpdate(IFramework framework)
    {
        if (ModuleConfig.ShowZoneInfo)
        {
            var mapID  = GameState.Map;
            var zoneID = GameState.TerritoryType;
            if (mapID == 0 || zoneID == 0)
            {
                ZoneInfoEntry.Shown = false;
                return;
            }
            
            ZoneInfoEntry.Shown = true;
            
            ZoneInfoEntry.Text = $"{LuminaWrapper.GetAddonText(870)}: {zoneID} / {LuminaWrapper.GetAddonText(670)}: {mapID}";
        }
        else
            ZoneInfoEntry.Shown = false;
    }

    protected override void ConfigUI()
    {
        if (ImGui.Checkbox($"{LuminaWrapper.GetAddonText(520)} ID", ref ModuleConfig.ShowItemID))
            SaveConfig(ModuleConfig);

        ImGui.NewLine();
        
        if (ImGui.Checkbox($"{LuminaWrapper.GetAddonText(1340)} ID", ref ModuleConfig.ShowActionID))
            SaveConfig(ModuleConfig);

        if (ModuleConfig.ShowActionID)
        {
            using (ImRaii.PushIndent())
            {
                if (ImGui.Checkbox(GetLoc("Resolved"), ref ModuleConfig.ShowActionIDResolved))
                    SaveConfig(ModuleConfig);
                
                if (ImGui.Checkbox(GetLoc("Original"), ref ModuleConfig.ShowActionIDOriginal))
                    SaveConfig(ModuleConfig);
            }
        }
        
        ImGui.NewLine();
        
        if (ImGui.Checkbox($"{LuminaWrapper.GetAddonText(1030)} ID", ref ModuleConfig.ShowTargetID))
            SaveConfig(ModuleConfig);
        
        if (ModuleConfig.ShowTargetID)
        {
            using (ImRaii.PushIndent())
            {
                if (ImGui.Checkbox("BattleNPC", ref ModuleConfig.ShowTargetIDBattleNPC))
                    SaveConfig(ModuleConfig);
                
                if (ImGui.Checkbox("EventNPC", ref ModuleConfig.ShowTargetIDEventNPC))
                    SaveConfig(ModuleConfig);
                
                if (ImGui.Checkbox("Companion", ref ModuleConfig.ShowTargetIDCompanion))
                    SaveConfig(ModuleConfig);
                
                if (ImGui.Checkbox(LuminaWrapper.GetAddonText(832), ref ModuleConfig.ShowTargetIDOthers))
                    SaveConfig(ModuleConfig);
            }
        }
        
        ImGui.NewLine();

        if (ImGui.Checkbox($"{GetLoc("Status")} ID", ref ModuleConfig.ShowStatusID))
            SaveConfig(ModuleConfig);
            
        ImGui.NewLine();
        
        if (ImGui.Checkbox($"{LuminaWrapper.GetAddonText(8555)} ID", ref ModuleConfig.ShowWeatherID))
            SaveConfig(ModuleConfig);
            
        ImGui.NewLine();
        
        if (ImGui.Checkbox($"{LuminaWrapper.GetAddonText(870)}", ref ModuleConfig.ShowZoneInfo))
            SaveConfig(ModuleConfig);
    }

    private static void OnAddon(AddonEvent type, AddonArgs args)
    {
        if (!Throttler.Throttle("AutoDisplayIDInfomation-OnAddon", 50)) return;
        
        switch (args.AddonName)
        {
            case "ActionDetail":
                if (ActionDetail== null) return;

                var actionTextNode = ActionDetail->GetTextNodeById(6);
                if (actionTextNode == null) return;
                
                actionTextNode->TextFlags |= TextFlags.MultiLine;
                actionTextNode->FontSize  =  (byte)(actionTextNode->NodeText.StringPtr.ExtractText().Contains('\n') ? 10 : 12);
                break;
            
            case "ItemDetail":
                if (ItemDetail== null) return;

                var itemTextnode = ItemDetail->GetTextNodeById(35);
                if (itemTextnode == null) return;

                itemTextnode->TextFlags |= TextFlags.MultiLine;
                break;
            
            case "_TargetInfoMainTarget" or "_TargetInfo":
                if (DService.Targets.Target is not { } target) return;

                var id = target.DataId;
                if (id == 0) return;

                var name = AtkStage.Instance()->GetStringArrayData(StringArrayType.Hud2)->StringArray->ExtractText();
                var show = target.ObjectKind switch
                {
                    ObjectKind.BattleNpc => ModuleConfig.ShowTargetIDBattleNPC,
                    ObjectKind.EventNpc  => ModuleConfig.ShowTargetIDEventNPC,
                    ObjectKind.Companion => ModuleConfig.ShowTargetIDCompanion,
                    _                    => ModuleConfig.ShowTargetIDOthers,
                };

                if (!show || !ModuleConfig.ShowTargetID)
                {
                    AtkStage.Instance()->GetStringArrayData(StringArrayType.Hud2)->SetValueAndUpdate(0, name.Replace($"  [{id}]", string.Empty));
                    return;
                }

                if (!name.Contains($"[{id}]"))
                    AtkStage.Instance()->GetStringArrayData(StringArrayType.Hud2)->SetValueAndUpdate(0, $"{name}  [{id}]");
                break;
        }
    }

    private static void ModifyItemTooltip(AtkUnitBase* addonItemDetail, NumberArrayData* numberArrayData, StringArrayData* stringArrayData)
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

        var payloads = new List<Payload>
        {
            new UIForegroundPayload(3),
            new TextPayload("   ["),
            new TextPayload($"{itemID}"),
            new TextPayload("]"),
            new UIForegroundPayload(0)
        };

        ItemTooltipModityGuid = GameTooltipManager.AddItemDetailTooltipModify(
            itemID,
            TooltipItemType.ItemUICategory,
            new SeString(payloads),
            TooltipModifyMode.Append);
    }

    private static void ModifyActionTooltip(AtkUnitBase* addonActionDetail, NumberArrayData* numberArrayData, StringArrayData* stringArrayData)
    {
        if (ActionTooltipModityGuid != Guid.Empty)
        {
            GameTooltipManager.RemoveItemDetailTooltipModify(ActionTooltipModityGuid);
            ActionTooltipModityGuid = Guid.Empty;
        }

        if (!ModuleConfig.ShowActionID) return;

        var hoveredID = AgentActionDetail.Instance()->ActionId;
        var id = ModuleConfig is { ShowActionIDResolved: true, ShowActionIDOriginal: false } 
               ? hoveredID 
               : AgentActionDetail.Instance()->OriginalId;

        var payloads = new List<Payload>();
        var needNewLine = ModuleConfig is { ShowActionIDResolved: true, ShowActionIDOriginal: true } && id != hoveredID;

        payloads.Add(needNewLine ? new NewLinePayload() : new TextPayload("   "));
        payloads.Add(new UIForegroundPayload(3));
        payloads.Add(new TextPayload("["));
        payloads.Add(new TextPayload($"{id}"));

        if (ModuleConfig is { ShowActionIDResolved: true, ShowActionIDOriginal: true } && id != hoveredID)
            payloads.Add(new TextPayload($" → {hoveredID}"));

        payloads.Add(new TextPayload("]"));
        payloads.Add(new UIForegroundPayload(0));

        ActionTooltipModityGuid = GameTooltipManager.AddActionDetailTooltipModify(
            hoveredID,
            TooltipActionType.ActionKind,
            new SeString(payloads),
            TooltipModifyMode.Append);
    }

    private static void ModifyStatuTooltip(
        AtkTooltipManager*                manager,
        AtkTooltipManager.AtkTooltipType  type,
        ushort                            parentID,
        AtkResNode*                       targetNode,
        AtkTooltipManager.AtkTooltipArgs* args)
    {
        if (StatuTooltipModityGuid != Guid.Empty)
        {
            GameTooltipManager.RemoveItemDetailTooltipModify(StatuTooltipModityGuid);
            StatuTooltipModityGuid = Guid.Empty;
        }

        if (!ModuleConfig.ShowStatusID) return;

        if (DService.ObjectTable.LocalPlayer is not { } localPlayer || targetNode == null) return;

        var imageNode = targetNode->GetAsAtkImageNode();
        if (imageNode == null) return;

        var iconID = imageNode->PartsList->Parts[imageNode->PartId].UldAsset->AtkTexture.Resource->IconId;
        if (iconID is < 210000 or > 230000) return;

        var map = new Dictionary<uint, uint>();

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

        return;

        void AddStatuses(StatusManager sm) =>
            AddStatusToMap(sm, ref map);
    }

    private static void ModifyWeatherTooltip(
        AtkTooltipManager*                manager,
        AtkTooltipManager.AtkTooltipType  type,
        ushort                            parentID,
        AtkResNode*                       targetNode,
        AtkTooltipManager.AtkTooltipArgs* args)
    {
        if (WeatherTooltipModifyGuid != Guid.Empty)
        {
            GameTooltipManager.RemoveWeatherTooltipModify(WeatherTooltipModifyGuid);
            WeatherTooltipModifyGuid = Guid.Empty;
        }

        if (!ModuleConfig.ShowWeatherID) return;

        var weatherID = WeatherManager.Instance()->WeatherId;
        if (!LuminaGetter.TryGetRow<Weather>(weatherID, out var weather)) return;

        WeatherTooltipModifyGuid = GameTooltipManager.AddWeatherTooltipModify($"{weather.Name} [{weatherID}]");
    }
    
    private static void AddStatusToMap(StatusManager statusManager, ref Dictionary<uint, uint> map)
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

    protected override void Uninit()
    {
        ZoneInfoEntry?.Remove();
        ZoneInfoEntry = null;

        GameTooltipManager.Unreg(generateItemModifiers: ModifyItemTooltip);
        GameTooltipManager.Unreg(generateActionModifiers: ModifyActionTooltip);
        GameTooltipManager.Unreg(ModifyStatuTooltip);
        GameTooltipManager.Unreg(ModifyWeatherTooltip);

        GameTooltipManager.RemoveItemDetailTooltipModify(ItemTooltipModityGuid);
        GameTooltipManager.RemoveItemDetailTooltipModify(ActionTooltipModityGuid);
        GameTooltipManager.RemoveItemDetailTooltipModify(StatuTooltipModityGuid);
        GameTooltipManager.RemoveWeatherTooltipModify(WeatherTooltipModifyGuid);

        DService.AddonLifecycle.UnregisterListener(OnAddon);
        FrameworkManager.Unreg(OnUpdate);
    }
    
    public class Config : ModuleConfiguration
    {
        public bool ShowItemID = true;

        public bool ShowActionID         = true;
        public bool ShowActionIDResolved = true;
        public bool ShowActionIDOriginal = true;

        public bool ShowStatusID  = true;
        public bool ShowWeatherID = true;
        public bool ShowZoneInfo  = true;
        
        public bool ShowTargetID          = true;
        public bool ShowTargetIDBattleNPC = true;
        public bool ShowTargetIDCompanion = true;
        public bool ShowTargetIDEventNPC  = true;
        public bool ShowTargetIDOthers    = true;
    }
}
