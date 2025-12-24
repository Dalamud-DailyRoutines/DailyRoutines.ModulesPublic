using System;
using System.Collections.Generic;
using DailyRoutines.Abstracts;
using DailyRoutines.Managers;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using FFXIVClientStructs.FFXIV.Client.Game;

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

    private const uint TargetContent = 195;

    private static readonly HashSet<string> ValidChatMessages = new(StringComparer.OrdinalIgnoreCase)
    {
        LuminaWrapper.GetLogMessageText(3516),
        LuminaWrapper.GetLogMessageText(5517),
        LuminaWrapper.GetLogMessageText(5518),
    };

    private static Config ModuleConfig = null!;
    
    private static string ZoneSearchInput = string.Empty;

    protected override void Init()
    {
        ModuleConfig =   LoadConfig<Config>() ?? new();
        TaskHelper   ??= new() { TimeLimitMS = 30_000, ShowDebug = true };

        DService.Chat.ChatMessage += OnChatMessage;
    }

    protected override void ConfigUI()
    {
        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), GetLoc("BlacklistZones"));

        using (ImRaii.PushIndent())
        {
            ImGui.SetNextItemWidth(300f * GlobalFontScale);
            if (ZoneSelectCombo(ref ModuleConfig.BlacklistZones, ref ZoneSearchInput))
                SaveConfig(ModuleConfig);
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
        if (!ValidChatMessages.Contains(message.ExtractText())) return;

        TaskHelper.Abort();

        // 云冠群岛
        if (GameState.TerritoryType == 939)
        {
            var currentPos      = DService.ObjectTable.LocalPlayer.Position;
            var currentRotation = DService.ObjectTable.LocalPlayer.Rotation;

            TaskHelper.Enqueue(ExitFishing, "离开钓鱼状态");
            TaskHelper.DelayNext(5_000, "等待 5 秒");
            TaskHelper.Enqueue(() => !OccupiedInEvent,                                                           "等待不在钓鱼状态");
            TaskHelper.Enqueue(() => ExitDuty(753),                                                              "离开副本");
            TaskHelper.Enqueue(() => !BoundByDuty && IsScreenReady() && GameState.TerritoryType != 939,          "等待离开副本");
            TaskHelper.Enqueue(() => ChatManager.SendMessage("/pdrfe diadem"),                                    "发送进入指令");
            TaskHelper.Enqueue(() => GameState.TerritoryType == 939 && DService.ObjectTable.LocalPlayer != null, "等待进入");
            TaskHelper.Enqueue(() => MovementManager.TPSmart_InZone(currentPos),                                 $"传送到原始位置 {currentPos}");
            TaskHelper.DelayNext(500, "等待 500 毫秒");
            TaskHelper.Enqueue(() => !MovementManager.IsManagerBusy,                                            "等待传送完毕");
            TaskHelper.Enqueue(() => DService.ObjectTable.LocalPlayer.ToStruct()->SetRotation(currentRotation), "设置面向");
        }
        else if (!BoundByDuty)
        {
            TaskHelper.Enqueue(ExitFishing, "离开钓鱼状态");
            TaskHelper.DelayNext(5_000);
            TaskHelper.Enqueue(() => !OccupiedInEvent, "等待离开忙碌状态");
            TaskHelper.Enqueue(() => RequestDutyNormal(TargetContent, new() { Config817to820 = true }), "申请目标副本");
            TaskHelper.Enqueue(() => ExitDuty(TargetContent), "离开目标副本");
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
                ChatManager.SendMessage(command);
        }, "执行文本指令");
    }

    private static bool? ExitFishing()
    {
        if (!Throttler.Throttle("AutoEliminateFishAwareness-ExitFishing")) return false;
        
        ExecuteCommandManager.ExecuteCommand(ExecuteCommandFlag.Fish, 1);
        return !DService.Condition[ConditionFlag.Fishing];
    }
    
    private static bool? ExitDuty(uint targetContent)
    {
        if (!Throttler.Throttle("AutoEliminateFishAwareness-ExitDuty")) return false;
        if (GameState.ContentFinderCondition != targetContent) return false;

        ExecuteCommandManager.ExecuteCommand(ExecuteCommandFlag.TerritoryTransportFinish);
        ExecuteCommandManager.ExecuteCommand(ExecuteCommandFlag.LeaveDuty);
        return true;
    }
    
    private static bool? EnterFishing()
    {
        if (!Throttler.Throttle("AutoEliminateFishAwareness-EnterFishing")) return false;
        if (DService.ObjectTable.LocalPlayer == null || BetweenAreas || !IsScreenReady()) return false;

        ExecuteCommandManager.ExecuteCommand(ExecuteCommandFlag.Fish);
        return DService.Condition[ConditionFlag.Fishing];
    }

    protected override void Uninit() => 
        DService.Chat.ChatMessage -= OnChatMessage;

    private class Config : ModuleConfiguration
    {
        public HashSet<uint> BlacklistZones = [];
        public string        ExtraCommands  = string.Empty;
        public bool          AutoCast       = true;
    }
}
