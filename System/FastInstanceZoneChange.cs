using System.Collections.Frozen;
using System.Numerics;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using DailyRoutines.Manager;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Game.Gui.Dtr;
using Dalamud.Utility.Numerics;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI;
using Lumina.Excel.Sheets;
using OmenTools.Info.Game.Enums;
using OmenTools.Interop.Game.Helpers;
using OmenTools.Interop.Game.Lumina;
using OmenTools.Interop.Game.Models.Packets.Upstream;
using OmenTools.OmenService;
using OmenTools.Threading;
using ModuleBase = DailyRoutines.Common.Module.Abstractions.ModuleBase;
using TerritoryIntendedUse = FFXIVClientStructs.FFXIV.Client.Enums.TerritoryIntendedUse;

namespace DailyRoutines.ModulesPublic;

public unsafe class FastInstanceZoneChange : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title            = Lang.Get("FastInstanceZoneChangeTitle"),
        Description      = Lang.Get("FastInstanceZoneChangeDescription", COMMAND),
        Category         = ModuleCategory.System,
        Author           = ["AtmoOmen", "KirisameVanilla"],
        ModulesRecommend = ["InstantTeleport"]
    };

    public override ModulePermission Permission { get; } = new() { AllDefaultEnabled = true };

    private Config        config = null!;
    private IDtrBarEntry? entry;

    protected override void Init()
    {
        TaskHelper   ??= new() { TimeoutMS = 30_000, ShowDebug = true };
        config =   Config.Load(this) ?? new();

        CommandManager.Instance().AddSubCommand(COMMAND, new(OnCommand) { HelpMessage = Lang.Get("FastInstanceZoneChange-CommandHelp") });

        if (config.AddDtrEntry)
        {
            HandleDtrEntry(true);
            OnConditionChanged(ConditionFlag.BetweenAreas, false);
        }

        DService.Instance().Condition.ConditionChange += OnConditionChanged;
    }

    protected override void Uninit()
    {
        DService.Instance().Condition.ConditionChange -= OnConditionChanged;

        HandleDtrEntry(false);
        CommandManager.Instance().RemoveSubCommand(COMMAND);
    }

    protected override void ConfigUI()
    {
        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), $"{Lang.Get("Command")}:");
        using (ImRaii.PushIndent())
            ImGui.TextUnformatted($"/pdr {COMMAND} \u2192 {Lang.Get("FastInstanceZoneChange-CommandHelp")}");

        ImGui.NewLine();

        if (ImGui.Checkbox(Lang.Get("FastInstanceZoneChange-TeleportIfNotNearAetheryte"), ref config.TeleportIfNotNearAetheryte))
            config.Save(this);

        if (ImGui.Checkbox(Lang.Get("FastInstanceZoneChange-ConstantlyTry"), ref config.ConstantlyTry))
            config.Save(this);

        if (ImGui.Checkbox(Lang.Get("FastInstanceZoneChange-MountAfterChange"), ref config.MountAfterChange))
            config.Save(this);

        if (ImGui.Checkbox(Lang.Get("FastInstanceZoneChange-AddDtrEntry"), ref config.AddDtrEntry))
        {
            config.Save(this);
            HandleDtrEntry(config.AddDtrEntry);
        }

        if (config.AddDtrEntry)
        {
            if (ImGui.Checkbox(Lang.Get("FastInstanceZoneChange-CloseAfterUsage"), ref config.CloseAfterUsage))
                config.Save(this);
        }
    }

    protected override void OverlayUI()
    {
        if (!InstancesManager.IsInstancedArea)
        {
            Overlay.IsOpen = false;
            return;
        }

        if (DService.Instance().KeyState[VirtualKey.ESCAPE])
        {
            Overlay.IsOpen = false;
            if (SystemMenu != null)
                SystemMenu->Close(true);
            return;
        }

        if (!ValidUses.Contains(GameState.TerritoryIntendedUse))
        {
            Overlay.IsOpen = false;
            return;
        }

        ImGui.SetWindowPos(ImGui.GetMainViewport().Size / 2 - new Vector2(0.5f));

        var count = InstancesManager.Instance().GetInstancesCount();

        for (uint i = 1; i <= count; i++)
        {
            if (i == InstancesManager.CurrentInstance) continue;

            if (ImGui.Button($"{Lang.Get("FastInstanceZoneChange-SwitchInstance", i.ToSESquareCount())}") |
                DService.Instance().KeyState[(VirtualKey)(48 + i)])
            {
                if (TaskHelper.IsBusy || DService.Instance().Condition.IsBetweenAreas || DService.Instance().Condition[ConditionFlag.Casting]) continue;
                ChatManager.Instance().SendMessage($"/pdr insc {i}");
                if (config.CloseAfterUsage)
                    Overlay.IsOpen = false;
            }
        }
    }

    private void OnConditionChanged(ConditionFlag flag, bool value)
    {
        if (flag != ConditionFlag.BetweenAreas || value) return;
        if (!config.AddDtrEntry          || entry == null) return;

        entry.Text = !InstancesManager.IsInstancedArea
                         ? string.Empty
                         : Lang.Get("AutoMarksFinder-RelayInstanceDisplay", InstancesManager.CurrentInstance.ToSESquareCount());
        entry.Shown   = InstancesManager.IsInstancedArea;
        entry.Tooltip = ValidUses.Contains(GameState.TerritoryIntendedUse) ? Lang.Get("FastInstanceZoneChange-DtrEntryTooltip") : string.Empty;
    }

    private void OnCommand(string command, string args)
    {
        args = args.Trim().ToLowerInvariant();

        var publicInstance = UIState.Instance()->PublicInstance;

        if (args.Length == 0)
        {
            if (publicInstance.IsInstancedArea())
                Overlay.IsOpen ^= true;
            else
                NotifyHelper.Instance().NotificationError(Lang.Get("FastInstanceZoneChange-Notice-NoInstanceZones"));
            return;
        }

        if (args == "abort")
        {
            TaskHelper.Abort();
            NotifyHelper.Instance().NotificationInfo(Lang.Get("FastInstanceZoneChange-Notice-Aborted"));
            return;
        }

        if (!uint.TryParse(args, out var targetInstance))
        {
            NotifyHelper.Instance().NotificationError(Lang.Get("FastInstanceZoneChange-Notice-InvalidArgs", args));
            return;
        }

        if (!publicInstance.IsInstancedArea())
        {
            NotifyHelper.Instance().NotificationError(Lang.Get("FastInstanceZoneChange-Notice-NoInstanceZones"));
            return;
        }

        if (publicInstance.InstanceId == targetInstance)
        {
            NotifyHelper.Instance().NotificationError(Lang.Get("FastInstanceZoneChange-Notice-CurrentlyInSameInstance", targetInstance));
            return;
        }

        TaskHelper.Abort();
        NotifyHelper.Instance().NotificationInfo(Lang.Get("FastInstanceZoneChange-Notice-Change", targetInstance));

        var isAnyAetheryteNearby = IsAnyAetheryteNearby(out _);

        if (config.TeleportIfNotNearAetheryte && !isAnyAetheryteNearby)
        {
            TaskHelper.Enqueue(() => MovementManager.Instance().TeleportNearestAetheryte(default, GameState.TerritoryType, true), "传送到目标区域最近以太之光", weight: 2);
            TaskHelper.DelayNext(500, "等待传送开始", 2);
            TaskHelper.Enqueue
            (
                () =>
                {
                    if (!Throttler.Shared.Throttle("FastInstanceZoneChange-WaitTeleportFinish")) return false;
                    return IsAnyAetheryteNearby(out _);
                },
                "传送到目标区域最近以太之光",
                weight: 2
            );
        }

        var currentMountID = 0U;
        if (DService.Instance().Condition[ConditionFlag.Mounted])
            currentMountID = DService.Instance().ObjectTable.LocalPlayer.CurrentMount?.RowId ?? 0;

        TaskHelper.Enqueue
        (
            () =>
            {
                if (!DService.Instance().Condition[ConditionFlag.Mounted]) return true;
                if (!Throttler.Shared.Throttle("FastInstanceZoneChange-WaitDismount", 100)) return false;

                ExecuteCommandManager.Instance().ExecuteCommand(ExecuteCommandFlag.Dismount);

                if (RaycastHelper.TryGetGroundPosition
                    (
                        DService.Instance().ObjectTable.LocalPlayer.Position.WithY(300),
                        out var groundPosition
                    ))
                {
                    MovementManager.Instance().TPMountAddress(groundPosition with { Y = groundPosition.Y - 0.5f });
                    UseActionManager.Instance().UseAction(ActionType.GeneralAction, 9);
                }

                return !DService.Instance().Condition[ConditionFlag.Mounted];
            },
            "下坐骑",
            weight: 2
        );

        if (config.ConstantlyTry)
            TaskHelper.Enqueue(() => EnqueueInstanceChange(targetInstance, 0), "开始持续尝试切换副本区", weight: 2);
        else
            TaskHelper.Enqueue(() => ChangeInstanceZone(targetInstance), "切换副本区", weight: 2);

        TaskHelper.Enqueue
        (
            () =>
            {
                if (InstancesManager.CurrentInstance != targetInstance ||
                    DService.Instance().Condition.IsBetweenAreas)
                    return false;

                return true;
            },
            "等待切换完毕, 更新副本区信息"
        );

        if (config.MountAfterChange)
        {
            TaskHelper.Enqueue
            (
                () =>
                {
                    if (!UIModule.IsScreenReady())
                        return false;

                    // 上不了坐骑
                    if (ActionManager.Instance()->GetActionStatus(ActionType.GeneralAction, 9) != 0)
                        return true;

                    if (currentMountID != 0)
                        UseActionManager.Instance().UseAction(ActionType.Mount, currentMountID);
                    else
                        UseActionManager.Instance().UseAction(ActionType.GeneralAction, 9);

                    return true;
                },
                "切换完毕后上坐骑"
            );
        }
    }

    private void HandleDtrEntry(bool isAdd)
    {
        switch (isAdd)
        {
            case true when entry == null:
                entry         ??= DService.Instance().DTRBar.Get("DailyRoutines-FastInstanceZoneChange");
                entry.OnClick =  _ =>
                {
                    Overlay ??= new(this)
                    {
                        WindowName = Lang.Get("FastInstanceZoneChangeTitle")
                    };

                    Overlay.IsOpen ^= true;
                };
                entry.Shown   =   false;
                entry.Tooltip =   Lang.Get("FastInstanceZoneChange-DtrEntryTooltip");
                return;
            case false:
                entry?.Remove();
                entry = null;
                break;
        }

    }

    public void EnqueueInstanceChange(uint i, uint tryTimes)
    {
        // 等待上一次切换完成
        TaskHelper.Enqueue(() => SelectString->IsAddonAndNodesReady() || !DService.Instance().Condition[ConditionFlag.BetweenAreas], "等待上一次切换完毕", weight: 2);

        // 检测切换情况
        TaskHelper.Enqueue
        (
            () =>
            {
                if (!Throttler.Shared.Throttle("FastInstanceZoneChange-DetectInstances")) return false;

                if (DService.Instance().Condition[ConditionFlag.BetweenAreas])
                {
                    ExecuteCommandManager.Instance().ExecuteCommand(ExecuteCommandFlag.TerritoryTransport);
                    return false;
                }

                var publicInstance = UIState.Instance()->PublicInstance;
                if (!publicInstance.IsInstancedArea() || publicInstance.InstanceId == i)
                    TaskHelper.RemoveQueueTasks(2);

                return true;
            },
            "检测切换情况",
            weight: 2
        );

        // 实际切换指令
        TaskHelper.Enqueue(() => ChangeInstanceZone(i), "开始切换", weight: 2);

        // 发送提示信息
        if (tryTimes > 0)
            TaskHelper.Enqueue(() => NotifyHelper.Instance().NotificationInfo(Lang.Get("FastInstanceZoneChange-Notice-ChangeTimes", tryTimes)), "发送提示信息", weight: 2);

        // 延迟下一次检测
        TaskHelper.DelayNext(1_500, "等待 1.5 秒后继续", 2);
        TaskHelper.Enqueue(() => EnqueueInstanceChange(i, tryTimes++), "开始新一轮切换", weight: 2);
    }

    internal static void ChangeInstanceZone(uint i)
    {
        if (!UIState.Instance()->PublicInstance.IsInstancedArea() ||
            !IsAnyAetheryteNearby(out var eventID))
            return;

        new EventStartPackt(LocalPlayerState.EntityID, eventID).Send();
        new EventCompletePackt(eventID, 33554432, 7, i + 1).Send();
    }

    private static bool IsAnyAetheryteNearby(out uint eventID)
    {
        eventID = 0;

        foreach (var eve in EventFramework.Instance()->EventHandlerModule.EventHandlerMap)
        {
            if (eve.Item2.Value->Info.EventId.ContentId != EventHandlerContent.Aetheryte) continue;

            foreach (var obj in eve.Item2.Value->EventObjects)
            {
                if (obj.Value->NameString == LuminaGetter.GetRow<Aetheryte>(0)!.Value.Singular.ToString())
                {
                    eventID = eve.Item2.Value->Info.EventId;
                    return true;
                }
            }
        }

        return false;
    }

    private class Config : ModuleConfig
    {
        public bool AddDtrEntry                = true;
        public bool CloseAfterUsage            = true;
        public bool ConstantlyTry              = true;
        public bool MountAfterChange           = true;
        public bool TeleportIfNotNearAetheryte = true;
    }
    
    #region 常量

    private const string COMMAND = "insc";

    // 其他地方也没法换线
    private static readonly FrozenSet<TerritoryIntendedUse> ValidUses =
    [
        TerritoryIntendedUse.Overworld,
        TerritoryIntendedUse.Town
    ];

    #endregion
}
