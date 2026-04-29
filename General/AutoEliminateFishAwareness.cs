using System.Collections.Frozen;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using DailyRoutines.Manager;
using Dalamud.Game.Chat;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using OmenTools.ImGuiOm.Widgets.Combos;
using OmenTools.Info.Game.Enums;
using OmenTools.Interop.Game.Helpers;
using OmenTools.Interop.Game.Lumina;
using OmenTools.OmenService;
using OmenTools.Threading;

namespace DailyRoutines.ModulesPublic;

public class AutoEliminateFishAwareness : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title               = Lang.Get("AutoEliminateFishAwarenessTitle"),
        Description         = Lang.Get("AutoEliminateFishAwarenessDescription"),
        Category            = ModuleCategory.General,
        ModulesPrerequisite = ["FieldEntryCommand", "AutoCommenceDuty"]
    };

    public override ModulePermission Permission { get; } = new() { NeedAuth = true };

    private Config config = null!;

    private readonly ZoneSelectCombo zoneSelectCombo = new("BlacklistZone");

    protected override void Init()
    {
        config     =   Config.Load(this) ?? new();
        TaskHelper ??= new() { TimeoutMS = 30_000, ShowDebug = true };

        zoneSelectCombo.SelectedIDs = config.BlacklistZones;

        DService.Instance().Chat.ChatMessage += OnChatMessage;
    }

    protected override void Uninit() =>
        DService.Instance().Chat.ChatMessage -= OnChatMessage;

    protected override void ConfigUI()
    {
        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), Lang.Get("BlacklistZones"));

        using (ImRaii.PushIndent())
        {
            ImGui.SetNextItemWidth(300f * GlobalUIScale);

            if (zoneSelectCombo.DrawCheckbox())
            {
                config.BlacklistZones = zoneSelectCombo.SelectedIDs;
                config.Save(this);
            }
        }

        ImGui.NewLine();

        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), Lang.Get("AutoEliminateFishAwareness-ExtraCommands"));
        ImGuiOm.HelpMarker(Lang.Get("AutoEliminateFishAwareness-ExtraCommandsHelp"));

        using (ImRaii.PushIndent())
        {
            ImGui.InputTextMultiline("###ExtraCommandsInput", ref config.ExtraCommands, 2048, ScaledVector2(400f, 120f));
            if (ImGui.IsItemDeactivatedAfterEdit())
                config.Save(this);
        }

        ImGui.NewLine();

        if (ImGui.Checkbox(Lang.Get("AutoEliminateFishAwareness-AutoCast"), ref config.AutoCast))
            config.Save(this);
        ImGuiOm.HelpMarker(Lang.Get("AutoEliminateFishAwareness-AutoCastHelp"));
    }

    private unsafe void OnChatMessage
    (
        IHandleableChatMessage message
    )
    {
        if ((ushort)message.LogKind != 2243 || config.BlacklistZones.Contains(GameState.TerritoryType)) return;
        if (!ValidChatMessages.Contains(message.ToString())) return;

        TaskHelper.Abort();

        // 云冠群岛
        if (GameState.TerritoryType == 939)
        {
            var currentPos      = DService.Instance().ObjectTable.LocalPlayer.Position;
            var currentRotation = DService.Instance().ObjectTable.LocalPlayer.Rotation;

            TaskHelper.Enqueue(ExitFishing, "离开钓鱼状态");
            TaskHelper.DelayNext(5_000, "等待 5 秒");
            TaskHelper.Enqueue(() => !DService.Instance().Condition.IsOccupiedInEvent, "等待不在钓鱼状态");
            TaskHelper.Enqueue(() => ExitDuty(753), "离开副本");
            TaskHelper.Enqueue(() => !DService.Instance().Condition.IsBoundByDuty && UIModule.IsScreenReady() && GameState.TerritoryType != 939, "等待离开副本");
            TaskHelper.Enqueue(() => ChatManager.Instance().SendMessage("/pdrfe diadem"), "发送进入指令");
            TaskHelper.Enqueue(() => GameState.TerritoryType == 939 && DService.Instance().ObjectTable.LocalPlayer != null, "等待进入");
            TaskHelper.Enqueue(() => MovementManager.Instance().TPSmart_InZone(currentPos), $"传送到原始位置 {currentPos}");
            TaskHelper.DelayNext(500, "等待 500 毫秒");
            TaskHelper.Enqueue(() => !MovementManager.Instance().IsManagerBusy,                                            "等待传送完毕");
            TaskHelper.Enqueue(() => DService.Instance().ObjectTable.LocalPlayer.ToStruct()->SetRotation(currentRotation), "设置面向");
        }
        else if (!DService.Instance().Condition.IsBoundByDuty)
        {
            TaskHelper.Enqueue(ExitFishing, "离开钓鱼状态");
            TaskHelper.DelayNext(5_000);
            TaskHelper.Enqueue(() => !DService.Instance().Condition.IsOccupiedInEvent,                                           "等待离开忙碌状态");
            TaskHelper.Enqueue(() => ContentsFinderHelper.RequestDutyNormal(TARGET_CONTENT, ContentsFinderHelper.DefaultOption), "申请目标副本");
            TaskHelper.Enqueue(() => ExitDuty(TARGET_CONTENT),                                                                   "离开目标副本");
        }
        else
            return;

        if (config.AutoCast)
            TaskHelper.Enqueue(EnterFishing, "进入钓鱼状态");
        else
            TaskHelper.Enqueue(() => ActionManager.Instance()->GetActionStatus(ActionType.Action, 289) == 0, "等待技能抛竿可用");

        TaskHelper.Enqueue
        (
            () =>
            {
                if (string.IsNullOrWhiteSpace(config.ExtraCommands)) return;

                foreach (var command in config.ExtraCommands.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                    ChatManager.Instance().SendMessage(command);
            },
            "执行文本指令"
        );
    }

    private static bool ExitFishing()
    {
        if (!Throttler.Shared.Throttle("AutoEliminateFishAwareness-ExitFishing")) return false;

        ExecuteCommandManager.Instance().ExecuteCommand(ExecuteCommandFlag.Fish, 1);
        return !DService.Instance().Condition[ConditionFlag.Fishing];
    }

    private static bool ExitDuty(uint targetContent)
    {
        if (!Throttler.Shared.Throttle("AutoEliminateFishAwareness-ExitDuty")) return false;
        if (GameState.ContentFinderCondition != targetContent) return false;

        ExecuteCommandManager.Instance().ExecuteCommand(ExecuteCommandFlag.TerritoryTransportFinish);
        ExecuteCommandManager.Instance().ExecuteCommand(ExecuteCommandFlag.LeaveDuty);
        return true;
    }

    private static bool EnterFishing()
    {
        if (!Throttler.Shared.Throttle("AutoEliminateFishAwareness-EnterFishing")) return false;
        if (DService.Instance().ObjectTable.LocalPlayer == null || DService.Instance().Condition.IsBetweenAreas || !UIModule.IsScreenReady()) return false;

        ExecuteCommandManager.Instance().ExecuteCommand(ExecuteCommandFlag.Fish);
        return DService.Instance().Condition[ConditionFlag.Fishing];
    }

    private class Config : ModuleConfig
    {
        public bool          AutoCast       = true;
        public HashSet<uint> BlacklistZones = [];
        public string        ExtraCommands  = string.Empty;
    }

    #region 常量

    private const uint TARGET_CONTENT = 195;

    private static readonly FrozenSet<string> ValidChatMessages = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        LuminaWrapper.GetLogMessageText(3516),
        LuminaWrapper.GetLogMessageText(5517),
        LuminaWrapper.GetLogMessageText(5518)
    }.ToFrozenSet();

    #endregion
}
