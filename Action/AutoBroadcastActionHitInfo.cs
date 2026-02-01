using System.Collections.Generic;
using DailyRoutines.Abstracts;
using Dalamud.Hooking;
using Dalamud.Interface.Components;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using Action = Lumina.Excel.Sheets.Action;

namespace DailyRoutines.ModulesPublic;

public unsafe class AutoBroadcastActionHitInfo : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("AutoBroadcastActionHitInfoTitle"),
        Description = GetLoc("AutoBroadcastActionHitInfoDescription"),
        Category    = ModuleCategories.Action,
        Author      = ["Xww"]
    };

    private static readonly CompSig ProcessPacketActionEffectSig = new("E8 ?? ?? ?? ?? 48 8B 8D F0 03 00 00");
    private delegate void ProcessPacketActionEffectDelegate
    (
        uint                        sourceID,
        nint                        sourceCharacter,
        nint                        pos,
        ActionEffectHandler.Header* effectHeader,
        ActionEffectHandler.Effect* effectArray,
        ulong*                      effectTrail
    );
    private static Hook<ProcessPacketActionEffectDelegate> ProcessPacketActionEffectHook;

    private static Config ModuleConfig = null!;

    private static readonly ActionSelectCombo WhitelistCombo = new("Whitelist");
    private static readonly ActionSelectCombo BlacklistCombo = new("Blacklist");
    private static readonly ActionSelectCombo SelectedCombo  = new("Selected");

    protected override void Init()
    {
        ModuleConfig = LoadConfig<Config>() ?? new();

        WhitelistCombo.SelectedActionIDs = ModuleConfig.WhitelistActions;
        BlacklistCombo.SelectedActionIDs = ModuleConfig.BlacklistActions;

        ProcessPacketActionEffectHook ??= ProcessPacketActionEffectSig.GetHook<ProcessPacketActionEffectDelegate>(ProcessPacketActionEffectDetour);
        ProcessPacketActionEffectHook.Enable();
    }

    protected override void ConfigUI()
    {
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), $"{GetLoc("AutoBroadcastActionHitInfo-DHHint")}:");

        ImGui.SameLine();
        ImGui.SetNextItemWidth(300f * GlobalFontScale);
        ImGui.InputText("###DirectHitMessage", ref ModuleConfig.DirectHitPattern);
        if (ImGui.IsItemDeactivatedAfterEdit())
            SaveConfig(ModuleConfig);

        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), $"{GetLoc("AutoBroadcastActionHitInfo-CHHint")}:");

        ImGui.SameLine();
        ImGui.SetNextItemWidth(300f * GlobalFontScale);
        ImGui.InputText("###CriticalHitMessage", ref ModuleConfig.CriticalHitPattern);
        if (ImGui.IsItemDeactivatedAfterEdit())
            SaveConfig(ModuleConfig);

        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), $"{GetLoc("AutoBroadcastActionHitInfo-DCHHint")}:");

        ImGui.SameLine();
        ImGui.SetNextItemWidth(300f * GlobalFontScale);
        ImGui.InputText("###DirectCriticalHitMessage", ref ModuleConfig.DirectCriticalHitPattern);
        if (ImGui.IsItemDeactivatedAfterEdit())
            SaveConfig(ModuleConfig);

        ScaledDummy(5f);

        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), $"{GetLoc("AutoBroadcastActionHitInfo-UseTTS")}");

        ImGui.SameLine();
        if (ImGui.Checkbox("###UseTTS", ref ModuleConfig.UseTTS))
            SaveConfig(ModuleConfig);

        ScaledDummy(5f);

        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), $"{GetLoc("WorkMode")}:");

        ImGui.SameLine();
        if (ImGuiComponents.ToggleButton("WorkModeButton", ref ModuleConfig.WorkMode))
            SaveConfig(ModuleConfig);

        ImGui.SameLine();
        ImGui.TextUnformatted(ModuleConfig.WorkMode ? GetLoc("Whitelist") : GetLoc("Blacklist"));

        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), $"{GetLoc("Action")}:");

        ImGui.SameLine();
        ImGui.SetNextItemWidth(200f * GlobalFontScale);

        if (ModuleConfig.WorkMode
                ? WhitelistCombo.DrawCheckbox()
                : BlacklistCombo.DrawCheckbox())
        {
            ModuleConfig.BlacklistActions = BlacklistCombo.SelectedActionIDs;
            ModuleConfig.WhitelistActions = BlacklistCombo.SelectedActionIDs;

            SaveConfig(ModuleConfig);
        }

        ScaledDummy(5f);

        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), $"{GetLoc("AutoBroadcastActionHitInfo-CustomActionAlias")}:");

        ImGui.SameLine();
        ImGui.SetNextItemWidth(250f * GlobalFontScale);
        using (ImRaii.PushId("AddCustomActionSelect"))
            SelectedCombo.DrawRadio();

        ImGui.SameLine();

        using (ImRaii.Disabled
               (
                   SelectedCombo.SelectedActionID == 0 ||
                   ModuleConfig.CustomActionName.ContainsKey(SelectedCombo.SelectedActionID)
               ))
        {
            if (ImGuiOm.ButtonIcon("##新增", FontAwesomeIcon.Plus))
            {
                if (SelectedCombo.SelectedActionID != 0)
                {
                    ModuleConfig.CustomActionName.TryAdd(SelectedCombo.SelectedActionID, string.Empty);
                    ModuleConfig.Save(this);
                }
            }
        }

        ImGui.Spacing();

        if (ModuleConfig.CustomActionName.Count < 1) return;

        if (ImGui.CollapsingHeader
            (
                $"{GetLoc("AutoBroadcastActionHitInfo-CustomActionAliasCount", ModuleConfig.CustomActionName.Count)}###CustomActionsCombo"
            ))
        {
            var counter = 1;

            foreach (var actionNamePair in ModuleConfig.CustomActionName)
            {
                using var id = ImRaii.PushId($"ActionCustomName_{actionNamePair.Key}");

                if (!LuminaGetter.TryGetRow<Action>(actionNamePair.Key, out var data)) continue;
                var actionIcon = DService.Instance().Texture.GetFromGameIcon(new(data.Icon)).GetWrapOrDefault();
                if (actionIcon == null) continue;

                using var group = ImRaii.Group();

                ImGui.AlignTextToFramePadding();
                ImGui.TextUnformatted($"{counter}.");

                ImGui.SameLine();
                ImGui.Image(actionIcon.Handle, new(ImGui.GetTextLineHeightWithSpacing()));

                ImGui.SameLine();
                ImGui.TextUnformatted(data.Name.ToString());

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

    private static void ProcessPacketActionEffectDetour
    (
        uint                        sourceID,
        nint                        sourceCharacter,
        nint                        pos,
        ActionEffectHandler.Header* effectHeader,
        ActionEffectHandler.Effect* effectArray,
        ulong*                      effectTrail
    )
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

            if (DService.Instance().ObjectTable.LocalPlayer is not { } localPlayer) return;
            if (localPlayer.EntityID != sourceEntityID) return;

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
                                 : actionData.Value.Name.ToString();

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
