using DailyRoutines.Common.Extensions;
using DailyRoutines.Common.KamiToolKit.Addons.SelectYesno;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using DailyRoutines.Manager;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Config;
using Dalamud.Game.Gui.Dtr;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Lumina.Excel.Sheets;
using OmenTools.Dalamud;
using OmenTools.Dalamud.Abstractions;
using OmenTools.Dalamud.Attributes;
using OmenTools.Info.Lumina;
using OmenTools.Interop.Game.AgentEvent;
using OmenTools.Interop.Game.Helpers;
using OmenTools.Interop.Game.Lumina;
using OmenTools.OmenService;
using OmenTools.Threading;

namespace DailyRoutines.ModulesPublic;

public partial class FastWorldTravel : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title = Lang.Get("FastWorldTravelTitle"),
        Description = Lang.Get("FastWorldTravelDescription", COMMAND) +
                      (!GameState.IsCN ?
                           string.Empty :
                           "\n支持快捷超域旅行并实时显示各服务器超域旅行拥挤度 [国服特供]"),
        Category            = ModuleCategory.System,
        ModulesPair    = ["InstantReturn", "InstantTeleport"],
        ModulesPrerequisite = ["InstantLogout"]
    };

    public override ModulePermission Permission { get; } = new() { AllDefaultEnabled = true };

    private Config?        config;
    private IDtrBarEntry?  entry;
    private DRSelectYesno? selectYesnoAddon;

    protected override unsafe void Init()
    {
        config     =   Config.Load(this) ?? new();
        TaskHelper ??= new() { TimeoutMS = int.MaxValue, ShowDebug = true };

        CommandManager.Instance().AddSubCommand(COMMAND, new(OnCommand) { HelpMessage = Lang.Get("FastWorldTravel-CommandHelp") });

        if (config.AddDtrEntry)
            HandleDtrEntry(true);

        ICondition.Instance().ConditionChange += OnConditionChanged;
        OnConditionChanged(ConditionFlag.BetweenAreas, false);

        IAddonLifecycle.Instance().RegisterListener(AddonEvent.PostSetup, "WorldTravelSelect", OnAddon);
        if (WorldTravelSelect->IsAddonAndNodesReady())
            OnAddon(AddonEvent.PostSetup, null);
    }

    protected override void Uninit()
    {
        ICondition.Instance().ConditionChange -= OnConditionChanged;
        IAddonLifecycle.Instance().UnregisterListener(OnAddon);

        HandleDtrEntry(false);

        AddonDRFastWorldTravel.Addon?.Dispose();
        AddonDRFastWorldTravel.Addon = null;
        
        selectYesnoAddon?.Dispose();
        selectYesnoAddon = null;

        CommandManager.Instance().RemoveSubCommand(COMMAND);
    }

    protected override void ConfigUI()
    {
        using (ImRaii.Heading1(Lang.Get("Command")))
            ImGui.TextUnformatted($"/pdr {COMMAND} → {Lang.Get("FastWorldTravel-CommandHelp")}");

        ImGui.NewLine();

        if (ImGui.Checkbox(Lang.Get("FastWorldTravel-AutoLeaveParty"), ref config.AutoLeaveParty))
            config.Save(this);
        ImGuiOm.TooltipHover(Lang.Get("FastWorldTravel-AutoLeavePartyHelp"));

        if (ImGui.Checkbox(Lang.Get("FastWorldTravel-AddDtrEntry"), ref config.AddDtrEntry))
        {
            config.Save(this);
            HandleDtrEntry(config.AddDtrEntry);
        }

        if (ImGui.Checkbox(Lang.Get("FastWorldTravel-ReplaceOrigAddon"), ref config.ReplaceOrigAddon))
            config.Save(this);
        
        if (ImGui.Checkbox(Lang.Get("FastWorldTravel-SkipTravelBackConfirm"), ref config.SkipReturnHomeConfirmation))
            config.Save(this);
    }

    #region 事件

    private void OnDTRClick
    (
        DtrInteractionEvent param
    )
    {
        if (param.ClickType == MouseClickType.Left)
        {
            AddonDRFastWorldTravel.Toggle(this);
            return;
        }

        if (CheckAndNotifyIfNotValid(GameState.HomeWorld))
            return;

        if (config.SkipReturnHomeConfirmation)
        {
            ChatManager.Instance().SendCommand($"/pdr worldtravel {GameState.HomeWorldData.Name}");
            return;
        }
        
        if (selectYesnoAddon != null &&
            !AddonHelper.TryGetPtrByName("DRSelectYesno", out _))
        {
            try
            {
                selectYesnoAddon?.Dispose();
                selectYesnoAddon = null;
            }
            catch
            {
                // 谁敢猜这个时候会发生什么
            }
        }

        if (selectYesnoAddon == null)
        {
            using var rented  = new RentedSeStringBuilder();
            var       builder = rented.Builder;

            builder.Append(GameState.HomeWorldData.Name);

            if (GameState.CurrentDataCenter != GameState.HomeDataCenter)
            {
                builder.AppendIcon((uint)BitmapFontIcon.CrossWorld)
                       .Append(GameState.HomeDataCenterData.Name);
            }

            selectYesnoAddon = DRSelectYesno.Open
            (
                new()
                {
                    Prompt = Lang.GetSe
                    (
                        "FastWorldTravel-Notification-TravelBackConfirm",
                        builder
                    ),
                    Callback = (_, result) =>
                    {
                        selectYesnoAddon = null;

                        if (result != DRSelectYesnoResult.Yes)
                            return;

                        ChatManager.Instance().SendCommand($"/pdr worldtravel {GameState.HomeWorldData.Name}");
                    }
                }
            );
        }
    }

    private void OnConditionChanged
    (
        ConditionFlag flag,
        bool          value
    )
    {
        if (entry == null || (TaskHelper?.IsBusy ?? true)) return;

        if (InvalidConditions.Contains(flag))
        {
            if (value)
                entry.Shown = false;
            else
            {
                entry.Shown = !ICondition.Instance().Any(InvalidConditions);

                if (entry.Shown)
                {
                    using var rented  = new RentedSeStringBuilder();
                    var       builder = rented.Builder;

                    entry.Text =
                        builder.AppendIcon((uint)BitmapFontIcon.CrossWorld)
                               .Append($"{GameState.CurrentWorldData.Name.ToString()}")
                               .ToReadOnlySeString()
                               .ToDalamudString();
                }
            }
        }

    }

    private unsafe void OnAddon
    (
        AddonEvent type,
        AddonArgs  args
    )
    {
        if (WorldTravelSelect == null) return;
        if (!config.ReplaceOrigAddon) return;

        WorldTravelSelect->Close(true);

        AddonDRFastWorldTravel.Open(this);
    }

    // 指令
    private void OnCommand
    (
        string command,
        string args
    )
    {
        if (!Throttler.Shared.Throttle("FastWorldTravel.OnCommand", 1_000)) return;

        if (args.Length == 0)
        {
            AddonDRFastWorldTravel.Open(this);
            return;
        }

        args = args.Trim().ToLowerInvariant();

        var worldID = 0U;
        if (uint.TryParse(args, out var parsedNumber))
        {
            if (LuminaGetter.TryGetRow(parsedNumber, out World _) &&
                Sheets.Worlds.ContainsKey(parsedNumber))
                worldID = parsedNumber;
        }
        else
            worldID = Sheets.Worlds.FirstOrDefault(x => x.Value.Name.ToString().Contains(args, StringComparison.OrdinalIgnoreCase)).Key;

        if (CheckAndNotifyIfNotValid(worldID))
            return;

        var worldName = LuminaWrapper.GetWorldName(worldID);
        var message = Lang.Get
        (
            "FastWorldTravel-Notification-TravelingTo",
            $"{char.ToUpper(worldName[0])}{worldName[1..]}"
        );
        
        NotifyHelper.Chat(message);
        NotifyHelper.Toast(message);
        
        // 跨大区
        if (LuminaWrapper.GetWorldDC(worldID) != GameState.CurrentDataCenter)
        {
            EnqueueDCTravel(worldID);
            return;
        }

        EnqueueWorldTravel(worldID);
    }

    #endregion

    #region Enqueue

    private unsafe void EnqueueWorldTravel
    (
        uint worldID
    )
    {
        if (!LuminaGetter.TryGetRow(worldID, out World targetWorld)) return;

        TaskHelper.Abort();

        TaskHelper.Enqueue
        (
            () =>
            {
                if (entry == null) return;
                entry.Text = $"\ue06f {targetWorld.Name.ToString()}";
            },
            "更新 DTR 目标服务器信息"
        );

        if (config.AutoLeaveParty)
            TaskHelper.Enqueue(LeaveNonCrossWorldParty, "离开非跨服小队");

        if (!WorldTravelValidZones.Contains(GameState.TerritoryType))
        {
            var nearestAetheryte = IAetheryteList.Instance()
                                           .Where(x => WorldTravelValidZones.Contains(x.TerritoryID))
                                           .MinBy(x => x.GilCost);
            if (nearestAetheryte == null) return;

            TaskHelper.Enqueue(() => MovementManager.Instance().TPSmart_BetweenZone(nearestAetheryte.TerritoryID),        "传送回可跨服区域");
            TaskHelper.Enqueue(() => GameState.TerritoryType == nearestAetheryte.TerritoryID && UIModule.IsScreenReady(), "等待跨服完成");
        }

        TaskHelper.Enqueue
        (
            () => AgentWorldTravel.Instance()->TravelTo(worldID),
            "发起大区内跨服请求"
        );
    }

    private void EnqueueDCTravel
    (
        uint targetWorldID
    )
    {
        if (GameState.CurrentWorld == 0 ||
            GameState.HomeWorld    == 0 ||
            targetWorldID          == 0 ||
            !LuminaGetter.TryGetRow(targetWorldID, out World targetWorld)) return;

        Travel travel;

        // 现在就在原始大区, 要去其他大区
        if (GameState.HomeDataCenter == GameState.CurrentDataCenter)
        {
            // 但是不在原始服务器
            if (GameState.CurrentWorld != GameState.HomeWorld)
                EnqueueWorldTravel(GameState.HomeWorld);

            TaskHelper.Enqueue(() => GameState.HomeWorld == GameState.CurrentWorld && UIModule.IsScreenReady(), "等待返回原始服务器的跨服完成");

            travel = new Travel
            {
                CurrentWorldID = GameState.HomeWorld,
                TargetWorldID  = targetWorldID,
                ContentID      = LocalPlayerState.ContentID,
                IsBack         = false,
                Name           = LocalPlayerState.Name,
                Description    = targetWorld.DataCenter.Value.Name.ToString()
            };

            EnqueueLogout();
            TaskHelper.EnqueueAsync(() => EnqueueDCTravelRequest([travel]), "发送跨服请求");
            return;
        }

        // 现在不在原始大区, 要回原始服务器
        if (targetWorldID == GameState.HomeWorld)
        {
            travel = new Travel
            {
                CurrentWorldID = GameState.CurrentWorld,
                TargetWorldID  = targetWorldID,
                ContentID      = LocalPlayerState.ContentID,
                IsBack         = true,
                Name           = LocalPlayerState.Name,
                Description    = targetWorld.DataCenter.Value.Name.ToString()
            };

            EnqueueLogout();
            TaskHelper.EnqueueAsync(() => EnqueueDCTravelRequest([travel]), "发送跨服请求");
            return;
        }

        // 现在不在原始大区, 要回原始大区的其他服务器
        if (targetWorld.DataCenter.RowId == GameState.HomeDataCenter)
        {
            travel = new Travel
            {
                CurrentWorldID = GameState.CurrentWorld,
                TargetWorldID  = targetWorldID,
                ContentID      = LocalPlayerState.ContentID,
                IsBack         = true,
                Name           = LocalPlayerState.Name,
                Description    = targetWorld.DataCenter.Value.Name.ToString(),
                HomeWorldID    = GameState.HomeWorld
            };

            EnqueueLogout();
            TaskHelper.EnqueueAsync(() => EnqueueDCTravelRequest([travel]), "发送跨服请求");
            TaskHelper.Enqueue
            (
                () =>
                {
                    if (GameState.CurrentWorld != GameState.HomeWorld || !GameState.IsLoggedIn) return false;

                    EnqueueWorldTravel(targetWorldID);
                    return true;
                },
                "回到原始服务器, 跨服到其他服务器",
                weight: -1
            );
            return;
        }

        // 现在不在原始大区, 要去非原始大区
        var travel0 = new Travel
        {
            CurrentWorldID = GameState.CurrentWorld,
            TargetWorldID  = GameState.HomeWorld,
            ContentID      = LocalPlayerState.ContentID,
            IsBack         = true,
            Name           = LocalPlayerState.Name,
            Description    = targetWorld.DataCenter.Value.Name.ToString()
        };

        var travel1 = new Travel
        {
            CurrentWorldID = GameState.HomeWorld,
            TargetWorldID  = targetWorldID,
            ContentID      = LocalPlayerState.ContentID,
            IsBack         = false,
            Name           = LocalPlayerState.Name,
            Description    = targetWorld.DataCenter.Value.Name.ToString()
        };

        EnqueueLogout();
        TaskHelper.EnqueueAsync(() => EnqueueDCTravelRequest([travel0, travel1]), "发送跨服请求");
    }

    private unsafe void EnqueueLogout()
    {
        TaskHelper.Enqueue
        (
            () =>
            {
                if (!(ModuleManager.Instance().IsModuleEnabled(MODULE_NAME_AUTO_LOGIN) ?? false))
                    return;

                markNextAutoLoginHandledIPC.InvokeAction();
            },
            "禁用自动登录"
        );

        TaskHelper.Enqueue
        (
            () =>
            {
                if (!Throttler.Shared.Throttle("FastWorldTravel.Logout"))
                    return false;

                if (!GameState.IsLoggedIn)
                    return true;

                ChatManager.Instance().SendCommand("/logout");
                return false;
            },
            "登出游戏"
        );

        TaskHelper.Enqueue(() => TitleMenu->IsAddonAndNodesReady(), "等待标题界面");

        TaskHelper.DelayNext(2000, "等待 2 秒");
    }

    private async Task EnqueueDCTravelRequest
    (
        Travel[] data
    )
    {
        try
        {
            NotifyHelper.Instance().NotificationInfo("DCTravelerX 正在处理超域旅行请求, 请稍等");

            for (var i = 0; i < data.Length; i++)
            {
                var travelData = data[i];

                TaskHelper.EnqueueAsync
                (async () =>
                    {
                        var exception = await SendDCTravel.InvokeFunc
                                        (
                                            (int)travelData.CurrentWorldID,
                                            (int)travelData.TargetWorldID,
                                            travelData.ContentID,
                                            travelData.IsBack,
                                            travelData.Name
                                        );

                        if (exception != null)
                        {
                            NotifyHelper.Instance().NotificationWarning("超域旅行失败: 请查看日志获取详细信息");
                            DLog.Error("超域旅行失败", exception);

                            TaskHelper.Abort();
                        }
                    }
                );

                if (i == data.Length - 1)
                {
                    TaskHelper.Enqueue(AgentLobbyEvent.OpenCharacterSelect, "进入角色选择界面");

                    unsafe
                    {
                        TaskHelper.Enqueue(() => CharaSelect != null || CharaSelectListMenu != null, "等待角色选择界面可用");
                        TaskHelper.DelayNext(500);
                    }

                    TaskHelper.Enqueue(() => AgentLobbyEvent.SelectWorldByID(travelData.TargetWorldID), "选择目标服务器");

                    TaskHelper.DelayNext(500);
                    TaskHelper.Enqueue(() => AgentLobbyEvent.SelectCharacter(x => x.ContentId == travelData.ContentID), "选择目标角色");

                    TaskHelper.Enqueue(() => GameState.IsLoggedIn, "等待登录");
                    return;
                }

                await Task.Delay(100);
            }
        }
        catch (Exception ex)
        {
            DLog.Debug($"超域旅行失败: {ex.Message}", ex);
        }
    }

    #endregion

    #region 工具
    
    /// <returns>如果有任何报错, 则返回 true</returns>
    private static bool CheckAndNotifyIfNotValid(uint targetWorld)
    {
        // 当前无法进行该操作。
        if (!GameState.IsLoggedIn           ||
            LocalPlayerState.Object == null ||
            ICondition.Instance().Any(InvalidConditions))
        {
            NotifyHelper.ToastError(ISeStringEvaluator.Instance().EvaluateFromLogMessage(9066));
            return true;
        }

        // 等待跨界传送时无法进行该操作。
        if (ICondition.Instance().Any(ConditionFlag.WaitingToVisitOtherWorld, ConditionFlag.ReadyingVisitOtherWorld))
        {
            NotifyHelper.ToastError(ISeStringEvaluator.Instance().EvaluateFromLogMessage(7792));
            return true;
        }
        
        // 无法指定当前服务器为目标。
        if (GameState.CurrentWorld == targetWorld)
        {
            NotifyHelper.ToastError(ISeStringEvaluator.Instance().EvaluateFromLogMessage(9407));
            return true;
        }

        World targetWorldRow = default;
        // 无法指定  为目标服务器。
        if (!Sheets.Worlds.ContainsKey(targetWorld) ||
            !LuminaGetter.TryGetRow(targetWorld, out targetWorldRow))
        {
            var worldName = "???";
            if (targetWorldRow.RowId != 0 &&
                targetWorldRow.Name.ToString() is var targetWorldName &&
                !string.IsNullOrEmpty(targetWorldName))
                worldName = targetWorldName;
            
            NotifyHelper.ToastError(ISeStringEvaluator.Instance().EvaluateFromLogMessage(9408, [worldName]));
            return true;
        }
        
        // 非国服跨大区
        if (!GameState.IsCN &&
            LuminaWrapper.GetWorldDC(targetWorld) != GameState.CurrentDataCenter)
        {
            NotifyHelper.ToastError(Lang.Get("FastWorldTravel-Notification-CrossDCTravel"));
            return true;
        }
        
        return false;
    }
    
    private static bool LeaveNonCrossWorldParty()
    {
        if (IPartyList.Instance().Length < 2 || ICondition.Instance()[ConditionFlag.ParticipatingInCrossWorldPartyOrAlliance])
            return true;
        if (!Throttler.Shared.Throttle("FastWorldTravel-LeaveNonCrossWorldParty"))
            return false;

        ChatManager.Instance().SendMessage("/leave");
        return IPartyList.Instance().Length < 2;
    }

    private static (bool, uint) CheckCNDataCenterStatus
    (
        uint dcID
    )
    {
        var worlds = Sheets.Worlds.Where(x => x.Value.DataCenter.RowId == dcID).Select(x => x.Key).ToList();

        foreach (var world in worlds)
        {
            var time = GetDCTravelWaitTime.InvokeFunc(world);
            if (time != 0) continue;

            return (true, world);
        }

        return (false, 0);
    }
    
    private void HandleDtrEntry
    (
        bool isAdd
    )
    {
        switch (isAdd)
        {
            case true:
                entry         ??= IDtrBar.Instance().Get("DailyRoutines-FastWorldTravel");
                entry.OnClick =   OnDTRClick;
                entry.Tooltip =   Lang.Get("FastWorldTravel-DtrEntryTooltip");
                entry.Text    =   LuminaWrapper.GetAddonText(12510);
                entry.Shown   =   true;
                
                IGameConfig.Instance().UiConfig.Set(nameof(UiConfigOption.InfoSettingDispWorldNameType), 0U);
                return;
            case false when entry != null:
                entry.Remove();
                entry = null;
                break;
        }
    }

    #endregion

    #region IPC

    [IPCSubscriber("DCTravelerX.Travel")]
    private static IPCSubscriber<int, int, ulong, bool, string, Task<Exception?>> SendDCTravel;

    [IPCSubscriber("DCTravelerX.IsValid", DefaultValue = "false")]
    private static IPCSubscriber<bool> IsDCTravelerValid;

    [IPCSubscriber("DCTravelerX.QueryAllWaitTime")]
    private static IPCSubscriber<Task> RequestDCTravelInfo;

    [IPCSubscriber("DCTravelerX.GetWaitTime", DefaultValue = "-1")]
    private static IPCSubscriber<uint, int> GetDCTravelWaitTime;

    #endregion

    private class Config : ModuleConfig
    {
        public bool AddDtrEntry                = true;
        public bool AutoLeaveParty             = true;
        public bool ReplaceOrigAddon           = true;
        public bool SkipReturnHomeConfirmation = true;
    }

    private struct Travel
    {
        public uint    CurrentWorldID;
        public uint    HomeWorldID;
        public uint    TargetWorldID;
        public ulong   ContentID;
        public bool    IsBack;
        public string  Name;
        public string? Description;
    }

    #region IPC

    [IPCSubscriber("AutoLogin.MarkNextAutoLoginHandled")]
    private IPCSubscriber<object> markNextAutoLoginHandledIPC;

    #endregion

    #region 常量

    private const string COMMAND = "worldtravel";

    private const string MODULE_NAME_AUTO_LOGIN = "AutoLogin";

    private static readonly uint[] WorldTravelValidZones =
    [
        132,
        129,
        130
    ];

    private static readonly ConditionFlag[] InvalidConditions =
    [
        ConditionFlag.BoundByDuty,
        ConditionFlag.BoundByDuty56,
        ConditionFlag.BoundByDuty95,
        ConditionFlag.InDutyQueue,
        ConditionFlag.DutyRecorderPlayback,
        ConditionFlag.BetweenAreas
    ];

    #endregion
}
