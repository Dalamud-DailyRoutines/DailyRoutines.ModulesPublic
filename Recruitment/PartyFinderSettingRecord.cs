using System.Numerics;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Component.GUI;
using OmenTools.Interop.Game.Models;

namespace DailyRoutines.ModulesPublic;

public unsafe class PartyFinderSettingRecord : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("PartyFinderSettingRecordTitle"),
        Description = Lang.Get("PartyFinderSettingRecordDescription"),
        Category    = ModuleCategory.Recruitment,
        Author      = ["status102"]
    };
    
    private static readonly CompSig AddonFireCallBackSig = new("E8 ?? ?? ?? ?? 0F B6 E8 8B 44 24 20");
    private delegate        bool   AddonFireCallBackDelegate(AtkUnitBase* atkunitbase, int valuecount, AtkValue* atkvalues, byte updateVisibility);
    private                 Hook<AddonFireCallBackDelegate>? AgentReceiveEventHook;

    private Config config = null!;

    private bool editInited;

    protected override void Init()
    {
        config = Config.Load(this) ?? new();

        Overlay       ??= new(this);
        Overlay.Flags |=  ImGuiWindowFlags.NoMove;
        TaskHelper    ??= new();

        AgentReceiveEventHook = AddonFireCallBackSig.GetHook<AddonFireCallBackDelegate>(AddonFireCallBackDetour);
        AgentReceiveEventHook.Enable();

        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PostSetup,   "LookingForGroupCondition", OnLookingForGroupConditionAddon);
        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "LookingForGroupCondition", OnLookingForGroupConditionAddon);
    }

    protected override void Uninit() =>
        DService.Instance().AddonLifecycle.UnregisterListener(OnLookingForGroupConditionAddon);

    protected override void OverlayUI()
    {
        if (!editInited || !LookingForGroup->IsAddonAndNodesReady() || !LookingForGroupCondition->IsAddonAndNodesReady())
            return;

        var addon = LookingForGroupCondition;

        var pos = new Vector2(addon->GetX() - ImGui.GetWindowSize().X, addon->GetY() + 6);
        ImGui.SetWindowPos(pos);

        if (ImGuiOm.ButtonIconWithText(FontAwesomeIcon.Plus, Lang.Get("Add")))
        {
            var setting = config.Last.Copy();
            setting.Name =
                LookingForGroupCondition->GetComponentByNodeId(11)->UldManager.SearchNodeById(2)->GetAsAtkComponentNode()->Component->GetTextNodeById(3)->
                    GetAsAtkTextNode()->NodeText.ToString();
            config.Slot.Add(setting);
        }

        ImGui.SameLine();
        if (ImGuiOm.ButtonIconWithText(FontAwesomeIcon.TrashAlt, Lang.Get("Clear")))
            config.Slot.Clear();

        for (var i = 0; i < config.Slot.Count; i++)
        {
            var setting = this.config.Slot[i];

            using (ImRaii.Group())
            {
                var title = setting.Name;
                if (string.IsNullOrEmpty(title))
                    title = Lang.Get("None");

                ImGui.AlignTextToFramePadding();
                ImGui.TextUnformatted($"{i + 1}: {title}");
                ImGuiOm.TooltipHover(Lang.Get("PartyFinderSettingRecord-Message", title, setting.Description));

                ImGui.SameLine();
                if (ImGuiOm.ButtonIcon($"Apply{i}", FontAwesomeIcon.Check, Lang.Get("Apply")))
                    ApplyPreset(setting);

                ImGui.SameLine();
                if (ImGuiOm.ButtonIcon($"Delete{i}", FontAwesomeIcon.Trash, Lang.Get("Delete")))
                    this.config.Slot.RemoveAt(i);
            }
        }
    }
    
    #region 事件
    
    private void OnLookingForGroupConditionAddon(AddonEvent type, AddonArgs args)
    {
        switch (type)
        {
            case AddonEvent.PostSetup:
                Overlay.IsOpen = true;

                if (editInited || !LookingForGroup->IsAddonAndNodesReady())
                    return;

                ApplyPreset(config.Last);
                editInited = true;
                break;
            case AddonEvent.PreFinalize:
                Overlay.IsOpen = false;
                break;
        }
    }

    private bool AddonFireCallBackDetour
    (
        AtkUnitBase* atkUnitBase,
        int          valueCount,
        AtkValue*    atkValues,
        byte         updateVisibility
    )
    {
        if (!editInited || atkUnitBase->NameString != "LookingForGroupCondition" || valueCount < 2)
            return AgentReceiveEventHook.Original(atkUnitBase, valueCount, atkValues, updateVisibility);

        if (atkValues != null)
        {
            var eventCase = atkValues[0].Int;

            switch (eventCase)
            {
                case 11 when valueCount == 3:
                    var itemLevel  = atkValues[1].UInt;
                    var isEnableIL = atkValues[2].Bool;
                    config.Last.ItemLevel = new(itemLevel, isEnableIL);
                    break;
                case 12:
                    config.Last.Category = atkValues[1].UInt;
                    config.Last.Duty     = 0;
                    break;
                case 13:
                    config.Last.Duty = atkValues[1].UInt;
                    break;
                case 15:
                    config.Last.Description = SeString.Parse(atkValues[1].String.Value).TextValue;
                    break;
            }
        }

        config.Save(this);
        return AgentReceiveEventHook.Original(atkUnitBase, valueCount, atkValues, updateVisibility);
    }

    #endregion
    
    private void ApplyPreset(PartyFinderSetting setting)
    {
        if (!LookingForGroup->IsAddonAndNodesReady() || !LookingForGroupCondition->IsAddonAndNodesReady())
            return;

        LookingForGroupCondition->Callback(11, setting.ItemLevel.AvgIL, setting.ItemLevel.IsEnableAvgIL);
        LookingForGroupCondition->Callback(12, setting.Category, 0);
        LookingForGroupCondition->Callback(13, setting.Duty, 0);
        LookingForGroupCondition->Callback(15, setting.Description, 0);

        TaskHelper.DelayNext(100);
        TaskHelper.Enqueue(() => LookingForGroupCondition->Close(true));
        TaskHelper.Enqueue(() => LookingForGroup->Callback(14));
    }
    
    #region Config

    private class PartyFinderSetting
    {
        public uint                             Category;
        public string                           Description = string.Empty;
        public uint                             Duty;
        public (uint AvgIL, bool IsEnableAvgIL) ItemLevel = new(0, false);

        /// <summary>
        ///     副本名，仅作为提示用
        /// </summary>
        public string Name;

        public PartyFinderSetting Copy() =>
            new() { Name = Name, Category = Category, Duty = Duty, Description = Description, ItemLevel = ItemLevel };
    }

    private class Config : ModuleConfig
    {
        public PartyFinderSetting Last = new();

        public List<PartyFinderSetting> Slot = [];
    }

    #endregion
}
