using System;
using System.Collections.Generic;
using DailyRoutines.Abstracts;
using DailyRoutines.Managers;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using OmenTools.Extensions;

namespace DailyRoutines.ModulesPublic;

public class AutoEliminateFishAwareness : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title               = GetLoc("AutoEliminateFishAwarenessTitle"),
        Description         = GetLoc("AutoEliminateFishAwarenessDescription"),
        Category            = ModuleCategories.General,
        ModulesPrerequisite = ["FieldEntryCommand", "AutoCommenceDuty"]
    };

    public override ModulePermission Permission { get; } = new() { NeedAuth = true };

    private const uint TARGET_CONTENT = 195;

    private static readonly HashSet<string> ValidChatMessages = new(StringComparer.OrdinalIgnoreCase)
    {
        LuminaWrapper.GetLogMessageText(3516),
        LuminaWrapper.GetLogMessageText(5517),
        LuminaWrapper.GetLogMessageText(5518),
    };

    private static Config ModuleConfig = null!;

    private static readonly ZoneSelectCombo ZoneSelectCombo = new("BlacklistZone");
    
    protected override void Init()
    {
        ModuleConfig =   LoadConfig<Config>() ?? new();
        TaskHelper   ??= new() { TimeoutMS = 30_000, ShowDebug = true };
        
        ZoneSelectCombo.SelectedIDs = ModuleConfig.BlacklistZones;

        DService.Instance().Chat.ChatMessage += OnChatMessage;
    }

    protected override void ConfigUI()
    {
        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), GetLoc("BlacklistZones"));

        using (ImRaii.PushIndent())
        {
            ImGui.SetNextItemWidth(300f * GlobalFontScale);
            if (ZoneSelectCombo.DrawCheckbox())
            {
                ModuleConfig.BlacklistZones = ZoneSelectCombo.SelectedIDs;
                SaveConfig(ModuleConfig);
            }
        }
        
        ImGui.NewLine();
        
        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), GetLoc("AutoEliminateFishAwareness-ExtraCommands"));
        ImGuiOm.HelpMarker(GetLoc("AutoEliminateFishAwareness-ExtraCommandsHelp"));
        
        using (ImRaii.PushIndent())
        {
            ImGui.InputTextMultiline("###ExtraCommandsInput", ref ModuleConfig.ExtraCommands, 2048, ScaledVector2(400f, 120f));
            if (ImGui.IsItemDeactivatedAfterEdit())
                SaveConfig(ModuleConfig);
        }
        
        ImGui.NewLine();
        
        if (ImGui.Checkbox(GetLoc("AutoEliminateFishAwareness-AutoCast"), ref ModuleConfig.AutoCast))
            SaveConfig(ModuleConfig);
        ImGuiOm.HelpMarker(GetLoc("AutoEliminateFishAwareness-AutoCastHelp"));
    }

    private unsafe void OnChatMessage(
        XivChatType  type,
        int          timestamp,
        ref SeString sender,
        ref SeString message,
        ref bool     ishandled)
    {
        if ((ushort)type != 2243 || ModuleConfig.BlacklistZones.Contains(GameState.TerritoryType)) return;
        if (!ValidChatMessages.Contains(message.ToString())) return;

        TaskHelper.Abort();

        // 云冠群岛
        if (GameState.TerritoryType == 939)
        {
            var currentPos      = DService.Instance().ObjectTable.LocalPlayer.Position;
            var currentRotation = DService.Instance().ObjectTable.LocalPlayer.Rotation;

            TaskHelper.Enqueue(ExitFishing, "离开钓鱼状态");
            TaskHelper.DelayNext(5_000, "等待 5 秒");
            TaskHelper.Enqueue(() => !OccupiedInEvent,                                                           "等待不在钓鱼状态");
            TaskHelper.Enqueue(() => ExitDuty(753),                                                              "离开副本");
            TaskHelper.Enqueue(() => !BoundByDuty && UIModule.IsScreenReady() && GameState.TerritoryType != 939, "等待离开副本");
            TaskHelper.Enqueue(() => ChatManager.Instance().SendMessage("/pdrfe diadem"),                                   "发送进入指令");
            TaskHelper.Enqueue(() => GameState.TerritoryType == 939 && DService.Instance().ObjectTable.LocalPlayer != null, "等待进入");
            TaskHelper.Enqueue(() => MovementManager.TPSmart_InZone(currentPos),                                 $"传送到原始位置 {currentPos}");
            TaskHelper.DelayNext(500, "等待 500 毫秒");
            TaskHelper.Enqueue(() => !MovementManager.IsManagerBusy,                                            "等待传送完毕");
            TaskHelper.Enqueue(() => DService.Instance().ObjectTable.LocalPlayer.ToStruct()->SetRotation(currentRotation), "设置面向");
        }
        else if (!BoundByDuty)
        {
            TaskHelper.Enqueue(ExitFishing, "离开钓鱼状态");
            TaskHelper.DelayNext(5_000);
            TaskHelper.Enqueue(() => !OccupiedInEvent,                                                                           "等待离开忙碌状态");
            TaskHelper.Enqueue(() => ContentsFinderHelper.RequestDutyNormal(TARGET_CONTENT, ContentsFinderHelper.DefaultOption), "申请目标副本");
            TaskHelper.Enqueue(() => ExitDuty(TARGET_CONTENT),                                                                   "离开目标副本");
        }
        else
            return;

        if (ModuleConfig.AutoCast)
            TaskHelper.Enqueue(EnterFishing, "进入钓鱼状态");
        else
            TaskHelper.Enqueue(() => ActionManager.Instance()->GetActionStatus(ActionType.Action, 289) == 0, "等待技能抛竿可用");
        
        TaskHelper.Enqueue(() =>
        {
            if (string.IsNullOrWhiteSpace(ModuleConfig.ExtraCommands)) return;
            
            foreach (var command in ModuleConfig.ExtraCommands.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                ChatManager.Instance().SendMessage(command);
        }, "执行文本指令");
    }

    private static bool ExitFishing()
    {
        if (!Throttler.Throttle("AutoEliminateFishAwareness-ExitFishing")) return false;
        
        ExecuteCommandManager.Instance().ExecuteCommand(ExecuteCommandFlag.Fish, 1);
        return !DService.Instance().Condition[ConditionFlag.Fishing];
    }
    
    private static bool ExitDuty(uint targetContent)
    {
        if (!Throttler.Throttle("AutoEliminateFishAwareness-ExitDuty")) return false;
        if (GameState.ContentFinderCondition != targetContent) return false;

        ExecuteCommandManager.Instance().ExecuteCommand(ExecuteCommandFlag.TerritoryTransportFinish);
        ExecuteCommandManager.Instance().ExecuteCommand(ExecuteCommandFlag.LeaveDuty);
        return true;
    }
    
    private static bool EnterFishing()
    {
        if (!Throttler.Throttle("AutoEliminateFishAwareness-EnterFishing")) return false;
        if (DService.Instance().ObjectTable.LocalPlayer == null || BetweenAreas || !UIModule.IsScreenReady()) return false;

        ExecuteCommandManager.Instance().ExecuteCommand(ExecuteCommandFlag.Fish);
        return DService.Instance().Condition[ConditionFlag.Fishing];
    }

    protected override void Uninit() => 
        DService.Instance().Chat.ChatMessage -= OnChatMessage;

    private class Config : ModuleConfiguration
    {
        public HashSet<uint> BlacklistZones = [];
        public string        ExtraCommands  = string.Empty;
        public bool          AutoCast       = true;
    }
}
