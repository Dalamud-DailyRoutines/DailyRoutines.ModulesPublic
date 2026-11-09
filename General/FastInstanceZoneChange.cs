using System.Numerics;
using DailyRoutines.Common.Extensions;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Game.Gui.Dtr;
using Dalamud.Utility.Numerics;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI;
using Lumina.Excel.Sheets;
using Lumina.Text.ReadOnly;
using OmenTools.Info.Game.Packets.Upstream;
using OmenTools.Interop.Game.Helpers;
using OmenTools.Interop.Game.Lumina;
using OmenTools.Threading;
using TerritoryIntendedUse = FFXIVClientStructs.FFXIV.Client.Enums.TerritoryIntendedUse;

namespace DailyRoutines.ModulesPublic;

internal unsafe class FastInstanceZoneChange : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("FastInstanceZoneChangeTitle"),
        Description = Lang.Get("FastInstanceZoneChangeDescription", COMMAND),
        Category    = ModuleCategory.General,
        Author      = ["AtmoOmen", "KirisameVanilla"],
        ModulesPair = ["InstantTeleport"]
    };

    public override string ModuleName => nameof(FastInstanceZoneChange);

    public override ModuleBehaviour Behaviour { get; } = new()
    {
        AllDefaultEnabled = true
    };

    private Config        config = null!;
    private IDtrBarEntry? entry;

    protected override void Init()
    {
        TaskHelper ??= new() { TimeoutMS = 30_000, ShowDebug = true };
        config     =   Config.Load(this) ?? new();

        CommandManager.Instance().AddSubCommand
        (
            COMMAND,
            new(OnCommand)
            {
                HelpMessage = Lang.Get("FastInstanceZoneChange-CommandHelp")
            }
        );

        if (config.AddDtrEntry)
        {
            HandleDtrEntry(true);
        }

        GameState.Instance().WarpComplete += OnWarpComplete;
    }

    protected override void Uninit()
    {
        GameState.Instance().WarpComplete -= OnWarpComplete;

        HandleDtrEntry(false);
        CommandManager.Instance().RemoveSubCommand(COMMAND);
    }

    protected override void ConfigUI()
    {
        using (ImRaii.Heading1(Lang.Get("Command")))
            ImGui.TextUnformatted($"/pdr {COMMAND} {Lang.Get("FastInstanceZoneChange-CommandHelp")}");

        ImGui.NewLine();

        if (ImGui.Checkbox(Lang.Get("FastInstanceZoneChange-TeleportIfNotNearAetheryte"), ref config.TeleportIfNotNearAetheryte))
            config.Save(this);

        if (ImGui.Checkbox(Lang.Get("FastInstanceZoneChange-ConstantlyTry"), ref config.ConstantlyTry))
            config.Save(this);

        if (ImGui.Checkbox(Lang.Get("FastInstanceZoneChange-AddDTREntry"), ref config.AddDtrEntry))
        {
            config.Save(this);
            HandleDtrEntry(config.AddDtrEntry);
        }
    }

    protected override void OverlayUI()
    {
        if (!InstancesManager.IsInstancedArea ||
            !ValidUses.Contains(GameState.TerritoryIntendedUse))
        {
            Overlay.IsOpen = false;
            return;
        }
        
        var count = InstancesManager.Instance().GetInstancesCount();
        if (count <= 1)
        {
            Overlay.IsOpen = false;
            return;
        }

        if (IKeyState.Instance()[VirtualKey.ESCAPE])
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

        ImGui.SetWindowPos((ImGui.GetMainViewport().Size - ImGui.GetWindowSize()) / 2f);

        for (uint i = 1; i <= count; i++)
        {
            if (i == InstancesManager.CurrentInstance)
                continue;

            var buttonLabel = Lang.Get
            (
                "FastInstanceZoneChange-ButtonFormat",
                new Dictionary<string, object>
                {
                    ["instanceText"] = i.ToSEHexCount(),
                    ["keyboardHint"] = i.ToSESmallCount()
                }
            );

            var disabledCondition =
                TaskHelper.IsBusy ||
                ICondition.Instance().IsBetweenAreas;
            
            using (ImRaii.Disabled(disabledCondition))
            {
                if (ImGui.Button($"{buttonLabel}")             |
                    IKeyState.Instance()[(VirtualKey)(48 + i)] |
                    IKeyState.Instance()[(VirtualKey)(96 + i)])
                {
                    if (disabledCondition)
                        continue;

                    ChatManager.Instance().SendMessage($"/pdr insc {i}");
                    Overlay.IsOpen = false;
                }
            }
        }
    }
    
    private void OnWarpComplete(WarpType warpType)
    {
        if (!config.AddDtrEntry ||
            entry == null)
            return;

        entry.Shown = InstancesManager.IsInstancedArea;
        
        entry.Text = InstancesManager.IsInstancedArea ?
                         Lang.Get
                         (
                             "FastInstanceZoneChange-DTRFormat",
                             new Dictionary<string, object>
                             {
                                 ["instanceText"] = InstancesManager.CurrentInstance.ToSEHexCount()
                             }
                         ) :
                         string.Empty;
        
        entry.Tooltip = ValidUses.Contains(GameState.TerritoryIntendedUse) ?
                            Lang.Get("FastInstanceZoneChange-DTREntryTooltip") :
                            string.Empty;
    }

    private void OnCommand
    (
        string command,
        string args
    )
    {
        args = args.Trim().ToLowerInvariant();

        var publicInstance = UIState.Instance()->PublicInstance;
        
        if (!InstancesManager.IsInstancedArea)
        {
            NotifyError(Lang.Get("FastInstanceZoneChange-Notification-NoInstanceZones"));
            return;
        }

        if (args.Length == 0)
        {
            Overlay.IsOpen ^= true;
            return;
        }

        if (args == "abort")
        {
            TaskHelper.Abort();
            NotifyError(Lang.Get("FastInstanceZoneChange-Notification-Aborted"));
            return;
        }

        if (!uint.TryParse(args, out var targetInstance) ||
            targetInstance == 0                          ||
            targetInstance > InstancesManager.InstanceAmount)
        {
            NotifyHelper.Instance().ChatError
            (
                ReadOnlySeString.FormatIncorrectCommandParamLogMessage
                (
                    1,
                    Lang.Get("InstancedArea"),
                    args
                )
            );
            return;
        }

        if (publicInstance.InstanceId == targetInstance)
        {
            NotifyHelper.ToastError
            (
                Lang.Get
                (
                    "FastInstanceZoneChange-Notification-SameInstance",
                    new Dictionary<string, object>
                    {
                        ["instanceText"] = $"{GameState.TerritoryTypeData.ExtractPlaceName()}{targetInstance.ToSEHexCount()}"
                    }
                )
            );
            return;
        }

        TaskHelper.Abort();
        NotifyHelper.Instance().Chat
        (
            Lang.Get
            (
                "FastInstanceZoneChange-Notification-Goto",
                new Dictionary<string, object>
                {
                    ["instanceText"] = $"{GameState.TerritoryTypeData.ExtractPlaceName()}{targetInstance.ToSEHexCount()}"
                }
            )
        );
        
        var isAnyAetheryteNearby = IsAnyAetheryteNearby(out _);
        if (config.TeleportIfNotNearAetheryte && !isAnyAetheryteNearby)
        {
            TaskHelper.Enqueue
            (
                () => AetheryteRecordManager.Instance().GetNearestAetheryte
                (
                    GameState.TerritoryType,
                    LocalPlayerState.Object?.Position ?? Vector3.Zero
                )?.TeleportTo(),
                "传送到目标区域最近以太之光",
                weight: 2
            );
            TaskHelper.DelayNext(500, "等待传送开始", 2);
            TaskHelper.Enqueue
            (
                () => Throttler.Shared.Throttle("FastInstanceZoneChange-WaitTeleportFinish") && IsAnyAetheryteNearby(out _),
                "传送到目标区域最近以太之光",
                weight: 2
            );
        }

        var currentMountID = 0U;
        if (ICondition.Instance()[ConditionFlag.Mounted])
            currentMountID = IObjectTable.Instance().LocalPlayer.CurrentMount?.RowId ?? 0;

        TaskHelper.Enqueue
        (
            () =>
            {
                if (!ICondition.Instance()[ConditionFlag.Mounted]) return true;
                if (!Throttler.Shared.Throttle("FastInstanceZoneChange-WaitDismount", 100)) return false;

                ExecuteCommandManager.Instance().ExecuteCommand(ExecuteCommandFlag.Dismount);

                if (RaycastHelper.TryGetGroundPosition
                    (
                        IObjectTable.Instance().LocalPlayer.Position.WithY(300),
                        out var groundPosition
                    ))
                {
                    MovementManager.Instance().TPPlayerAddress(groundPosition with { Y = groundPosition.Y - 0.5f });
                    UseActionManager.Instance().UseAction(ActionType.GeneralAction, 9);
                }

                return !ICondition.Instance()[ConditionFlag.Mounted];
            },
            "下坐骑",
            weight: 2
        );

        if (config.ConstantlyTry)
            TaskHelper.Enqueue(() => EnqueueInstanceChange(targetInstance), "开始持续尝试切换副本区", weight: 2);
        else
            TaskHelper.Enqueue(() => ChangeInstanceZone(targetInstance), "切换副本区", weight: 2);

        TaskHelper.Enqueue
        (
            () => InstancesManager.CurrentInstance == targetInstance && !ICondition.Instance().IsBetweenAreas,
            "等待切换完毕, 更新副本区信息"
        );

        TaskHelper.Enqueue
        (
            () =>
            {
                if (!UIModule.IsScreenReady())
                    return false;

                // 上不了坐骑
                if (ActionManager.Instance()->GetActionStatus(ActionType.GeneralAction, 9) != 0)
                    return true;

                if (currentMountID == 0)
                    return true;

                UseActionManager.Instance().UseAction(ActionType.Mount, currentMountID);
                return true;
            },
            "切换完毕后上坐骑"
        );
        
        return;

        static void NotifyError(ReadOnlySeString message)
        {
            NotifyHelper.Instance().ChatError(message);
            NotifyHelper.ToastError(message);
        }
    }

    private void HandleDtrEntry
    (
        bool isAdd
    )
    {
        switch (isAdd)
        {
            case true when entry == null:
                entry = IDtrBar.Instance().Get("DailyRoutines-FastInstanceZoneChange");
                entry.OnClick = _ =>
                {
                    if (Overlay == null)
                    {
                        Overlay = new(this, Lang.Get("FastInstanceZoneChangeTitle"));

                        var flags = Overlay.Flags;
                        flags.Add(ImGuiWindowFlags.NoMove);
                        Overlay.Flags = flags;
                    }

                    Overlay.IsOpen ^= true;
                };
                entry.Shown = false;
                
                OnWarpComplete(WarpType.Normal);
                return;
            case false:
                entry?.Remove();
                entry = null;
                break;
        }

    }

    public void EnqueueInstanceChange
    (
        uint targetInstance
    )
    {
        // 等待上一次切换完成
        TaskHelper.Enqueue
        (
            () => SelectString->IsAddonAndNodesReady() ||
                  !ICondition.Instance()[ConditionFlag.BetweenAreas],
            "等待上一次切换完毕",
            weight: 2
        );

        // 检测切换情况
        TaskHelper.Enqueue
        (
            () =>
            {
                if (!Throttler.Shared.Throttle("FastInstanceZoneChange-DetectInstances")) return false;

                var publicInstance = UIState.Instance()->PublicInstance;
                if (!publicInstance.IsInstancedArea() || 
                    publicInstance.InstanceId == targetInstance)
                    TaskHelper.RemoveQueueTasks(2);

                return true;
            },
            "检测切换情况",
            weight: 2
        );

        // 实际切换指令
        TaskHelper.Enqueue(() => ChangeInstanceZone(targetInstance), "开始切换", weight: 2);
        
        // 延迟下一次检测
        TaskHelper.DelayNext(1_500, "等待 1.5 秒后继续", 2);
        TaskHelper.Enqueue
        (
            () => EnqueueInstanceChange(targetInstance),
            "开始新一轮切换",
            weight: 2
        );
    }

    internal static void ChangeInstanceZone
    (
        uint targetInstance
    )
    {
        if (!UIState.Instance()->PublicInstance.IsInstancedArea() ||
            !IsAnyAetheryteNearby(out var eventID))
            return;

        new EventStartPackt(LocalPlayerState.EntityID, eventID).Send();
        new EventCompletePackt(eventID, 0x2000000, 7, targetInstance + 1).Send();
    }

    private static bool IsAnyAetheryteNearby
    (
        out EventId eventID
    ) =>
        EventFramework.Instance()->TryGetNearestEventID
        (
            info => info.EventId.ContentId == EventHandlerContent.Aetheryte,
            obj => obj.NameString.Equals
            (
                LuminaGetter.GetRowOrDefault<Aetheryte>(0).Singular.ToString(),
                StringComparison.OrdinalIgnoreCase
            ),
            Vector3.Zero,
            out eventID
        );

    private class Config : ModuleConfig
    {
        public bool AddDtrEntry                = true;
        public bool ConstantlyTry              = true;
        public bool TeleportIfNotNearAetheryte = true;
    }

    #region 常量

    private const string COMMAND = "insc";

    // 其他地方也没法换线
    private static readonly TerritoryIntendedUse[] ValidUses =
    [
        TerritoryIntendedUse.Overworld,
        TerritoryIntendedUse.Town
    ];

    #endregion
}
