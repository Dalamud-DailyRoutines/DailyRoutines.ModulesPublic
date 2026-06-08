using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.Gui.Dtr;
using Dalamud.Hooking;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.Enums;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using FFXIVClientStructs.Interop;
using InteropGenerator.Runtime;
using Lumina.Excel.Sheets;
using OmenTools.Interop.Game.Lumina;
using OmenTools.Interop.Game.Models;
using OmenTools.OmenService;

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

    private static readonly CompSig                             GetStatusTooltipTextSig = new("40 55 41 54 41 55 41 56 41 57 48 8D 6C 24 90 48 81 EC 70 01 00 00");
    private delegate        CStringPointer                      GetStatusTooltipTextDelegate(AgentHUD* agent, Utf8String* output, uint statusID, uint param);
    private                 Hook<GetStatusTooltipTextDelegate>? GetStatusTooltipTextHook;
    
    private Config        config = null!;
    private IDtrBarEntry? zoneInfoEntry;

    private AtkEventWrapper? naviMapMouseOverEvent;
    private AtkEventWrapper? naviMapMouseOutEvent;

    protected override void Init()
    {
        config = Config.Load(this) ?? new();

        zoneInfoEntry ??= DService.Instance().DTRBar.Get("AutoDisplayIDInfomation-ZoneInfo");

        TooltipManager.Instance().RegItem(OnItemTooltip);
        TooltipManager.Instance().RegAction(OnActionTooltip);

        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PreRequestedUpdate, "_TargetInfo",           OnAddonTarget);
        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PreRequestedUpdate, "_TargetInfoMainTarget", OnAddonTarget);

        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PostDraw,    "_NaviMap", OnAddonNaviMap);
        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "_NaviMap", OnAddonNaviMap);

        GetStatusTooltipTextHook ??= GetStatusTooltipTextSig.GetHook<GetStatusTooltipTextDelegate>(GetStatusTooltipTextDetour);
        GetStatusTooltipTextHook.Enable();

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

        TooltipManager.Instance().Unreg(OnItemTooltip);
        TooltipManager.Instance().Unreg(OnActionTooltip);

        DService.Instance().AddonLifecycle.UnregisterListener(OnAddonTarget, OnAddonNaviMap);
        OnAddonNaviMap(AddonEvent.PreFinalize, null);
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

    private void OnAddonNaviMap(AddonEvent type, AddonArgs args)
    {
        switch (type)
        {
            case AddonEvent.PreFinalize:
                naviMapMouseOverEvent?.Dispose();
                naviMapMouseOverEvent = null;

                naviMapMouseOutEvent?.Dispose();
                naviMapMouseOutEvent = null;
                break;

            case AddonEvent.PostDraw:
                if (NaviMap == null) return;
                if (naviMapMouseOutEvent != null && naviMapMouseOverEvent != null) return;

                var component = NaviMap->GetComponentByNodeId(14);
                if (component == null) return;

                var collisionNode = component->UldManager.SearchNodeById(5);
                if (collisionNode == null) return;

                collisionNode->ClearEvents();

                naviMapMouseOverEvent = new
                ((_, addon, _, _) =>
                    {
                        var id = WeatherManager.Instance()->WeatherId;
                        if (!LuminaGetter.TryGetRow<Weather>(id, out var weather)) return;

                        using var stringBuilder = new RentedSeStringBuilder();
                        using var stringBuffer  = new RentedAtkValues(1);
                        stringBuffer[0].SetManagedString(stringBuilder.Builder.Append($"{weather.Name} [{weather.RowId}]").GetViewAsSpan());

                        var tooltipArgs = new AtkTooltipManager.AtkTooltipArgs();
                        tooltipArgs.TextArgs.AtkArrayType = 0;
                        tooltipArgs.TextArgs.Text         = stringBuffer[0].String;

                        AtkStage.Instance()->TooltipManager.ShowTooltip(AtkTooltipType.Text, addon->Id, collisionNode, &tooltipArgs);
                    }
                );
                naviMapMouseOverEvent.Add(NaviMap, collisionNode, AtkEventType.MouseOver);

                naviMapMouseOutEvent = new((_, addon, _, _) => AtkStage.Instance()->TooltipManager.HideTooltip(addon->Id));
                naviMapMouseOutEvent.Add(NaviMap, collisionNode, AtkEventType.MouseOut);
                break;
        }
    }

    private void OnAddonTarget(AddonEvent type, AddonArgs args)
    {
        if (!config.ShowTargetID) return;

        if (TargetManager.Target is not { } target) return;

        var id = target.DataID;
        if (id == 0) return;

        var show = target.ObjectKind switch
        {
            ObjectKind.BattleNpc => config.ShowTargetIDBattleNPC,
            ObjectKind.EventNpc  => config.ShowTargetIDEventNPC,
            ObjectKind.Companion => config.ShowTargetIDCompanion,
            _                    => config.ShowTargetIDOthers
        };
        if (!show) return;

        var stringArray = AtkStage.Instance()->GetStringArrayData(StringArrayType.Hud2);
        if (stringArray == null) return;

        using var utf8String = new Utf8String($"{target.Name} [{target.DataID}]");
        stringArray->SetValue(0, $"{target.Name} [{target.DataID}]");
    }

    private void OnItemTooltip(ItemKind itemKind, uint itemID, ref List<TooltipItemModification> modifications)
    {
        if (!config.ShowItemID) return;

        using var builder = new RentedSeStringBuilder();

        builder.Builder
               .PushColorType(3)
               .Append(" [")
               .Append(itemID)
               .Append(']')
               .PopColorType();

        modifications.Add
        (
            new()
            {
                Target = TooltipItemType.UICategory,
                Type   = TooltipModificationType.Append,
                Text   = builder.Builder.ToReadOnlySeString()
            }
        );
    }

    private void OnActionTooltip(DetailKind actionKind, uint actionID, ref List<TooltipActionModification> modifications)
    {
        if (!config.ShowActionID) return;

        using var builder          = new RentedSeStringBuilder();
        var       originalActionID = AgentActionDetail.Instance()->OriginalId;
        var id = config is { ShowActionIDResolved: true, ShowActionIDOriginal: false }
                     ? actionID
                     : originalActionID;

        builder.Builder
               .PushColorType(3)
               .Append(" [")
               .Append(id);

        if (config is { ShowActionIDResolved: true, ShowActionIDOriginal: true } && id != actionID)
            builder.Builder.Append($" → {actionID}");

        builder.Builder
               .Append("]")
               .PopColorType();

        modifications.Add
        (
            new()
            {
                Target = TooltipActionType.Category,
                Type   = TooltipModificationType.Append,
                Text   = builder.Builder.ToReadOnlySeString()
            }
        );
    }

    private CStringPointer GetStatusTooltipTextDetour(AgentHUD* agent, Utf8String* output, uint statusID, uint param)
    {
        var orig = GetStatusTooltipTextHook.Original(agent, output, statusID, param);

        if (!config.ShowStatusID || statusID == 0 || statusID == 0xFFFFFFFF || !orig.HasValue)
            return orig;

        var originalText = orig.ToString();
        if (string.IsNullOrEmpty(originalText))
            return orig;

        var newlineIndex = originalText.IndexOf('\n');
        var modifiedText = newlineIndex < 0 ? $"{originalText} [{statusID}]" : $"{originalText[..newlineIndex]} [{statusID}]{originalText[newlineIndex..]}";

        using var utf8String = new Utf8String(modifiedText);
        output->Copy(&utf8String);
        return utf8String.StringPtr;
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

        public bool ShowTargetID          = true;
        public bool ShowTargetIDBattleNPC = true;
        public bool ShowTargetIDCompanion = true;
        public bool ShowTargetIDEventNPC  = true;
        public bool ShowTargetIDOthers    = true;
        public bool ShowStatusID          = true;
        public bool ShowWeatherID         = true;
        public bool ShowZoneInfo          = true;
    }
}
