using System.Linq;
using System.Text;
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
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using OmenTools.Interop.Game.Helpers;
using OmenTools.Interop.Game.Lumina;
using OmenTools.Interop.Game.Models;
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

    private static readonly CompSig StatusDescSig = new("40 55 41 54 41 55 41 56 41 57 48 8D 6C 24 90 48 81 EC 70 01 00 00");
    private delegate nint StatusDescDelegate(nint ctx, Utf8String* output, uint statusID, uint param);
    private Hook<StatusDescDelegate>? statusDescHook;

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
        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PreDraw,  "_NaviMap",              OnAddon);

        statusDescHook ??= StatusDescSig.GetHook<StatusDescDelegate>(StatusDescDetour);
        statusDescHook.Enable();

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

            case "_NaviMap":
                if (!config.ShowWeatherID) break;
                if (!NaviMap->IsAddonAndNodesReady()) break;
                if (AtkStage.Instance()->TooltipManager.ParentAddonId != NaviMap->Id) break;

                var tooltip = AddonHelper.GetByName("Tooltip");
                if (tooltip == null || tooltip->RootNode == null || tooltip->RootNode->ChildNode == null) break;

                var tn = tooltip->GetTextNodeById(2);
                if (tn == null || tn->AtkResNode.Type != NodeType.Text) break;

                var tv = MemoryHelper.ReadSeStringNullTerminated((nint)tn->NodeText.StringPtr.Value).TextValue;
                if (string.IsNullOrEmpty(tv)) break;
                if (tv.Contains('[') && tv.Contains(']')) break;

                var wid = WeatherManager.Instance()->WeatherId;
                if (!LuminaGetter.TryGetRow<Weather>(wid, out var w)) break;
                if (!tv.Equals(w.Name.ToString(), StringComparison.OrdinalIgnoreCase)) break;

                var seStr = new SeStringBuilder().Append($"{w.Name} [{wid}]").Build();
                tn->SetText(seStr.EncodeWithNullTerminator());

                ushort tw = 0, th = 0;
                fixed (byte* p = seStr.EncodeWithNullTerminator())
                    tn->GetTextDrawSize(&tw, &th, p);

                var nw = (ushort)(tw + 16);
                var nh = (ushort)(th + 10);
                tn->AtkResNode.SetWidth(nw);
                tn->AtkResNode.SetHeight(nh);

                var bg = tooltip->GetNodeById(1);
                if (bg != null)
                {
                    bg->SetWidth(nw);
                    bg->SetHeight(nh);
                }
                tooltip->RootNode->SetWidth(nw);
                tooltip->RootNode->SetHeight(nh);
                tooltip->SetSize(nw, nh);
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

    private nint StatusDescDetour(nint ctx, Utf8String* output, uint statusID, uint param)
    {
        var ret = statusDescHook!.Original(ctx, output, statusID, param);

        if (!config.ShowStatusID || statusID == 0 || statusID == 0xFFFFFFFF || ret == 0)
            return ret;

        var originalText = ReadUtf8(ret);
        if (originalText.Length == 0)
            return ret;

        var newlineIndex = originalText.IndexOf('\n');
        string modifiedText;

        if (newlineIndex < 0)
            modifiedText = $"{originalText} [{statusID}]";
        else
            modifiedText = $"{originalText[..newlineIndex]} [{statusID}]{originalText[newlineIndex..]}";

        if (output != null && *(byte**)output != null)
        {
            var bytes = Encoding.UTF8.GetBytes(modifiedText + '\0');
            fixed (byte* p = bytes)
                *(byte**)output = p;
        }

        return ret;
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

    private static string ReadUtf8(nint ptr)
    {
        if (ptr == 0) return string.Empty;

        try
        {
            var span  = MemoryHelper.ReadRaw(ptr, 256);
            var len   = span.IndexOf((byte)0);
            return len <= 0 ? string.Empty : Encoding.UTF8.GetString(span[..len]);
        }
        catch
        {
            return string.Empty;
        }
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
