using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using Dalamud.Hooking;
using Dalamud.Interface.Components;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using OmenTools.ImGuiOm.Widgets.Combos;
using OmenTools.Interop.Game.Lumina;
using OmenTools.Interop.Game.Models;
using OmenTools.OmenService;
using Action = Lumina.Excel.Sheets.Action;

namespace DailyRoutines.ModulesPublic;

public unsafe class AutoBroadcastActionHitInfo : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoBroadcastActionHitInfoTitle"),
        Description = Lang.Get("AutoBroadcastActionHitInfoDescription"),
        Category    = ModuleCategory.Action,
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
    private Hook<ProcessPacketActionEffectDelegate> ProcessPacketActionEffectHook;

    private Config config = null!;

    private readonly ActionSelectCombo whitelistCombo = new("Whitelist");
    private readonly ActionSelectCombo blacklistCombo = new("Blacklist");
    private readonly ActionSelectCombo selectedCombo  = new("Selected");

    protected override void Init()
    {
        config = Config.Load(this) ?? new();

        whitelistCombo.SelectedIDs = config.WhitelistActions;
        blacklistCombo.SelectedIDs = config.BlacklistActions;

        ProcessPacketActionEffectHook ??= ProcessPacketActionEffectSig.GetHook<ProcessPacketActionEffectDelegate>(ProcessPacketActionEffectDetour);
        ProcessPacketActionEffectHook.Enable();
    }

    protected override void ConfigUI()
    {
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), $"{Lang.Get("AutoBroadcastActionHitInfo-DHHint")}:");

        ImGui.SameLine();
        ImGui.SetNextItemWidth(300f * GlobalUIScale);
        ImGui.InputText("###DirectHitMessage", ref config.DirectHitPattern);
        if (ImGui.IsItemDeactivatedAfterEdit())
            config.Save(this);

        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), $"{Lang.Get("AutoBroadcastActionHitInfo-CHHint")}:");

        ImGui.SameLine();
        ImGui.SetNextItemWidth(300f * GlobalUIScale);
        ImGui.InputText("###CriticalHitMessage", ref config.CriticalHitPattern);
        if (ImGui.IsItemDeactivatedAfterEdit())
            config.Save(this);

        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), $"{Lang.Get("AutoBroadcastActionHitInfo-DCHHint")}:");

        ImGui.SameLine();
        ImGui.SetNextItemWidth(300f * GlobalUIScale);
        ImGui.InputText("###DirectCriticalHitMessage", ref config.DirectCriticalHitPattern);
        if (ImGui.IsItemDeactivatedAfterEdit())
            config.Save(this);

        ImGuiOm.ScaledDummy(5f);

        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), $"{Lang.Get("AutoBroadcastActionHitInfo-UseTTS")}");

        ImGui.SameLine();
        if (ImGui.Checkbox("###UseTTS", ref config.UseTTS))
            config.Save(this);

        ImGuiOm.ScaledDummy(5f);

        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), $"{Lang.Get("WorkMode")}:");

        ImGui.SameLine();
        if (ImGuiComponents.ToggleButton("WorkModeButton", ref config.WorkMode))
            config.Save(this);

        ImGui.SameLine();
        ImGui.TextUnformatted(config.WorkMode ? Lang.Get("Whitelist") : Lang.Get("Blacklist"));

        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), $"{Lang.Get("Action")}:");

        ImGui.SameLine();
        ImGui.SetNextItemWidth(200f * GlobalUIScale);

        if (config.WorkMode
                ? whitelistCombo.DrawCheckbox()
                : blacklistCombo.DrawCheckbox())
        {
            config.BlacklistActions = blacklistCombo.SelectedIDs;
            config.WhitelistActions = blacklistCombo.SelectedIDs;

            config.Save(this);
        }

        ImGuiOm.ScaledDummy(5f);

        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), $"{Lang.Get("AutoBroadcastActionHitInfo-CustomActionAlias")}:");

        ImGui.SameLine();
        ImGui.SetNextItemWidth(250f * GlobalUIScale);
        using (ImRaii.PushId("AddCustomActionSelect"))
            selectedCombo.DrawRadio();

        ImGui.SameLine();

        using (ImRaii.Disabled
               (
                   selectedCombo.SelectedID == 0 ||
                   config.CustomActionName.ContainsKey(selectedCombo.SelectedID)
               ))
        {
            if (ImGuiOm.ButtonIcon("##新增", FontAwesomeIcon.Plus))
            {
                if (selectedCombo.SelectedID != 0)
                {
                    config.CustomActionName.TryAdd(selectedCombo.SelectedID, string.Empty);
                    config.Save(this);
                }
            }
        }

        ImGui.Spacing();

        if (config.CustomActionName.Count < 1) return;

        if (ImGui.CollapsingHeader
            (
                $"{Lang.Get("AutoBroadcastActionHitInfo-CustomActionAliasCount", config.CustomActionName.Count)}###CustomActionsCombo"
            ))
        {
            var counter = 1;

            foreach (var actionNamePair in config.CustomActionName)
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

                if (ImGuiOm.ButtonIconWithText(FontAwesomeIcon.TrashAlt, Lang.Get("Delete")))
                {
                    config.CustomActionName.Remove(actionNamePair.Key);
                    config.Save(this);
                    continue;
                }

                using (ImRaii.PushIndent())
                {
                    var message = actionNamePair.Value;

                    ImGui.SetNextItemWidth(250f * GlobalUIScale);
                    if (ImGui.InputText("###ActionCustomNameInput", ref message, 64))
                        config.CustomActionName[actionNamePair.Key] = message;
                    if (ImGui.IsItemDeactivatedAfterEdit())
                        config.Save(this);
                }

                counter++;
            }
        }
    }

    private void ProcessPacketActionEffectDetour
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

    private void Parse(uint sourceEntityID, ActionEffectHandler.Header* effectHeader, ActionEffectHandler.Effect* effectArray)
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

            switch (config.WorkMode)
            {
                case false:
                    if (config.BlacklistActions.Contains(actionID)) return;
                    break;
                case true:
                    if (!config.WhitelistActions.Contains(actionID)) return;
                    break;
            }

            var actionName = config.CustomActionName.TryGetValue(actionID, out var customName) &&
                             !string.IsNullOrWhiteSpace(customName)
                                 ? customName
                                 : actionData.Value.Name.ToString();

            var message = effectArray->Param0 switch
            {
                64 => string.Format(config.DirectHitPattern,         actionName),
                32 => string.Format(config.CriticalHitPattern,       actionName),
                96 => string.Format(config.DirectCriticalHitPattern, actionName),
                _  => string.Empty
            };

            if (string.IsNullOrWhiteSpace(message)) return;

            switch (effectArray->Param0)
            {
                case 32 or 64:
                    NotifyHelper.Instance().ContentHintBlue(message, TimeSpan.FromSeconds(1));
                    if (config.UseTTS)
                        NotifyHelper.Speak(message);
                    break;
                case 96:
                    NotifyHelper.Instance().ContentHintRed(message, TimeSpan.FromSeconds(1));
                    if (config.UseTTS)
                        NotifyHelper.Speak(message);
                    break;
            }
        }
        catch
        {
            // ignored
        }

    }

    private class Config : ModuleConfig
    {
        public HashSet<uint> BlacklistActions   = [];
        public string        CriticalHitPattern = "技能 {0} 触发了暴击";

        public Dictionary<uint, string> CustomActionName         = [];
        public string                   DirectCriticalHitPattern = "技能 {0} 触发了直暴";

        public string DirectHitPattern = "技能 {0} 触发了直击";

        public bool UseTTS;

        public HashSet<uint> WhitelistActions = [];

        // False - 黑名单, True - 白名单
        public bool WorkMode;
    }
}
