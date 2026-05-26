using System.Runtime.InteropServices;
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
using Dalamud.Hooking;
using Dalamud.Memory;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.Enums;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using InteropGenerator.Runtime;
using Lumina.Excel.Sheets;
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

    internal bool ShowWeatherEnabled => config.ShowWeatherID;

    private IDtrBarEntry? zoneInfoEntry;

    private static readonly CompSig ShowTooltipSig = new("E8 ?? ?? ?? ?? 49 63 47 ?? BB");

    private delegate void ShowTooltipDelegate
    (
        AtkTooltipManager*                manager,
        AtkTooltipType                    type,
        ushort                            parentID,
        AtkResNode*                       targetNode,
        AtkTooltipManager.AtkTooltipArgs* tooltipArgs,
        void*                             unkDelegate,
        byte                              unk7,
        byte                              unk8
    );

    private Hook<ShowTooltipDelegate>? showTooltipHook;

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

        showTooltipHook ??= ShowTooltipSig.GetHook<ShowTooltipDelegate>(ShowTooltipDetour);
        showTooltipHook.Enable();
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

        showTooltipHook?.Dispose();
        showTooltipHook = null;
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

    private void ShowTooltipDetour
    (
        AtkTooltipManager*                manager,
        AtkTooltipType                    type,
        ushort                            parentID,
        AtkResNode*                       targetNode,
        AtkTooltipManager.AtkTooltipArgs* args,
        void*                             unkDelegate,
        byte                              unk7,
        byte                              unk8
    )
    {
        if (config.ShowStatusID)
        {
            try { ModifyStatusTooltip(targetNode, args); }
            catch { /* ignored */ }
        }

        if (config.ShowWeatherID)
        {
            try { ModifyWeatherTooltip(parentID, targetNode, args); }
            catch { /* ignored */ }
        }

        showTooltipHook?.Original(manager, type, parentID, targetNode, args, unkDelegate, unk7, unk8);
    }

    private void ModifyStatusTooltip(AtkResNode* targetNode, AtkTooltipManager.AtkTooltipArgs* args)
    {
        if (DService.Instance().ObjectTable.LocalPlayer is not { } localPlayer || targetNode == null) return;

        var imageNode = targetNode->GetAsAtkImageNode();
        if (imageNode == null) return;

        var iconID = imageNode->PartsList->Parts[imageNode->PartId].UldAsset->AtkTexture.Resource->IconId;
        if (iconID is < 210000 or > 230000) return;

        var map = new Dictionary<uint, uint>();

        if (TargetManager.Target is { } target && target.Address != localPlayer.Address)
            AddStatuses(ref target.ToBCStruct()->StatusManager);
        if (TargetManager.FocusTarget is { } focus)
            AddStatuses(ref focus.ToBCStruct()->StatusManager);
        foreach (var member in AgentHUD.Instance()->PartyMembers.ToArray().Where(m => m.Index != 0))
        {
            if (member.Object != null)
                AddStatuses(ref member.Object->StatusManager);
        }
        AddStatuses(ref localPlayer.ToBCStruct()->StatusManager);

        if (!map.TryGetValue(iconID, out var statusID) || statusID == 0) return;

        var currentText = MemoryHelper.ReadSeStringNullTerminated((nint)args->TextArgs.Text.Value);

        if (currentText.TextValue.Contains($"[{statusID}]")) return;

        SeString finalText;
        try
        {
            var regex = new Regex(@"^(.*?)(?=\uff08|（|\n|$)");
            finalText = new SeStringBuilder().Append(regex.Replace(currentText.TextValue,
                match => match.Groups[1].Value + $"  [{statusID}]")).Build();
        }
        catch
        {
            finalText = currentText;
        }

        var bytes = finalText.EncodeWithNullTerminator();
        var ptr   = (byte*)Marshal.AllocHGlobal(bytes.Length);
        for (var i = 0; i < bytes.Length; i++)
            ptr[i] = bytes[i];
        args->TextArgs.Text = ptr;

        return;

        void AddStatuses(ref StatusManager sm)
        {
            foreach (var s in sm.Status)
            {
                if (s.StatusId == 0) continue;
                if (!LuminaGetter.TryGetRow<RowStatus>(s.StatusId, out var row)) continue;
                map.TryAdd(row.Icon, row.RowId);
                for (var i = 1; i <= s.Param; i++)
                    map.TryAdd((uint)(row.Icon + i), row.RowId);
            }
        }
    }

    private void ModifyWeatherTooltip(ushort parentID, AtkResNode* targetNode, AtkTooltipManager.AtkTooltipArgs* args)
    {
        if (targetNode == null || NaviMap == null || parentID != NaviMap->Id) return;

        var compNode = targetNode->ParentNode->GetAsAtkComponentNode();
        if (compNode == null) return;

        var imageNode = compNode->Component->UldManager.SearchNodeById(3)->GetAsAtkImageNode();
        if (imageNode == null) return;

        var iconID    = imageNode->PartsList->Parts[imageNode->PartId].UldAsset->AtkTexture.Resource->IconId;
        var weatherID = WeatherManager.Instance()->WeatherId;

        if (!LuminaGetter.TryGetRow<Weather>(weatherID, out var weather)) return;
        if (weather.Icon != iconID) return;

        var finalText = new SeStringBuilder().Append($"{weather.Name} [{weatherID}]").Build();
        var bytes     = finalText.EncodeWithNullTerminator();
        var ptr       = (byte*)Marshal.AllocHGlobal(bytes.Length);
        for (var i = 0; i < bytes.Length; i++)
            ptr[i] = bytes[i];
        args->TextArgs.Text = ptr;
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
