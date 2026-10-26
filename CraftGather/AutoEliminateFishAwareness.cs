using DailyRoutines.Common.Extensions;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using Lumina.Excel.Sheets;
using OmenTools.ImGuiOm.Widgets.Combos;
using OmenTools.Info.Game.Enums;
using OmenTools.Interop.Game.Helpers;
using OmenTools.Interop.Game.Lumina;
using OmenTools.OmenService;
using OmenTools.Threading;

namespace DailyRoutines.ModulesPublic.CraftGather;

public unsafe class AutoEliminateFishAwareness : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoEliminateFishAwarenessTitle"),
        Description = Lang.Get("AutoEliminateFishAwarenessDescription"),
        Category    = ModuleCategory.CraftGather,
        ModulesPrerequisite =
        [
            "FieldEntryCommand",
            "AutoCommenceDuty",
            "InstantLogout"
        ]
    };

    public override ModulePermission Permission { get; } = new() { NeedAuth = true };

    private Config config = null!;

    private ZoneSelectCombo zoneSelectCombo = null!;

    private bool isQuestUnlocked;

    protected override void Init()
    {
        zoneSelectCombo =   new("BlacklistZone");
        config          =   Config.Load(this) ?? new();
        TaskHelper      ??= new() { TimeoutMS = 30_000, ShowDebug = true };

        zoneSelectCombo.SelectedIDs = config.BlacklistZones;

        LogMessageManager.Instance().RegPost(OnPost);
    }

    protected override void Uninit() =>
        LogMessageManager.Instance().Unreg(OnPost);

    protected override void ConfigUI()
    {
        var questRow = LuminaGetter.GetRowOrDefault<Quest>(QUEST_ID);

        if (Throttler.Shared.Throttle("AutoEliminateFishAwareness.UpdateQuest"))
            isQuestUnlocked = IUnlockState.Instance().IsQuestCompleted(questRow);

        using (ImRaii.Heading1(Lang.Get("PreCondition")))
        {
            ImGui.Bullet();

            ImGui.SameLine();

            using (ImRaii.Group())
            {
                ImGui.TextUnformatted
                (
                    Lang.Get
                    (
                        "AutoEliminateFishAwareness-PreCondition-Quest",
                        new Dictionary<string, object>
                        {
                            ["quest"] = questRow.Name
                        }
                    )
                );

                ImGui.SameLine();

                using (ImRaii.PushColor(ImGuiCol.Text, KnownColor.LawnGreen.ToUInt(), isQuestUnlocked)
                             .Push(ImGuiCol.Text, KnownColor.OrangeRed.ToUInt(), !isQuestUnlocked))
                {
                    ImGui.TextUnformatted
                    (
                        isQuestUnlocked ?
                            "√" :
                            "X"
                    );
                }
            }

            if (ImGui.IsItemHovered())
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);

            if (ImGui.IsItemClicked())
            {
                var locationData = questRow.IssuerLocation.Value;
                AgentMap.Instance()->SetMapFlagAndOpen
                (
                    locationData.Map.RowId,
                    locationData.GetPosition(),
                    questRow.Name.ToString()
                );
            }
        }

        ImGui.NewLine();

        using (ImRaii.Heading1(Lang.Get("BlacklistZones")))
        {
            ImGui.SetNextItemWidth(300f * GlobalUIScale);

            if (zoneSelectCombo.DrawCheckbox())
            {
                config.BlacklistZones = zoneSelectCombo.SelectedIDs;
                config.Save(this);
            }
        }

        ImGui.NewLine();

        using (ImRaii.Heading1
               (
                   Lang.Get("AutoEliminateFishAwareness-ExtraCommands"),
                   Lang.Get("AutoEliminateFishAwareness-ExtraCommands-Help")
               ))
        {
            ImGui.InputTextMultiline("###ExtraCommandsInput", ref config.ExtraCommands, 2048);

            if (ImGui.IsItemDeactivatedAfterEdit())
            {
                config.ExtraCommands = string.Join("\n", config.ExtraCommands.Split(["\r\n", "\n", "\r"], StringSplitOptions.None).Take(15));
                config.Save(this);
            }
        }

        ImGui.NewLine();

        if (ImGui.Checkbox(Lang.Get("AutoEliminateFishAwareness-LogoutWhenGlobalWarning"), ref config.LogoutWhenGlobalWarning))
            config.Save(this);
    }

    private void OnPost
    (
        uint                logMessageID,
        LogMessageQueueItem item
    )
    {
        if (config.BlacklistZones.Contains(GameState.TerritoryType))
            return;

        switch (logMessageID)
        {
            case 5518:
                var message = Lang.Get("AutoEliminateFishAwareness-Notification-GlobalWarning");

                NotifyHelper.Instance().ChatError(message);
                NotifyHelper.ToastError(message);
                NotifyHelper.Instance().TrayError(message);

                if (config.LogoutWhenGlobalWarning)
                    ChatManager.Instance().SendCommand("/logout");
                break;

            // 非全局警惕
            case 3516 or 5517:
                TaskHelper.Abort();

                // 云冠群岛
                if (GameState.TerritoryType == 939)
                {
                    var currentPosition = IObjectTable.Instance().LocalPlayer.Position;
                    var currentRotation = IObjectTable.Instance().LocalPlayer.Rotation;

                    TaskHelper.Enqueue
                    (
                        ExitFishing,
                        "离开钓鱼状态"
                    );
                    TaskHelper.DelayNext
                    (
                        5_000,
                        "等待 5 秒"
                    );
                    TaskHelper.Enqueue
                    (
                        () => !ICondition.Instance().IsOccupiedInEvent,
                        "等待不在钓鱼状态"
                    );
                    TaskHelper.Enqueue
                    (
                        () => ExitDuty(753),
                        "离开副本"
                    );
                    TaskHelper.Enqueue
                    (
                        () => !ICondition.Instance().IsBoundByDuty &&
                              UIModule.IsScreenReady()             &&
                              GameState.TerritoryType != 939,
                        "等待离开副本"
                    );
                    TaskHelper.Enqueue
                    (
                        () => ChatManager.Instance().SendMessage("/pdrfe diadem"),
                        "发送进入指令"
                    );
                    TaskHelper.Enqueue
                    (
                        () => GameState.TerritoryType             == 939 &&
                              IObjectTable.Instance().LocalPlayer != null,
                        "等待进入"
                    );
                    TaskHelper.Enqueue
                    (
                        () => MovementManager.Instance().TPSmart_InZone(currentPosition),
                        $"传送到原始位置 {currentPosition}"
                    );
                    TaskHelper.DelayNext
                    (
                        500,
                        "等待 500 毫秒"
                    );
                    TaskHelper.Enqueue
                    (
                        () => !MovementManager.Instance().IsManagerBusy,
                        "等待传送完毕"
                    );
                    TaskHelper.Enqueue
                    (
                        () => IObjectTable.Instance().LocalPlayer.ToStruct()->SetRotation(currentRotation),
                        "设置面向"
                    );
                }
                else if (!ICondition.Instance().IsBoundByDuty)
                {
                    TaskHelper.Enqueue
                    (
                        ExitFishing,
                        "离开钓鱼状态"
                    );
                    TaskHelper.DelayNext
                    (
                        5_000,
                        "等待 5 秒"
                    );
                    TaskHelper.Enqueue
                    (
                        () => !ICondition.Instance().IsOccupiedInEvent,
                        "等待离开忙碌状态"
                    );
                    TaskHelper.Enqueue
                    (
                        () => ContentsFinderHelper.RequestDutyNormal(TARGET_CONTENT, ContentsFinderHelper.DefaultOption),
                        "申请目标副本"
                    );
                    TaskHelper.Enqueue
                    (
                        () => ExitDuty(TARGET_CONTENT),
                        "离开目标副本"
                    );
                }
                else
                    return;

                TaskHelper.Enqueue
                (
                    () => ActionManager.Instance()->GetActionStatus(ActionType.Action, 289) == 0,
                    "等待技能抛竿可用"
                );

                TaskHelper.Enqueue
                (
                    () =>
                    {
                        if (string.IsNullOrWhiteSpace(config.ExtraCommands))
                            return;

                        ChatManager.Instance().ExecuteMacro(config.ExtraCommands);
                    },
                    "执行文本指令"
                );
                break;
        }
    }

    private static bool ExitFishing()
    {
        if (!Throttler.Shared.Throttle("AutoEliminateFishAwareness-ExitFishing")) return false;

        ExecuteCommandManager.Instance().ExecuteCommand(ExecuteCommandFlag.Fishing, 1);
        return !ICondition.Instance()[ConditionFlag.Fishing];
    }

    private static bool ExitDuty
    (
        uint targetContent
    )
    {
        if (!Throttler.Shared.Throttle("AutoEliminateFishAwareness-ExitDuty")) return false;
        if (GameState.ContentFinderCondition != targetContent) return false;

        ExecuteCommandManager.Instance().ExecuteCommand(ExecuteCommandFlag.NotifyTerritoryReady);
        ExecuteCommandManager.Instance().ExecuteCommand(ExecuteCommandFlag.LeaveDuty);
        return true;
    }

    private class Config : ModuleConfig
    {
        public HashSet<uint> BlacklistZones = [];
        public string        ExtraCommands  = string.Empty;

        public bool LogoutWhenGlobalWarning;
    }

    #region 常量

    private const uint TARGET_CONTENT = 195;
    private const uint QUEST_ID       = 65973;

    #endregion
}
