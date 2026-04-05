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
    
    private static Config ModuleConfig = null!;

    private static IDtrBarEntry? ZoneInfoEntry;

    private static TooltipModification? ItemModification;
    private static TooltipModification? ActionModification;
    private static TooltipModification? StatusModification;
    private static TooltipModification? WeatherModification;

    protected override void Init()
    {
        ModuleConfig = Config.Load(this) ?? new();
        
        ZoneInfoEntry ??= DService.Instance().DTRBar.Get("AutoDisplayIDInfomation-ZoneInfo");

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

        ZoneInfoEntry?.Remove();
        ZoneInfoEntry = null;

        GameTooltipManager.Instance().Unreg(generateItemModifiers: ModifyItemTooltip);
        GameTooltipManager.Instance().Unreg(generateActionModifiers: ModifyActionTooltip);
        GameTooltipManager.Instance().Unreg(ModifyStatusTooltip);
        GameTooltipManager.Instance().Unreg(ModifyWeatherTooltip);

        GameTooltipManager.Instance().RemoveItemDetail(ItemModification);
        GameTooltipManager.Instance().RemoveItemDetail(ActionModification);
        GameTooltipManager.Instance().RemoveItemDetail(StatusModification);
        GameTooltipManager.Instance().RemoveWeather(WeatherModification);

        DService.Instance().AddonLifecycle.UnregisterListener(OnAddon);

    }

    protected override void ConfigUI()
    {
        if (ImGui.Checkbox($"{LuminaWrapper.GetAddonText(520)} ID", ref ModuleConfig.ShowItemID))
            ModuleConfig.Save(this);

        ImGui.NewLine();

        if (ImGui.Checkbox($"{LuminaWrapper.GetAddonText(1340)} ID", ref ModuleConfig.ShowActionID))
            ModuleConfig.Save(this);

        if (ModuleConfig.ShowActionID)
        {
            using (ImRaii.PushIndent())
            {
                if (ImGui.Checkbox(Lang.Get("Resolved"), ref ModuleConfig.ShowActionIDResolved))
                    ModuleConfig.Save(this);

                if (ImGui.Checkbox(Lang.Get("Original"), ref ModuleConfig.ShowActionIDOriginal))
                    ModuleConfig.Save(this);
            }
        }

        ImGui.NewLine();

        if (ImGui.Checkbox($"{LuminaWrapper.GetAddonText(1030)} ID", ref ModuleConfig.ShowTargetID))
            ModuleConfig.Save(this);

        if (ModuleConfig.ShowTargetID)
        {
            using (ImRaii.PushIndent())
            {
                if (ImGui.Checkbox("BattleNPC", ref ModuleConfig.ShowTargetIDBattleNPC))
                    ModuleConfig.Save(this);

                if (ImGui.Checkbox("EventNPC", ref ModuleConfig.ShowTargetIDEventNPC))
                    ModuleConfig.Save(this);

                if (ImGui.Checkbox("Companion", ref ModuleConfig.ShowTargetIDCompanion))
                    ModuleConfig.Save(this);

                if (ImGui.Checkbox(LuminaWrapper.GetAddonText(832), ref ModuleConfig.ShowTargetIDOthers))
                    ModuleConfig.Save(this);
            }
        }

        ImGui.NewLine();

        if (ImGui.Checkbox($"{Lang.Get("Status")} ID", ref ModuleConfig.ShowStatusID))
            ModuleConfig.Save(this);

        ImGui.NewLine();

        if (ImGui.Checkbox($"{LuminaWrapper.GetAddonText(8555)} ID", ref ModuleConfig.ShowWeatherID))
            ModuleConfig.Save(this);

        ImGui.NewLine();

        if (ImGui.Checkbox($"{LuminaWrapper.GetAddonText(870)}", ref ModuleConfig.ShowZoneInfo))
            ModuleConfig.Save(this);
    }

    private static void OnMapChanged(uint obj) =>
        UpdateDTRInfo();

    private static void OnZoneChanged(ushort obj) =>
        UpdateDTRInfo();

    private static void OnAddon(AddonEvent type, AddonArgs args)
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
                    ObjectKind.BattleNpc => ModuleConfig.ShowTargetIDBattleNPC,
                    ObjectKind.EventNpc  => ModuleConfig.ShowTargetIDEventNPC,
                    ObjectKind.Companion => ModuleConfig.ShowTargetIDCompanion,
                    _                    => ModuleConfig.ShowTargetIDOthers
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
        if (ItemModification != null)
        {
            GameTooltipManager.Instance().RemoveItemDetail(ItemModification);
            ItemModification = null;
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

        ItemModification = GameTooltipManager.Instance().AddItemDetail
        (
            itemID,
            TooltipItemType.ItemUICategory,
            new SeString(payloads),
            TooltipModifyMode.Append
        );
    }

    private static void ModifyActionTooltip(AtkUnitBase* addonActionDetail, NumberArrayData* numberArrayData, StringArrayData* stringArrayData)
    {
        if (ActionModification != null)
        {
            GameTooltipManager.Instance().RemoveItemDetail(ActionModification);
            ActionModification = null;
        }

        if (!ModuleConfig.ShowActionID) return;

        var hoveredID = AgentActionDetail.Instance()->ActionId;
        var id = ModuleConfig is { ShowActionIDResolved: true, ShowActionIDOriginal: false }
                     ? hoveredID
                     : AgentActionDetail.Instance()->OriginalId;

        var payloads    = new List<Payload>();
        var needNewLine = ModuleConfig is { ShowActionIDResolved: true, ShowActionIDOriginal: true } && id != hoveredID;

        payloads.Add(needNewLine ? new NewLinePayload() : new TextPayload("   "));
        payloads.Add(new UIForegroundPayload(3));
        payloads.Add(new TextPayload("["));
        payloads.Add(new TextPayload($"{id}"));

        if (ModuleConfig is { ShowActionIDResolved: true, ShowActionIDOriginal: true } && id != hoveredID)
            payloads.Add(new TextPayload($" → {hoveredID}"));

        payloads.Add(new TextPayload("]"));
        payloads.Add(new UIForegroundPayload(0));

        ActionModification = GameTooltipManager.Instance().AddActionDetail
        (
            hoveredID,
            TooltipActionType.ActionKind,
            new SeString(payloads),
            TooltipModifyMode.Append
        );
    }

    private static void ModifyStatusTooltip
    (
        AtkTooltipManager*                manager,
        AtkTooltipManager.AtkTooltipType  type,
        ushort                            parentID,
        AtkResNode*                       targetNode,
        AtkTooltipManager.AtkTooltipArgs* args
    )
    {
        if (StatusModification != null)
        {
            GameTooltipManager.Instance().RemoveItemDetail(StatusModification);
            StatusModification = null;
        }

        if (!ModuleConfig.ShowStatusID) return;

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

        StatusModification = GameTooltipManager.Instance().AddStatus(statuID, $"  [{statuID}]", TooltipModifyMode.Regex, @"^(.*?)(?=\(|（|\n|$)");

        return;

        void AddStatuses(StatusManager sm)
        {
            AddStatusToMap(sm, ref map);
        }
    }

    private static void ModifyWeatherTooltip
    (
        AtkTooltipManager*                manager,
        AtkTooltipManager.AtkTooltipType  type,
        ushort                            parentID,
        AtkResNode*                       targetNode,
        AtkTooltipManager.AtkTooltipArgs* args
    )
    {
        if (WeatherModification != null)
        {
            GameTooltipManager.Instance().RemoveWeather(WeatherModification);
            WeatherModification = null;
        }

        if (!ModuleConfig.ShowWeatherID) return;

        var weatherID = WeatherManager.Instance()->WeatherId;
        if (!LuminaGetter.TryGetRow<Weather>(weatherID, out var weather)) return;

        WeatherModification = GameTooltipManager.Instance().AddWeatherTooltipModify($"{weather.Name} [{weatherID}]");
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

    private static void UpdateDTRInfo()
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

    public class Config : ModuleConfig
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
