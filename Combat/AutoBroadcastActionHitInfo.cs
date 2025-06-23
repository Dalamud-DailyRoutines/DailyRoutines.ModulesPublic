using DailyRoutines.Abstracts;
using Dalamud.Hooking;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.Utility.Raii;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using Action = Lumina.Excel.Sheets.Action;

namespace DailyRoutines.Modules;

public unsafe class AutoBroadcastActionHitInfo : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("AutoBroadcastActionHitInfoTitle"),
        Description = GetLoc("AutoBroadcastActionHitInfoDescription"),
        Category    = ModuleCategories.Combat,
        Author      = ["Xww"]
    };
    
    private static Config ModuleConfig = null!;

    private static readonly CompSig ProcessPacketActionEffectSig = new("E8 ?? ?? ?? ?? 48 8B 8D F0 03 00 00");
    private delegate void ProcessPacketActionEffectDelegate(
        uint sourceID, nint sourceCharacter, nint pos, ActionEffectHandler.Header* effectHeader, ActionEffectHandler.Effect* effectArray,
        ulong* effectTrail);
    private static Hook<ProcessPacketActionEffectDelegate> ProcessPacketActionEffectHook;

    private static Action? SelectedCustomAction;
    private static string  ActionSearchInput = string.Empty;
    
    public override void Init()
    {
        ModuleConfig = LoadConfig<Config>() ?? new();

        ProcessPacketActionEffectHook ??=
            ProcessPacketActionEffectSig.GetHook<ProcessPacketActionEffectDelegate>(ProcessPacketActionEffectDetour);
        ProcessPacketActionEffectHook.Enable();
    }

    public override void ConfigUI()
    {
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(LightSkyBlue, $"{GetLoc("AutoBroadcastActionHitInfo-DHHint")}:");

        ImGui.SameLine();
        ImGui.SetNextItemWidth(300f * GlobalFontScale);
        ImGui.InputText("###DirectHitMessage", ref ModuleConfig.DirectHitPattern, 512);
        if (ImGui.IsItemDeactivatedAfterEdit()) 
            SaveConfig(ModuleConfig);

        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(LightSkyBlue, $"{GetLoc("AutoBroadcastActionHitInfo-CHHint")}:");

        ImGui.SameLine();
        ImGui.SetNextItemWidth(300f * GlobalFontScale);
        ImGui.InputText("###CriticalHitMessage", ref ModuleConfig.CriticalHitPattern, 512);
        if (ImGui.IsItemDeactivatedAfterEdit()) 
            SaveConfig(ModuleConfig);

        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(LightSkyBlue, $"{GetLoc("AutoBroadcastActionHitInfo-DCHHint")}:");

        ImGui.SameLine();
        ImGui.SetNextItemWidth(300f * GlobalFontScale);
        ImGui.InputText("###DirectCriticalHitMessage", ref ModuleConfig.DirectCriticalHitPattern, 512);
        if (ImGui.IsItemDeactivatedAfterEdit()) 
            SaveConfig(ModuleConfig);
        
        ScaledDummy(5f);
        
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(LightSkyBlue, $"{GetLoc("AutoBroadcastActionHitInfo-UseTTS")}");

        ImGui.SameLine();
        if (ImGui.Checkbox("###UseTTS", ref ModuleConfig.UseTTS))
            SaveConfig(ModuleConfig);

        ScaledDummy(5f);

        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(LightSkyBlue, $"{GetLoc("WorkMode")}:");

        ImGui.SameLine();
        if (ImGuiComponents.ToggleButton("WorkModeButton", ref ModuleConfig.WorkMode))
            SaveConfig(ModuleConfig);

        ImGui.SameLine();
        ImGui.Text(ModuleConfig.WorkMode ? GetLoc("Whitelist") : GetLoc("Blacklist"));

        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(LightSkyBlue, $"{GetLoc("Action")}:");

        ImGui.SameLine();
        ImGui.SetNextItemWidth(200f * GlobalFontScale);
        if (ModuleConfig.WorkMode
                ? ActionSelectCombo(ref ModuleConfig.WhitelistActions, ref ActionSearchInput)
                : ActionSelectCombo(ref ModuleConfig.BlacklistActions, ref ActionSearchInput))
            SaveConfig(ModuleConfig);

        ScaledDummy(5f);

        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(LightSkyBlue, $"{GetLoc("AutoBroadcastActionHitInfo-CustomActionAlias")}:");

        ImGui.SameLine();
        ImGui.SetNextItemWidth(250f * GlobalFontScale);
        using (ImRaii.PushId("AddCustomActionSelect"))
            ActionSelectCombo(ref SelectedCustomAction, ref ActionSearchInput);

        ImGui.SameLine();
        using (ImRaii.Disabled(SelectedCustomAction == null ||
                               ModuleConfig.CustomActionName.ContainsKey(SelectedCustomAction.Value.RowId)))
        {
            if (ImGuiOm.ButtonIcon("##新增", FontAwesomeIcon.Plus))
            {
                if (SelectedCustomAction != null)
                {
                    ModuleConfig.CustomActionName.TryAdd(SelectedCustomAction.Value.RowId, string.Empty);
                    ModuleConfig.Save(this);
                }
            }
        }

        ImGui.Spacing();

        if (ModuleConfig.CustomActionName.Count < 1) return;
        if (ImGui.CollapsingHeader(
                $"{GetLoc("AutoBroadcastActionHitInfo-CustomActionAliasCount", ModuleConfig.CustomActionName.Count)}###CustomActionsCombo"))
        {
            var counter = 1;
            foreach (var actionNamePair in ModuleConfig.CustomActionName)
            {
                using var id = ImRaii.PushId($"ActionCustomName_{actionNamePair.Key}");

                if (!LuminaGetter.TryGetRow<Action>(actionNamePair.Key, out var data)) continue;
                var actionIcon = DService.Texture.GetFromGameIcon(new(data.Icon)).GetWrapOrDefault();
                if (actionIcon == null) continue;

                using var group = ImRaii.Group();

                ImGui.AlignTextToFramePadding();
                ImGui.Text($"{counter}.");

                ImGui.SameLine();
                ImGui.Image(actionIcon.ImGuiHandle, new(ImGui.GetTextLineHeightWithSpacing()));

                ImGui.SameLine();
                ImGui.Text(data.Name.ExtractText());

                ImGui.SameLine();
                if (ImGuiOm.ButtonIconWithText(FontAwesomeIcon.TrashAlt, GetLoc("Delete")))
                {
                    ModuleConfig.CustomActionName.Remove(actionNamePair.Key);
                    ModuleConfig.Save(this);
                    continue;
                }

                using (ImRaii.PushIndent())
                {
                    var message = actionNamePair.Value;

                    ImGui.SetNextItemWidth(250f * GlobalFontScale);
                    if (ImGui.InputText("###ActionCustomNameInput", ref message, 64))
                        ModuleConfig.CustomActionName[actionNamePair.Key] = message;
                    if (ImGui.IsItemDeactivatedAfterEdit())
                        SaveConfig(ModuleConfig);
                }

                counter++;
            }
        }
    }

    private void ProcessPacketActionEffectDetour(
        uint   sourceID, nint sourceCharacter, nint pos, ActionEffectHandler.Header* effectHeader, ActionEffectHandler.Effect* effectArray,
        ulong* effectTrail)
    {
        ProcessPacketActionEffectHook.Original(sourceID, sourceCharacter, pos, effectHeader, effectArray, effectTrail);
        Parse(sourceID, effectHeader, effectArray);
    }
    
    public static void Parse(uint sourceEntityID, ActionEffectHandler.Header* effectHeader, ActionEffectHandler.Effect* effectArray)
    {
        try
        {
            var targets = effectHeader->NumTargets;
            if (targets < 1) return;

            if (DService.ObjectTable.LocalPlayer is not { } localPlayer) return;
            if (localPlayer.EntityId != sourceEntityID) return;

            var actionID   = effectHeader->ActionId;
            var actionData = LuminaGetter.GetRow<Action>(actionID);
            if (actionData == null || actionData.Value.ActionCategory.RowId == 1) return; // 自动攻击

            switch (ModuleConfig.WorkMode)
            {
                case false:
                    if (ModuleConfig.BlacklistActions.Contains(actionID)) return;
                    break;
                case true:
                    if (!ModuleConfig.WhitelistActions.Contains(actionID)) return;
                    break;
            }

            var actionName = ModuleConfig.CustomActionName.TryGetValue(actionID, out var customName) &&
                             !string.IsNullOrWhiteSpace(customName)
                                 ? customName
                                 : actionData.Value.Name.ExtractText();

            var message = effectArray->Param0 switch
            {
                64 => string.Format(ModuleConfig.DirectHitPattern,         actionName),
                32 => string.Format(ModuleConfig.CriticalHitPattern,       actionName),
                96 => string.Format(ModuleConfig.DirectCriticalHitPattern, actionName),
                _  => string.Empty
            };

            if (string.IsNullOrWhiteSpace(message)) return;

            switch (effectArray->Param0)
            {
                case 32 or 64:
                    ContentHintBlue(message, 10);
                    if (ModuleConfig.UseTTS) 
                        Speak(message);
                    break;
                case 96:
                    ContentHintRed(message, 10);
                    if (ModuleConfig.UseTTS) 
                        Speak(message);
                    break;
            }
        }
        catch
        {
            // ignored
        }
        
    }
    
    public class Config : ModuleConfiguration
    {
        // False - 黑名单, True - 白名单
        public bool WorkMode;

        public HashSet<uint> BlacklistActions = [];
        public HashSet<uint> WhitelistActions = [];
        
        public Dictionary<uint, string> CustomActionName = [];

        public string DirectHitPattern         = "技能 {0} 触发了直击";
        public string CriticalHitPattern       = "技能 {0} 触发了暴击";
        public string DirectCriticalHitPattern = "技能 {0} 触发了直暴";
        
        public bool UseTTS;
    }
}
