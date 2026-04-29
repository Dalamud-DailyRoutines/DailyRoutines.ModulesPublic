using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
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
using Lumina.Excel.Sheets;
using OmenTools.Interop.Game.Lumina;
using OmenTools.OmenService;
using OmenTools.Threading;
using RowStatus = Lumina.Excel.Sheets.Status;

namespace DailyRoutines.ModulesPublic;

public unsafe class AutoDisplayIDInfomation : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoDisplayIDInfomationTitle"),
        Description = Lang.Get("AutoDisplayIDInfomationDescription"),
        Category    = ModuleCategory.UIOptimization,
        Author      = ["Middo"]
    };
    
    private Config config = null!;
    private IDtrBarEntry? zoneInfoEntry;

    private TooltipModification? itemModification;
    private TooltipModification? actionModification;
    private TooltipModification? statusModification;
    private TooltipModification? weatherModification;

    protected override void Init()
    {
        config = Config.Load(this) ?? new();
        
        zoneInfoEntry ??= DService.Instance().DTRBar.Get("AutoDisplayIDInfomation-ZoneInfo");

        GameTooltipManager.Instance().RegGenerateItemTooltipModifier(ModifyItemTooltip);
        GameTooltipManager.Instance().RegGenerateActionTooltipModifier(ModifyActionTooltip);
        GameTooltipManager.Instance().RegTooltipShowModifier(ModifyStatusTooltip);
        GameTooltipManager.Instance().RegTooltipShowModifier(ModifyWeatherTooltip);

        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PostDraw, "ActionDetail",          OnAddon);
        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PostDraw, "ItemDetail",            OnAddon);
        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PreDraw,  "_TargetInfo",           OnAddon);
        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PreDraw,  "_TargetInfoMainTarget", OnAddon);

        DService.Instance().ClientState.MapIdChanged     += OnMapChanged;
        DService.Instance().ClientState.TerritoryChanged += OnZoneChanged;
        
        UpdateDTRInfo();
    }

    protected override void Uninit()
    {
        DService.Instance().ClientState.MapIdChanged     -= OnMapChanged;
        DService.Instance().ClientState.TerritoryChanged -= OnZoneChanged;

        zoneInfoEntry?.Remove();
        zoneInfoEntry = null;

        GameTooltipManager.Instance().Unreg(generateItemModifiers: ModifyItemTooltip);
        GameTooltipManager.Instance().Unreg(generateActionModifiers: ModifyActionTooltip);
        GameTooltipManager.Instance().Unreg(ModifyStatusTooltip);
        GameTooltipManager.Instance().Unreg(ModifyWeatherTooltip);

        GameTooltipManager.Instance().RemoveItemDetail(itemModification);
        GameTooltipManager.Instance().RemoveItemDetail(actionModification);
        GameTooltipManager.Instance().RemoveItemDetail(statusModification);
        GameTooltipManager.Instance().RemoveWeather(weatherModification);

        DService.Instance().AddonLifecycle.UnregisterListener(OnAddon);

    }

    protected override void ConfigUI()
    {
        if (ImGui.Checkbox($"{LuminaWrapper.GetAddonText(520)} ID", ref config.ShowItemID))
            config.Save(this);

        ImGui.NewLine();

        if (ImGui.Checkbox($"{LuminaWrapper.GetAddonText(1340)} ID", ref config.ShowActionID))
            config.Save(this);

        if (config.ShowActionID)
        {
            using (ImRaii.PushIndent())
            {
                if (ImGui.Checkbox(Lang.Get("Resolved"), ref config.ShowActionIDResolved))
                    config.Save(this);

                if (ImGui.Checkbox(Lang.Get("Original"), ref config.ShowActionIDOriginal))
                    config.Save(this);
            }
        }

        ImGui.NewLine();

        if (ImGui.Checkbox($"{LuminaWrapper.GetAddonText(1030)} ID", ref config.ShowTargetID))
            config.Save(this);

        if (config.ShowTargetID)
        {
            using (ImRaii.PushIndent())
            {
                if (ImGui.Checkbox("BattleNPC", ref config.ShowTargetIDBattleNPC))
                    config.Save(this);

                if (ImGui.Checkbox("EventNPC", ref config.ShowTargetIDEventNPC))
                    config.Save(this);

                if (ImGui.Checkbox("Companion", ref config.ShowTargetIDCompanion))
                    config.Save(this);

                if (ImGui.Checkbox(LuminaWrapper.GetAddonText(832), ref config.ShowTargetIDOthers))
                    config.Save(this);
            }
        }

        ImGui.NewLine();

        if (ImGui.Checkbox($"{Lang.Get("Status")} ID", ref config.ShowStatusID))
            config.Save(this);

        ImGui.NewLine();

        if (ImGui.Checkbox($"{LuminaWrapper.GetAddonText(8555)} ID", ref config.ShowWeatherID))
            config.Save(this);

        ImGui.NewLine();

        if (ImGui.Checkbox($"{LuminaWrapper.GetAddonText(870)}", ref config.ShowZoneInfo))
            config.Save(this);
    }

    private void OnMapChanged(uint obj) =>
        UpdateDTRInfo();

    private void OnZoneChanged(uint u) =>
        UpdateDTRInfo();

    private void OnAddon(AddonEvent type, AddonArgs args)
    {
        if (!Throttler.Shared.Throttle("AutoDisplayIDInfomation-OnAddon", 50)) return;

        switch (args.AddonName)
        {
            case "ActionDetail":
                if (ActionDetail == null) return;

                var actionTextNode = ActionDetail->GetTextNodeById(6);
                if (actionTextNode == null) return;

                actionTextNode->TextFlags |= TextFlags.MultiLine;
                actionTextNode->FontSize  =  (byte)(actionTextNode->NodeText.StringPtr.ToString().Contains('\n') ? 10 : 12);
                break;

            case "ItemDetail":
                if (ItemDetail == null) return;

                var itemTextnode = ItemDetail->GetTextNodeById(35);
                if (itemTextnode == null) return;

                itemTextnode->TextFlags |= TextFlags.MultiLine;
                break;

            case "_TargetInfoMainTarget" or "_TargetInfo":
                if (TargetManager.Target is not { } target) return;

                var id = target.DataID;
                if (id == 0) return;

                var name = AtkStage.Instance()->GetStringArrayData(StringArrayType.Hud2)->StringArray->ExtractText();
                var show = target.ObjectKind switch
                {
                    ObjectKind.BattleNpc => config.ShowTargetIDBattleNPC,
                    ObjectKind.EventNpc  => config.ShowTargetIDEventNPC,
                    ObjectKind.Companion => config.ShowTargetIDCompanion,
                    _                    => config.ShowTargetIDOthers
                };

                if (!show || !config.ShowTargetID)
                {
                    AtkStage.Instance()->GetStringArrayData(StringArrayType.Hud2)->SetValueAndUpdate(0, name.Replace($"  [{id}]", string.Empty));
                    return;
                }

                if (!name.Contains($"[{id}]"))
                    AtkStage.Instance()->GetStringArrayData(StringArrayType.Hud2)->SetValueAndUpdate(0, $"{name}  [{id}]");
                break;
        }
    }

    private void ModifyItemTooltip(AtkUnitBase* addonItemDetail, NumberArrayData* numberArrayData, StringArrayData* stringArrayData)
    {
        if (itemModification != null)
        {
            GameTooltipManager.Instance().RemoveItemDetail(itemModification);
            itemModification = null;
        }

        if (!config.ShowItemID) return;

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

        itemModification = GameTooltipManager.Instance().AddItemDetail
        (
            itemID,
            TooltipItemType.ItemUICategory,
            new SeString(payloads),
            TooltipModifyMode.Append
        );
    }

    private void ModifyActionTooltip(AtkUnitBase* addonActionDetail, NumberArrayData* numberArrayData, StringArrayData* stringArrayData)
    {
        if (actionModification != null)
        {
            GameTooltipManager.Instance().RemoveItemDetail(actionModification);
            actionModification = null;
        }

        if (!config.ShowActionID) return;

        var hoveredID = AgentActionDetail.Instance()->ActionId;
        var id = config is { ShowActionIDResolved: true, ShowActionIDOriginal: false }
                     ? hoveredID
                     : AgentActionDetail.Instance()->OriginalId;

        var payloads    = new List<Payload>();
        var needNewLine = config is { ShowActionIDResolved: true, ShowActionIDOriginal: true } && id != hoveredID;

        payloads.Add(needNewLine ? new NewLinePayload() : new TextPayload("   "));
        payloads.Add(new UIForegroundPayload(3));
        payloads.Add(new TextPayload("["));
        payloads.Add(new TextPayload($"{id}"));

        if (config is { ShowActionIDResolved: true, ShowActionIDOriginal: true } && id != hoveredID)
            payloads.Add(new TextPayload($" → {hoveredID}"));

        payloads.Add(new TextPayload("]"));
        payloads.Add(new UIForegroundPayload(0));

        actionModification = GameTooltipManager.Instance().AddActionDetail
        (
            hoveredID,
            TooltipActionType.ActionKind,
            new SeString(payloads),
            TooltipModifyMode.Append
        );
    }

    private void ModifyStatusTooltip
    (
        AtkTooltipManager*                manager,
        AtkTooltipType  type,
        ushort                            parentID,
        AtkResNode*                       targetNode,
        AtkTooltipManager.AtkTooltipArgs* args
    )
    {
        if (statusModification != null)
        {
            GameTooltipManager.Instance().RemoveItemDetail(statusModification);
            statusModification = null;
        }

        if (!config.ShowStatusID) return;

        if (DService.Instance().ObjectTable.LocalPlayer is not { } localPlayer || targetNode == null) return;

        var imageNode = targetNode->GetAsAtkImageNode();
        if (imageNode == null) return;

        var iconID = imageNode->PartsList->Parts[imageNode->PartId].UldAsset->AtkTexture.Resource->IconId;
        if (iconID is < 210000 or > 230000) return;

        var map = new Dictionary<uint, uint>();

        if (TargetManager.Target is { } target && target.Address != localPlayer.Address)
            AddStatuses(target.ToBCStruct()->StatusManager);

        if (TargetManager.FocusTarget is { } focus)
            AddStatuses(focus.ToBCStruct()->StatusManager);

        foreach (var member in AgentHUD.Instance()->PartyMembers.ToArray().Where(m => m.Index != 0))
        {
            if (member.Object != null)
                AddStatuses(member.Object->StatusManager);
        }

        AddStatuses(localPlayer.ToBCStruct()->StatusManager);

        if (!map.TryGetValue(iconID, out var statuID) || statuID == 0) return;

        statusModification = GameTooltipManager.Instance().AddStatus(statuID, $"  [{statuID}]", TooltipModifyMode.Regex, @"^(.*?)(?=\(|（|\n|$)");

        return;

        void AddStatuses(StatusManager sm)
        {
            AddStatusToMap(sm, ref map);
        }
    }

    private void ModifyWeatherTooltip
    (
        AtkTooltipManager*                manager,
        AtkTooltipType  type,
        ushort                            parentID,
        AtkResNode*                       targetNode,
        AtkTooltipManager.AtkTooltipArgs* args
    )
    {
        if (weatherModification != null)
        {
            GameTooltipManager.Instance().RemoveWeather(weatherModification);
            weatherModification = null;
        }

        if (!config.ShowWeatherID) return;

        var weatherID = WeatherManager.Instance()->WeatherId;
        if (!LuminaGetter.TryGetRow<Weather>(weatherID, out var weather)) return;

        weatherModification = GameTooltipManager.Instance().AddWeatherTooltipModify($"{weather.Name} [{weatherID}]");
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

    private void UpdateDTRInfo()
    {
        if (config.ShowZoneInfo)
        {
            var mapID  = GameState.Map;
            var zoneID = GameState.TerritoryType;

            if (mapID == 0 || zoneID == 0)
            {
                zoneInfoEntry.Shown = false;
                return;
            }

            zoneInfoEntry.Shown = true;

            zoneInfoEntry.Text = $"{LuminaWrapper.GetAddonText(870)}: {zoneID} / {LuminaWrapper.GetAddonText(670)}: {mapID}";
        }
        else
            zoneInfoEntry.Shown = false;
    }

    private class Config : ModuleConfig
    {
        public bool ShowActionID         = true;
        public bool ShowActionIDOriginal = true;
        public bool ShowActionIDResolved = true;
        public bool ShowItemID           = true;

        public bool ShowStatusID = true;

        public bool ShowTargetID          = true;
        public bool ShowTargetIDBattleNPC = true;
        public bool ShowTargetIDCompanion = true;
        public bool ShowTargetIDEventNPC  = true;
        public bool ShowTargetIDOthers    = true;
        public bool ShowWeatherID         = true;
        public bool ShowZoneInfo          = true;
    }
}
