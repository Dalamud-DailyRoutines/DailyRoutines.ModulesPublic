using System.Collections.Generic;
using System.Numerics;
using DailyRoutines.Abstracts;
using DailyRoutines.Windows;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Hooking;
using Dalamud.Interface;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace DailyRoutines.ModulesPublic;

public unsafe class PartyFinderSettingRecord : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("PartyFinderSettingRecordTitle"),
        Description = GetLoc("PartyFinderSettingRecordDescription"),
        Category    = ModuleCategories.UIOptimization,
        Author      = ["status102"]
    };

    private static Config? ModuleConfig;
    private static bool EditInited;

    protected override void Init()
    {
        ModuleConfig = LoadConfig<Config>() ?? new();
        Overlay ??= new Overlay(this);
        Overlay.Flags |= ImGuiWindowFlags.NoMove;
        TaskHelper ??= new();

        AgentReceiveEventHook =
            DService.Hook.HookFromSignature<AddonFireCallBackDelegate>(AddonFireCallBackSig.Get(), AddonFireCallBackDetour);
        AgentReceiveEventHook.Enable();

        DService.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "LookingForGroupCondition", OnLookingForGroupConditionAddon);
        DService.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "LookingForGroupCondition", OnLookingForGroupConditionAddon);
    }

    protected override void Uninit()
    {
        DService.AddonLifecycle.UnregisterListener(OnLookingForGroupConditionAddon);

        base.Uninit();
    }

    protected override void OverlayUI()
    {
        if (!EditInited || !IsAddonAndNodesReady(LookingForGroup) || !IsAddonAndNodesReady(LookingForGroupCondition))
            return;

        var addon = LookingForGroupCondition;

        var pos = new Vector2(addon->GetX() - ImGui.GetWindowSize().X, addon->GetY() + 6);
        ImGui.SetWindowPos(pos);

        if (ImGuiOm.ButtonIconWithText(FontAwesomeIcon.Plus, GetLoc("Add")))
        {
            var setting = ModuleConfig.Last.Copy();
            setting.Name = LookingForGroupCondition->GetComponentByNodeId(11)->UldManager.SearchNodeById(2)->GetAsAtkComponentNode()->Component->GetTextNodeById(3)->GetAsAtkTextNode()->NodeText.ToString();
            ModuleConfig.Slot.Add(setting);
        }

        ImGui.SameLine();
        if (ImGuiOm.ButtonIconWithText(FontAwesomeIcon.TrashAlt, GetLoc("Clear")))
            ModuleConfig.Slot.Clear();

        for (var i = 0; i < ModuleConfig.Slot.Count; i++)
        {
            var config = ModuleConfig.Slot[i];
            using (ImRaii.Group())
            {
                var title = config.Name;
                if (string.IsNullOrEmpty(title)) 
                    title = GetLoc("None");

                ImGui.AlignTextToFramePadding();
                ImGui.Text($"{i + 1}: {title}");
                ImGuiOm.TooltipHover(GetLoc("PartyFinderSettingRecord-Message", title, config.Description));

                ImGui.SameLine();
                if (ImGuiOm.ButtonIcon($"Apply{i}", FontAwesomeIcon.Check, GetLoc("Apply")))
                    ApplyPreset(config);

                ImGui.SameLine();
                if (ImGuiOm.ButtonIcon($"Delete{i}", FontAwesomeIcon.Trash, GetLoc("Delete")))
                    ModuleConfig.Slot.RemoveAt(i);
            }
        }
    }

    private void OnLookingForGroupConditionAddon(AddonEvent type, AddonArgs args)
    {
        switch (type)
        {
            case AddonEvent.PostSetup:
                Overlay.IsOpen = true;

                if (EditInited || !IsAddonAndNodesReady(LookingForGroup))
                    return;

                ApplyPreset(ModuleConfig.Last);
                EditInited = true;
                break;
            case AddonEvent.PreFinalize:
                Overlay.IsOpen = false;
                break;
        }
    }

    private void ApplyPreset(PartyFinderSetting setting)
    {
        if (!IsAddonAndNodesReady(LookingForGroup) || !IsAddonAndNodesReady(LookingForGroupCondition))
            return;

        Callback(LookingForGroupCondition, true, 11, setting.ItemLevel.AvgIL, setting.ItemLevel.IsEnableAvgIL);
        Callback(LookingForGroupCondition, true, 12, setting.Category, 0);
        Callback(LookingForGroupCondition, true, 13, setting.Duty, 0);
        Callback(LookingForGroupCondition, true, 15, setting.Description, 0);

        TaskHelper.Enqueue(() => LookingForGroupCondition->Close(true));
        TaskHelper.Enqueue(() => Callback(LookingForGroup, true, 14));
    }

    #region Hook

    // 偷的 Simple Tweaks 的 Debugger 的 Addon Callback
    private static readonly CompSig AddonFireCallBackSig = new("E8 ?? ?? ?? ?? 0F B6 E8 8B 44 24 20");
    private delegate void* AddonFireCallBackDelegate(
        AtkUnitBase* atkunitbase, int valuecount, AtkValue* atkvalues, byte updateVisibility);
    private static Hook<AddonFireCallBackDelegate>? AgentReceiveEventHook;

    private void* AddonFireCallBackDetour(
        AtkUnitBase* atkUnitBase, int valueCount, AtkValue* atkValues, byte updateVisibility)
    {
        if (!EditInited || atkUnitBase->NameString != "LookingForGroupCondition" || valueCount < 2)
            return AgentReceiveEventHook.Original(atkUnitBase, valueCount, atkValues, updateVisibility);

        var eventCase = atkValues[0].Int;
        switch (eventCase)
        {
            case 11 when valueCount == 3:
                var itemLevel = atkValues[1].UInt;
                var isEnableIL = atkValues[2].Bool;
                ModuleConfig.Last.ItemLevel = new(itemLevel, isEnableIL);
                break;
            case 12:
                ModuleConfig.Last.Category = atkValues[1].UInt;
                ModuleConfig.Last.Duty = 0;
                break;
            case 13:
                ModuleConfig.Last.Duty = atkValues[1].UInt;
                break;
            case 15:
                ModuleConfig.Last.Description = SeString.Parse(atkValues[1].String.Value).TextValue;
                break;
        }

        ModuleConfig.Save(this);
        return AgentReceiveEventHook.Original(atkUnitBase, valueCount, atkValues, updateVisibility);
    }

    #endregion

    #region Config

    private class PartyFinderSetting
    {
        /// <summary>
        /// 副本名，仅作为提示用
        /// </summary>
        public string Name;
        public uint Category;
        public uint Duty;
        public string Description = string.Empty;
        public (uint AvgIL, bool IsEnableAvgIL) ItemLevel = new(0, false);

        public PartyFinderSetting Copy()
        {
            return new() { Name = Name, Category = Category, Duty = Duty, Description = Description, ItemLevel = ItemLevel };
        }
    }

    private class Config : ModuleConfiguration
    {
        public PartyFinderSetting Last = new();

        public List<PartyFinderSetting> Slot = [];
    }

    #endregion
}

