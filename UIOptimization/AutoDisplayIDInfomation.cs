using System.Text.RegularExpressions;
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
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using OmenTools.Interop.Game.Lumina;
using OmenTools.OmenService;
using OmenTools.Threading;

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

    protected override void Init()
    {
        config = Config.Load(this) ?? new();
        
        zoneInfoEntry ??= DService.Instance().DTRBar.Get("AutoDisplayIDInfomation-ZoneInfo");

        GameTooltipManager.Instance().RegItemTooltip(ModifyItemTooltip);
        GameTooltipManager.Instance().RegActionTooltip(ModifyActionTooltip);
        GameTooltipManager.Instance().RegTooltipShow(ModifyStatusTooltip);
        GameTooltipManager.Instance().RegTooltipShow(ModifyWeatherTooltip);

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

        GameTooltipManager.Instance().Unreg(ModifyItemTooltip);
        GameTooltipManager.Instance().Unreg(ModifyActionTooltip);
        GameTooltipManager.Instance().Unreg(ModifyStatusTooltip);
        GameTooltipManager.Instance().Unreg(ModifyWeatherTooltip);

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

    private void ModifyItemTooltip(ItemTooltipContext context)
    {
        if (!config.ShowItemID) return;

        var payloads = new List<Payload>
        {
            new UIForegroundPayload(3),
            new TextPayload("   ["),
            new TextPayload($"{context.ItemID}"),
            new TextPayload("]"),
            new UIForegroundPayload(0)
        };

        context.Append(TooltipItemType.ItemUICategory, new SeString(payloads));
    }

    private void ModifyActionTooltip(ActionTooltipContext context)
    {
        if (!config.ShowActionID) return;

        var id = config is { ShowActionIDResolved: true, ShowActionIDOriginal: false }
                     ? context.ActionID
                     : context.OriginalActionID;

        var payloads    = new List<Payload>();
        var needNewLine = config is { ShowActionIDResolved: true, ShowActionIDOriginal: true } && id != context.ActionID;

        payloads.Add(needNewLine ? new NewLinePayload() : new TextPayload("   "));
        payloads.Add(new UIForegroundPayload(3));
        payloads.Add(new TextPayload("["));
        payloads.Add(new TextPayload($"{id}"));

        if (config is { ShowActionIDResolved: true, ShowActionIDOriginal: true } && id != context.ActionID)
            payloads.Add(new TextPayload($" → {context.ActionID}"));

        payloads.Add(new TextPayload("]"));
        payloads.Add(new UIForegroundPayload(0));

        context.Append(TooltipActionType.ActionKind, new SeString(payloads));
    }

    private void ModifyStatusTooltip(TooltipShowContext context)
    {
        if (!config.ShowStatusID) return;
        if (!context.TryGetStatusID(out var statusID)) return;

        context.ReplaceText(current => new SeString().Append(Regex.Replace(current.TextValue, @"^(.*?)(?=\(|（|\n|$)", $"$1  [{statusID}]")));
    }

    private void ModifyWeatherTooltip(TooltipShowContext context)
    {
        if (!config.ShowWeatherID) return;
        if (!context.TryGetWeather(out var weatherID, out var weather)) return;

        context.SetText($"{weather.Name} [{weatherID}]");
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
