using System.Linq;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.Gui.Dtr;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Memory;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.Enums;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using OmenTools.Interop.Game.Helpers;
using OmenTools.Interop.Game.Lumina;
using OmenTools.Interop.Game.Models;
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

    protected override void Init()
    {
        config = Config.Load(this) ?? new();

        zoneInfoEntry ??= DService.Instance().DTRBar.Get("AutoDisplayIDInfomation-ZoneInfo");

        TooltipManager.Instance().RegItem(OnItemTooltip);
        TooltipManager.Instance().RegAction(OnActionTooltip);

        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PostDraw, "ActionDetail",          OnAddon);
        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PostDraw, "ItemDetail",            OnAddon);
        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PreDraw,  "_TargetInfo",           OnAddon);
        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PreDraw,  "_TargetInfoMainTarget", OnAddon);
        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PreDraw,  "Tooltip",               OnAddon);

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

            case "Tooltip":
                if (!config.ShowStatusID && !config.ShowWeatherID) return;

                var addon = AddonHelper.GetByName("Tooltip");
                if (addon == null || addon->RootNode == null || addon->RootNode->ChildNode == null) return;

                var textNode = addon->GetTextNodeById(2);
                if (textNode == null || textNode->AtkResNode.Type != NodeType.Text) return;

                var textValue = MemoryHelper.ReadSeStringNullTerminated((nint)textNode->NodeText.StringPtr.Value).TextValue;
                if (string.IsNullOrEmpty(textValue)) return;
                if (textValue.Contains('[') && textValue.Contains(']')) return;

                SeString? newText = null;

                if (config.ShowStatusID && TryAppendStatusID(textValue, out var newStatusText))
                    newText = newStatusText;
                else if (config.ShowWeatherID)
                {
                    var weatherID = WeatherManager.Instance()->WeatherId;
                    if (LuminaGetter.TryGetRow<Weather>(weatherID, out var weather) &&
                        textValue.Equals(weather.Name.ToString(), StringComparison.OrdinalIgnoreCase))
                        newText = new SeStringBuilder().Append($"{weather.Name} [{weatherID}]").Build();
                }

                if (newText == null) return;

                textNode->SetText(newText.EncodeWithNullTerminator());

                ushort textW = 0, textH = 0;
                fixed (byte* ptr = newText.EncodeWithNullTerminator())
                    textNode->GetTextDrawSize(&textW, &textH, ptr);

                var newWidth  = (ushort)(textW + 16);
                var newHeight = (ushort)(textH + 10);

                textNode->AtkResNode.SetWidth(newWidth);
                textNode->AtkResNode.SetHeight(newHeight);

                var bgNode = addon->GetNodeById(1);
                if (bgNode != null)
                {
                    bgNode->SetWidth(newWidth);
                    bgNode->SetHeight(newHeight);
                }

                addon->RootNode->SetWidth(newWidth);
                addon->RootNode->SetHeight(newHeight);
                addon->SetSize(newWidth, newHeight);
                break;
        }
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

        using var builder = new RentedSeStringBuilder();
        var originalActionID = AgentActionDetail.Instance()->OriginalId;
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

    private bool TryAppendStatusID(string textValue, out SeString result)
    {
        result = default!;
        if (DService.Instance().ObjectTable.LocalPlayer is not { } localPlayer) return false;

        var nameMap = new Dictionary<string, List<(uint ID, string Desc)>>(StringComparer.OrdinalIgnoreCase);
        void AddStatuses(ref StatusManager sm)
        {
            foreach (var s in sm.Status)
            {
                if (s.StatusId == 0) continue;
                if (!LuminaGetter.TryGetRow<RowStatus>(s.StatusId, out var row)) continue;
                var name = row.Name.ToString();
                if (!nameMap.TryGetValue(name, out var list))
                    nameMap[name] = list = [];
                list.Add((row.RowId, row.Description.ToString()));
            }
        }
        AddStatuses(ref localPlayer.ToBCStruct()->StatusManager);
        if (TargetManager.Target is { } target && target.Address != localPlayer.Address)
            AddStatuses(ref target.ToBCStruct()->StatusManager);
        if (TargetManager.FocusTarget is { } focus)
            AddStatuses(ref focus.ToBCStruct()->StatusManager);
        foreach (var member in AgentHUD.Instance()->PartyMembers.ToArray().Where(m => m.Index != 0))
        {
            if (member.Object != null) AddStatuses(ref member.Object->StatusManager);
        }

        foreach (var (name, entries) in nameMap)
        {
            if (!textValue.StartsWith(name, StringComparison.OrdinalIgnoreCase)) continue;

            List<uint> candidateIDs;
            if (entries.Select(e => e.Desc).Distinct().Count() > 1)
            {
                var normalizedText = textValue.Replace("\r", "").Replace("\n", "").Replace("<br>", "");
                candidateIDs = entries
                    .Where(e =>
                    {
                        var normalizedDesc = e.Desc.Replace("\r", "").Replace("\n", "").Replace("<br>", "");
                        return normalizedText.Contains(normalizedDesc, StringComparison.OrdinalIgnoreCase);
                    })
                    .Select(e => e.ID)
                    .ToList();
                if (candidateIDs.Count == 0)
                    candidateIDs = entries.Select(e => e.ID).ToList();
            }
            else
                candidateIDs = entries.Select(e => e.ID).ToList();

            var newIDs = candidateIDs.Where(id => !textValue.Contains($"[{id}]")).ToList();
            if (newIDs.Count == 0) return false;

            var nameEnd   = textValue.IndexOfAny(['\uff08', '（', '\r', '\n']);
            var firstLine = nameEnd > 0 ? textValue[..nameEnd] : textValue;
            var rest      = nameEnd > 0 ? textValue[nameEnd..] : string.Empty;

            var sb = new SeStringBuilder().Append(firstLine);
            foreach (var id in newIDs)
                sb.Append($"  [{id}]");
            sb.Append(rest);

            result = sb.Build();
            return true;
        }

        return false;
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
