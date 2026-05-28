using System.Runtime.InteropServices;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.Gui.Dtr;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Hooking;
using Dalamud.Memory;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.Enums;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using InteropGenerator.Runtime;
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

    private static readonly CompSig AtkTextNodeSetTextSig = new("48 85 C9 0F 84 ?? ?? ?? ?? 4C 8B DC 53 56");

    private delegate void AtkTextNodeSetTextDelegate(AtkTextNode* node, CStringPointer text);

    private Hook<AtkTextNodeSetTextDelegate>? setTextHook;
    private AtkResNode* tooltipRoot;

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

        DService.Instance().ClientState.MapIdChanged     += OnMapChanged;
        DService.Instance().ClientState.TerritoryChanged += OnZoneChanged;

        UpdateDTRInfo();

        tooltipRoot = AddonHelper.GetByName("Tooltip")->RootNode;
        setTextHook ??= AtkTextNodeSetTextSig.GetHook<AtkTextNodeSetTextDelegate>(SetTextDetour);
        setTextHook.Enable();
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

    private static AtkResNode* GetAncestorRoot(AtkResNode* node)
    {
        while (node != null && node->ParentNode != null)
            node = node->ParentNode;
        return node;
    }

    private void SetTextDetour(AtkTextNode* node, CStringPointer text)
    {
        if (!config.ShowStatusID && !config.ShowWeatherID)
        {
            setTextHook!.Original(node, text);
            return;
        }

        if (tooltipRoot == null || GetAncestorRoot((AtkResNode*)node) != tooltipRoot)
        {
            setTextHook!.Original(node, text);
            return;
        }

        var currentText = MemoryHelper.ReadSeStringNullTerminated((nint)text.Value);
        var textValue   = currentText.TextValue;

        if (textValue.Contains('[') && textValue.Contains(']'))
        {
            setTextHook!.Original(node, text);
            return;
        }

        if (config.ShowStatusID && TryAppendStatusID(textValue, out var newStatusText))
        {
            var bytes = newStatusText.EncodeWithNullTerminator();
            var ptr   = (byte*)Marshal.AllocHGlobal(bytes.Length);
            for (var i = 0; i < bytes.Length; i++) ptr[i] = bytes[i];
            setTextHook!.Original(node, new CStringPointer(ptr));
            return;
        }

        if (config.ShowWeatherID)
        {
            var weatherID = WeatherManager.Instance()->WeatherId;
            if (LuminaGetter.TryGetRow<Weather>(weatherID, out var weather) &&
                textValue.Equals(weather.Name.ToString(), StringComparison.OrdinalIgnoreCase) &&
                !textValue.Contains($"[{weatherID}]"))
            {
                var finalText = new SeStringBuilder().Append($"{weather.Name} [{weatherID}]").Build();
                var bytes     = finalText.EncodeWithNullTerminator();
                var ptr       = (byte*)Marshal.AllocHGlobal(bytes.Length);
                for (var i = 0; i < bytes.Length; i++) ptr[i] = bytes[i];
                setTextHook!.Original(node, new CStringPointer(ptr));
                return;
            }
        }

        setTextHook!.Original(node, text);
    }

    private bool TryAppendStatusID(string textValue, out SeString result)
    {
        result = default!;
        if (DService.Instance().ObjectTable.LocalPlayer is not { } localPlayer) return false;

        var nameMap = new Dictionary<string, (uint ID, string Desc)>(StringComparer.OrdinalIgnoreCase);
        void AddStatuses(ref StatusManager sm)
        {
            foreach (var s in sm.Status)
            {
                if (s.StatusId == 0) continue;
                if (!LuminaGetter.TryGetRow<RowStatus>(s.StatusId, out var row)) continue;
                nameMap.TryAdd(row.Name.ToString(), (row.RowId, row.Description.ToString()));
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

        foreach (var (name, (statusID, desc)) in nameMap)
        {
            if (!textValue.StartsWith(name, StringComparison.OrdinalIgnoreCase)) continue;
            if (textValue.Contains($"[{statusID}]")) continue;

            var nameEnd = textValue.IndexOfAny(['\uff08', '（', '\r', '\n']);
            var firstLine = nameEnd > 0 ? textValue[..nameEnd] : textValue;
            var rest      = nameEnd > 0 ? textValue[nameEnd..] : string.Empty;
            result = new SeStringBuilder().Append(firstLine).Append($"  [{statusID}]").Append(rest).Build();
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
