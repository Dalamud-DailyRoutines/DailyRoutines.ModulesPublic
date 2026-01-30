using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using DailyRoutines.Abstracts;
using DailyRoutines.Managers;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Gui.Dtr;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit;
using KamiToolKit.Classes;
using KamiToolKit.ContextMenu;
using KamiToolKit.Nodes;
using Lumina.Excel.Sheets;
using AgentWorldTravel = OmenTools.Infos.AgentWorldTravel;

namespace DailyRoutines.ModulesPublic;

public class FastWorldTravel : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title = GetLoc("FastWorldTravelTitle"),
        Description = GetLoc("FastWorldTravelDescription", COMMAND) +
                      (!GameState.IsCN ? string.Empty : "\n支持快捷超域旅行并实时显示各服务器超域旅行拥挤度 [国服特供]"),
        Category            = ModuleCategories.System,
        ModulesRecommend    = ["InstantReturn", "InstantTeleport"],
        ModulesPrerequisite = ["InstantLogout"]
    };

    public override ModulePermission Permission { get; } = new() { AllDefaultEnabled = true };

    internal const string COMMAND = "worldtravel";

    private static readonly HashSet<uint> WorldTravelValidZones = [132, 129, 130];

    private static readonly ConditionFlag[] InvalidConditions =
    [
        ConditionFlag.BoundByDuty,
        ConditionFlag.BoundByDuty56,
        ConditionFlag.BoundByDuty95,
        ConditionFlag.InDutyQueue,
        ConditionFlag.DutyRecorderPlayback,
        ConditionFlag.BetweenAreas
    ];

    private static Config? ModuleConfig;

    private static IDtrBarEntry? Entry;

    private static AddonDRFastWorldTravel? Addon;

    private static WorldMonitor? WorldStatusMonitor;

    protected override unsafe void Init()
    {
        ModuleConfig =   LoadConfig<Config>() ?? new();
        TaskHelper   ??= new() { TimeoutMS = int.MaxValue, ShowDebug = true };

        if (GameState.IsCN)
            WorldStatusMonitor = new(CheckCNDataCenterStatus);

        CommandManager.AddSubCommand(COMMAND, new(OnCommand) { HelpMessage = GetLoc("FastWorldTravel-CommandHelp") });

        if (ModuleConfig.AddDtrEntry)
            HandleDtrEntry(true);

        DService.Instance().Condition.ConditionChange += OnConditionChanged;
        OnConditionChanged(ConditionFlag.BetweenAreas, false);

        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "WorldTravelSelect", OnAddon);
        if (WorldTravelSelect->IsAddonAndNodesReady())
            OnAddon(AddonEvent.PostSetup, null);
    }

    protected override void Uninit()
    {
        DService.Instance().Condition.ConditionChange -= OnConditionChanged;
        DService.Instance().AddonLifecycle.UnregisterListener(OnAddon);

        HandleDtrEntry(false);

        Addon?.Dispose();
        Addon = null;

        WorldStatusMonitor?.Dispose();
        WorldStatusMonitor = null;

        FrameworkManager.Instance().Unreg(OnUpdate);
        CommandManager.RemoveSubCommand(COMMAND);
    }

    protected override void ConfigUI()
    {
        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), $"{GetLoc("Command")}:");
        using (ImRaii.PushIndent())
            ImGui.TextUnformatted($"/pdr {COMMAND} \u2192 {GetLoc("FastWorldTravel-CommandHelp")}");

        ImGui.NewLine();

        if (ImGui.Checkbox(GetLoc("FastWorldTravel-AutoLeaveParty"), ref ModuleConfig.AutoLeaveParty))
            SaveConfig(ModuleConfig);
        ImGuiOm.TooltipHover(GetLoc("FastWorldTravel-AutoLeavePartyHelp"));

        if (ImGui.Checkbox(GetLoc("FastWorldTravel-AddDtrEntry"), ref ModuleConfig.AddDtrEntry))
        {
            SaveConfig(ModuleConfig);
            HandleDtrEntry(ModuleConfig.AddDtrEntry);
        }

        if (ImGui.Checkbox(GetLoc("FastWorldTravel-ReplaceOrigAddon"), ref ModuleConfig.ReplaceOrigAddon))
            SaveConfig(ModuleConfig);
    }

    private static void HandleDtrEntry(bool isAdd)
    {
        switch (isAdd)
        {
            case true:
                if (Entry != null)
                {
                    Entry.Remove();
                    Entry = null;
                }

                Entry         =  DService.Instance().DTRBar.Get("DailyRoutines-FastWorldTravel");
                Entry.OnClick += _ => Addon.Toggle();
                Entry.Shown   =  true;
                Entry.Tooltip =  GetLoc("FastWorldTravel-DtrEntryTooltip");
                Entry.Text    =  LuminaWrapper.GetAddonText(12510);
                return;
            case false when Entry != null:
                Entry.Remove();
                Entry = null;
                break;
        }
    }

    #region 事件

    private void OnConditionChanged(ConditionFlag flag, bool value)
    {
        if (Entry == null || (TaskHelper?.IsBusy ?? true)) return;

        if (InvalidConditions.Contains(flag))
        {
            if (value)
                Entry.Shown = false;
            else
            {
                Entry.Shown = !DService.Instance().Condition.Any(InvalidConditions);

                if (Entry.Shown)
                {
                    Addon?.Dispose();
                    Addon = new(TaskHelper)
                    {
                        InternalName = "DRFastWorldTravel",
                        Title        = GameState.IsCN ? $"Daily Routines {Info.Title}" : LuminaWrapper.GetAddonText(12510),
                        Size         = new(GameState.IsCN ? 710f : 180f, 480f)
                    };
                    
                    Entry.Text = new SeStringBuilder().AddIcon(BitmapFontIcon.CrossWorld)
                                                      .Append($"{GameState.CurrentWorldData.Name.ToString()}")
                                                      .Build();
                }
            }
        }

    }

    private static unsafe void OnAddon(AddonEvent type, AddonArgs args)
    {
        if (WorldTravelSelect == null) return;
        if (!ModuleConfig.ReplaceOrigAddon) return;

        WorldTravelSelect->Close(true);
        Addon.Open();
    }

    // 更新 DTR
    private void OnUpdate(IFramework _)
    {
        if (Entry == null || (TaskHelper?.IsBusy ?? true)) return;

        Entry.Shown = !DService.Instance().Condition.Any(InvalidConditions);

        if (DService.Instance().Condition.Any(ConditionFlag.WaitingToVisitOtherWorld, ConditionFlag.ReadyingVisitOtherWorld))
            return;

        Entry.Text = new SeStringBuilder().AddIcon(BitmapFontIcon.CrossWorld)
                                          .Append($"{GameState.CurrentWorldData.Name.ToString()}")
                                          .Build();
    }

    // 指令
    private void OnCommand(string command, string args)
    {
        if (!Throttler.Throttle("FastWorldTravel-OnCommand", 1_000)) return;

        if (!DService.Instance().ClientState.IsLoggedIn          ||
            DService.Instance().ObjectTable.LocalPlayer == null  ||
            DService.Instance().Condition.Any(InvalidConditions) ||
            DService.Instance().Condition.Any(ConditionFlag.WaitingToVisitOtherWorld))
        {
            NotificationError(GetLoc("FastWorldTravel-Notice-InvalidEnv"));
            return;
        }

        if (args.Length == 0)
        {
            Addon.Toggle();
            return;
        }

        args = args.Trim().ToLowerInvariant();

        if (string.IsNullOrWhiteSpace(args))
        {
            NotificationError(GetLoc("FastWorldTravel-Notice-InvalidInput"));
            return;
        }

        var worldID = 0U;

        if (uint.TryParse(args, out var parsedNumber))
        {
            if (LuminaGetter.TryGetRow(parsedNumber, out World _) &&
                PresetSheet.Worlds.ContainsKey(parsedNumber))
                worldID = parsedNumber;
        }
        else
            worldID = PresetSheet.Worlds.FirstOrDefault(x => x.Value.Name.ToString().Contains(args, StringComparison.OrdinalIgnoreCase)).Key;

        if (worldID != 0)
        {
            // 国际服仅允许同大区
            if (!GameState.IsCN)
            {
                if (!PresetSheet.Worlds.TryGetValue(worldID, out var worldData) ||
                    worldData.DataCenter.RowId != GameState.CurrentDataCenter)
                    worldID = 0;
            }
            else
            {
                // 不是国服服务器
                if (!PresetSheet.CNWorlds.ContainsKey(worldID))
                    worldID = 0;
            }
        }

        if (worldID == 0 || !LuminaGetter.TryGetRow(worldID, out World targetWorld))
        {
            NotificationError(GetLoc("FastWorldTravel-Notice-WorldNoFound", args));
            return;
        }

        if (GameState.CurrentWorld == worldID)
        {
            NotificationError(GetLoc("FastWorldTravel-Notice-SameWorld"));
            return;
        }

        if (!GameState.IsCN)
        {
            EnqueueWorldTravel(worldID);
            return;
        }

        // 跨大区
        if (targetWorld.DataCenter.RowId != GameState.CurrentDataCenter)
        {
            EnqueueDCTravel(worldID);
            return;
        }

        EnqueueWorldTravel(worldID);
    }

    #endregion

    #region Enqueue

    private unsafe void EnqueueWorldTravel(uint worldID)
    {
        if (!LuminaGetter.TryGetRow(worldID, out World targetWorld)) return;

        TaskHelper.Abort();

        TaskHelper.Enqueue
        (
            () =>
            {
                if (Entry == null) return;
                Entry.Text = $"\ue06f {targetWorld.Name.ToString()}";
            },
            "更新 DTR 目标服务器信息"
        );

        if (ModuleConfig.AutoLeaveParty)
            TaskHelper.Enqueue(LeaveNonCrossWorldParty, "离开非跨服小队");

        if (!WorldTravelValidZones.Contains(GameState.TerritoryType))
        {
            var nearestAetheryte = DService.Instance().AetheryteList
                                           .Where(x => WorldTravelValidZones.Contains(x.TerritoryID))
                                           .MinBy(x => x.GilCost);
            if (nearestAetheryte == null) return;

            TaskHelper.Enqueue(() => Telepo.Instance()->Teleport(nearestAetheryte.AetheryteID, 0),                        "传送回可跨服区域");
            TaskHelper.Enqueue(() => GameState.TerritoryType == nearestAetheryte.TerritoryID && UIModule.IsScreenReady(), "等待跨服完成");
        }

        TaskHelper.Enqueue
        (
            () =>
            {
                AgentWorldTravel.Instance()->TravelTo(worldID);
                NotificationInfo
                (
                    GetLoc
                    (
                        "FastWorldTravel-Notice-TravelTo",
                        $"{char.ToUpper(targetWorld.Name.ToString()[0])}{targetWorld.Name.ToString()[1..]}"
                    )
                );
            },
            "发起大区内跨服请求"
        );
    }

    private void EnqueueDCTravel(uint targetWorldID)
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
        TaskHelper.EnqueueAsync(() => ModuleManager.UnloadAsync(ModuleManager.GetModuleByName("AutoLogin")), "禁用自动登录");

        TaskHelper.DelayNext(500, "等待 500 毫秒");
        TaskHelper.Enqueue(() => ChatManager.Instance().SendCommand("/logout"), "登出游戏");

        TaskHelper.Enqueue(() => Dialogue->IsAddonAndNodesReady(), "等待界面出现");

        TaskHelper.DelayNext(500, "等待 500 毫秒");

        TaskHelper.Enqueue
        (
            () =>
            {
                if (TitleMenu->IsAddonAndNodesReady()) return true;
                if (!Dialogue->IsAddonAndNodesReady()) return false;

                var buttonNode = Dialogue->GetComponentButtonById(4);
                if (buttonNode == null) return false;

                buttonNode->Click();
                return true;
            },
            "点击确认键"
        );

        TaskHelper.Enqueue(() => TitleMenu->IsAddonAndNodesReady(), "等待标题界面");
    }

    private async Task EnqueueDCTravelRequest(Travel[] data)
    {
        try
        {
            NotificationInfo("DCTravelrX 正在处理超域旅行请求, 请稍等");

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
                            NotificationWarning("超域旅行失败: 请查看日志获取详细信息");
                            Error("超域旅行失败", exception);

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
                        TaskHelper.DelayNext(1000);
                    }

                    TaskHelper.Enqueue(() => AgentLobbyEvent.SelectWorldByID(travelData.TargetWorldID), "选择目标服务器");

                    TaskHelper.DelayNext(1000);
                    TaskHelper.Enqueue(() => AgentLobbyEvent.SelectCharacter(x => x.ContentId == travelData.ContentID), "选择目标角色");

                    if (DRConfig.Instance().ModuleEnabled.GetValueOrDefault("AutoLogin", false))
                        TaskHelper.EnqueueAsync(() => ModuleManager.LoadAsync(ModuleManager.GetModuleByName("AutoLogin")), "启用自动登录");
                    return;
                }

                await Task.Delay(100);
            }
        }
        catch (Exception ex)
        {
            Debug($"超域旅行失败: {ex.Message}", ex);
        }
    }

    #endregion

    #region 工具

    private static bool LeaveNonCrossWorldParty()
    {
        if (DService.Instance().PartyList.Length < 2 || DService.Instance().Condition[ConditionFlag.ParticipatingInCrossWorldPartyOrAlliance])
            return true;
        if (!Throttler.Throttle("FastWorldTravel-LeaveNonCrossWorldParty"))
            return false;

        ChatManager.Instance().SendMessage("/leave");
        return DService.Instance().PartyList.Length < 2;
    }

    private static (bool, uint) CheckCNDataCenterStatus(uint dcID)
    {
        var worlds = PresetSheet.Worlds.Where(x => x.Value.DataCenter.RowId == dcID).Select(x => x.Key).ToList();

        foreach (var world in worlds)
        {
            var time = GetDCTravelWaitTime.InvokeFunc(world);
            if (time != 0) continue;

            return (true, world);
        }

        return (false, 0);
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

    private class Config : ModuleConfiguration
    {
        public bool AutoLeaveParty   = true;
        public bool AddDtrEntry      = true;
        public bool ReplaceOrigAddon = true;
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

    private class AddonDRFastWorldTravel(TaskHelper taskHelper) : NativeAddon
    {
        private static NodeBase TeleportWidget;

        private static readonly Version MinDCTravelerXVersion = new("0.2.3.0");

        private static bool IsPluginEnabled =>
            DService.Instance().PI.IsPluginEnabled("DCTravelerX", MinDCTravelerXVersion);

        private static bool IsPluginValid =>
            IsPluginEnabled && IsDCTravelerValid;

        private static ContextMenu? ContextMenuService;

        private static bool LastOpenPluginState;

        private static bool LastForegroundState;

        private static Dictionary<uint, TextButtonNode> WorldToButtons = [];

        protected override unsafe void OnSetup(AtkUnitBase* addon)
        {
            ContextMenuService = new();

            LastOpenPluginState = IsPluginValid;
            WorldToButtons.Clear();

            TeleportWidget          = CreateTeleportWidget();
            TeleportWidget.Position = ContentStartPosition;

            if (GameState.IsCN)
            {
                var message = SeString.Empty;

                if (!IsPluginEnabled)
                {
                    message = new SeStringBuilder().Append("超域旅行功能依赖 ")
                                                   .AddUiForeground("DCTravlerX", 32)
                                                   .Append($" 插件 (版本 {MinDCTravelerXVersion} 及以上)")
                                                   .Build();
                }
                else if (!IsDCTravelerValid)
                {
                    message = new SeStringBuilder().Append("无法连接至超域旅行 API, 请确认已安装并启用 ")
                                                   .AddUiForeground("DCTravlerX", 32)
                                                   .Append($" 插件 (版本 {MinDCTravelerXVersion} 及以上), 若已启用, 请从 XIVLauncherCN 重启游戏")
                                                   .Build();
                }

                if (message != SeString.Empty)
                {
                    var pluginHelpNode = new TextNode
                    {
                        String           = message.Encode(),
                        FontSize         = 14,
                        IsVisible        = true,
                        Size             = new(150f, 25f),
                        AlignmentType    = AlignmentType.Center,
                        Position         = new(305f, -22f),
                        TextFlags        = TextFlags.Bold | TextFlags.Edge,
                        TextColor        = ColorHelper.GetColor(50),
                        TextOutlineColor = ColorHelper.GetColor(32)
                    };
                    pluginHelpNode.AttachNode(this);
                }
            }

            TeleportWidget.AttachNode(this);

            UpdateWaitTimeInfo();
        }

        protected override unsafe void OnUpdate(AtkUnitBase* addon)
        {
            if (BoundByDuty)
            {
                Close();
                return;
            }

            if (!GameState.IsCN) return;

            if (Throttler.Throttle("FastWorldTravel-OnAddonUpdate") && LastOpenPluginState != IsPluginValid)
            {
                Close();

                taskHelper.Abort();
                taskHelper.DelayNext(100);
                taskHelper.Enqueue(() => !IsOpen, "等待界面完全关闭");
                taskHelper.Enqueue(Open,          "重新打开");

                LastOpenPluginState = IsPluginValid;
                return;
            }

            if (LastForegroundState != GameState.IsForeground)
            {
                LastForegroundState = GameState.IsForeground;

                Throttler.Remove("FastWorldTravel-OnAddonUpdate-RequestQueueTime");
                Throttler.Remove("FastWorldTravel-OnAddonUpdate-UpdateQueueTime");
            }

            // 都在后台了就不要 DDOS 拂晓服务器了
            if (Throttler.Throttle("FastWorldTravel-OnAddonUpdate-RequestQueueTime", GameState.IsForeground ? 15_000U : 60_000))
                RequestWaitTimeInfoUpdate();

            if (Throttler.Throttle("FastWorldTravel-OnAddonUpdate-UpdateQueueTime", 1_000))
                UpdateWaitTimeInfo();
        }

        protected override unsafe void OnFinalize(AtkUnitBase* addon)
        {
            ContextMenuService?.Dispose();
            ContextMenuService = null;
        }

        private void RequestWaitTimeInfoUpdate()
        {
            DService.Instance().Framework.RunOnTick
            (async () =>
                {
                    if (!IsOpen || !IsPluginValid || WorldToButtons is not { Count: > 0 }) return;
                    await RequestDCTravelInfo.InvokeFunc();
                }
            );
        }

        private void UpdateWaitTimeInfo()
        {
            if (!IsOpen || !IsPluginValid || WorldToButtons is not { Count: > 0 }) return;

            foreach (var (worldID, node) in WorldToButtons)
            {
                var time = GetDCTravelWaitTime.InvokeFunc(worldID);
                if (time == -1) continue;

                var builder = new SeStringBuilder();
                builder.AddText("超域传送状态:")
                       .Add(NewLinePayload.Payload)
                       .AddText("              ");

                switch (time)
                {
                    case 0:
                        builder.AddUiForeground("即刻完成 / 等待 1 分钟以内", 45);
                        break;
                    case -999:
                        builder.AddUiForeground("繁忙 / 无法通行", 518);
                        break;
                    default:
                        builder.AddText("至少需要等待 ")
                               .AddUiForeground(time.ToString(), 32)
                               .AddText(" 分钟");
                        break;
                }


                node.TextTooltip = builder.Build().Encode();
                var baseColor = time switch
                {
                    0    => KnownColor.DarkGreen.ToVector4().ToVector3(),
                    -999 => KnownColor.DarkRed.ToVector4().ToVector3(),
                    >= 5 => KnownColor.Brown.ToVector4().ToVector3(),
                    _    => ColorHelper.GetColor(32).ToVector3()
                };

                node.AddColor = baseColor;
            }
        }

        private static HorizontalListNode CreateTeleportWidget()
        {
            var mainLayoutContainer = new HorizontalListNode { IsVisible = true };

            // 当前大区
            var currentDCWorlds = PresetSheet.Worlds
                                             .Where(x => x.Value.DataCenter.RowId == GameState.CurrentDataCenter)
                                             .ToList();
            if (currentDCWorlds is not { Count: > 0 }) return mainLayoutContainer;

            var currentDCColumn = CreateDataCenterColumn(currentDCWorlds.First().Value.DataCenter.RowId, currentDCWorlds);
            mainLayoutContainer.AddNode(currentDCColumn);

            if (!GameState.IsCN)
                return mainLayoutContainer;

            // 其他大区 (仅国服)
            var otherDataCenters = PresetSheet.CNWorlds
                                              .Where(kvp => kvp.Value.DataCenter.RowId != GameState.CurrentDataCenter)
                                              .GroupBy(x => x.Value.DataCenter.RowId)
                                              .ToDictionary(x => x.Key, x => x.ToList());

            foreach (var dataCenter in otherDataCenters)
            {
                mainLayoutContainer.AddDummy(25);

                var otherDCColumn = CreateDataCenterColumn(dataCenter.Key, dataCenter.Value);
                mainLayoutContainer.AddNode(otherDCColumn);
            }

            return mainLayoutContainer;
        }

        private static unsafe VerticalListNode CreateDataCenterColumn(uint dcID, List<KeyValuePair<uint, World>> worlds)
        {
            var dcName = LuminaWrapper.GetDataCenterName(dcID);

            var column      = new VerticalListNode { IsVisible = true };
            var totalHeight = 0f;

            column.AddDummy(5f);
            totalHeight += 5f;

            var header = new TextNode
            {
                String        = dcName,
                FontSize      = 20,
                IsVisible     = true,
                Size          = new(150f, 30f),
                AlignmentType = AlignmentType.Center,
                TextFlags     = TextFlags.Bold
            };

            if (dcID != GameState.CurrentDataCenter)
                header.ShowClickableCursor = true;

            column.AddNode(header);
            totalHeight += header.Size.Y;

            if (dcID != GameState.CurrentDataCenter)
            {
                header.AddEvent
                (
                    AtkEventType.MouseClick,
                    (_, _, _, _, _) =>
                    {
                        ContextMenuService.Clear();

                        ContextMenuService.AddItem
                        (
                            new()
                            {
                                IsEnabled = false,
                                Name      = $"{dcName}大区",
                                OnClick   = () => { }
                            }
                        );

                        ContextMenuService.AddItem
                        (
                            new()
                            {
                                IsEnabled = false,
                                Name = $"当前监控: " +
                                       $"{(WorldStatusMonitor.GetActiveMonitors().ToList() is { Count: > 0 } list ?
                                               LuminaWrapper.GetDataCenterName(list.First()) :
                                               "(无)")}",
                                OnClick = () => { }
                            }
                        );

                        var subMenu = new ContextMenuSubItem
                        {
                            OnClick   = () => { },
                            Name      = "监控通行状态",
                            IsEnabled = true
                        };

                        if (WorldStatusMonitor.GetActiveMonitors().Contains(dcID))
                        {
                            subMenu.AddItem
                            (
                                new()
                                {
                                    Name    = "移除监控",
                                    OnClick = () => WorldStatusMonitor.RemoveMonitor(dcID)
                                }
                            );
                        }
                        else
                        {
                            subMenu.AddItem
                            (
                                new()
                                {
                                    IsEnabled = false,
                                    Name      = "(当目标大区可通行时)",
                                    OnClick   = () => { }
                                }
                            );

                            subMenu.AddItem
                            (
                                new()
                                {
                                    Name = "自动前往",
                                    OnClick = () =>
                                    {
                                        WorldStatusMonitor.Clear();

                                        WorldStatusMonitor.JustGo = true;
                                        WorldStatusMonitor.AddMonitor(dcID);
                                    }
                                }
                            );

                            subMenu.AddItem
                            (
                                new()
                                {
                                    Name = "发送通知",
                                    OnClick = () =>
                                    {
                                        WorldStatusMonitor.Clear();

                                        WorldStatusMonitor.JustGo = false;
                                        WorldStatusMonitor.AddMonitor(dcID);
                                    }
                                }
                            );
                        }


                        ContextMenuService.AddItem(subMenu);

                        ContextMenuService.Open();
                    }
                );
            }

            column.AddDummy(15f);
            totalHeight += 15f;

            foreach (var (worldID, worldData) in worlds)
            {
                var worldNameBuilder = new SeStringBuilder().Append(worldData.Name.ToString());

                if (GameState.HomeWorld == worldID)
                {
                    worldNameBuilder.Append(" ");
                    worldNameBuilder.AddIcon(BitmapFontIcon.CrossWorld);
                }

                var button = new TextButtonNode
                {
                    Size      = new(150f, 40f),
                    IsVisible = true,
                    String    = worldNameBuilder.Build().Encode(),
                    OnClick = () =>
                    {
                        Addon.Close();
                        ChatManager.Instance().SendMessage($"/pdr worldtravel {worldData.Name.ToString()}");
                    },
                    IsEnabled = GameState.CurrentWorld != worldID && (worldData.DataCenter.RowId == GameState.CurrentDataCenter || IsPluginValid)
                };

                button.LabelNode.TextOutlineColor = KnownColor.Black.ToVector4();
                button.LabelNode.TextFlags        = TextFlags.Edge;
                column.AddNode(button);

                if (GameState.IsCN)
                    WorldToButtons.Add(worldID, button);

                totalHeight += button.Size.Y;

                column.AddDummy(5f);
                totalHeight += 5f;
            }

            column.Size = new(150f, totalHeight);

            return column;
        }
    }

    private class WorldMonitor : IDisposable
    {
        private readonly Channel<uint> requestChannel = Channel.CreateUnbounded<uint>();

        private readonly ConcurrentDictionary<uint, CancellationTokenSource> activeMonitors = [];

        private readonly Func<uint, (bool, uint)> checkLogicFunc;

        private readonly CancellationTokenSource serviceCts = new();

        private bool disposed;

        public bool JustGo { get; set; }

        public WorldMonitor(Func<uint, (bool, uint)> checkLogic)
        {
            checkLogicFunc = checkLogic;
            _              = ProcessChannelRequestsAsync(serviceCts.Token);
        }

        public void AddMonitor(uint dcID)
        {
            if (disposed)
                throw new ObjectDisposedException(nameof(WorldMonitor));

            if (activeMonitors.ContainsKey(dcID) || serviceCts.IsCancellationRequested)
                return;

            if (requestChannel.Writer.TryWrite(dcID))
            {
                Debug($"[FastWorldTravel] 开始监控 {LuminaWrapper.GetDataCenterName(dcID)} ({dcID}) 大区状态");
                Chat($"[{GetLoc("FastWorldTravelTitle")}]\n开始实时监控 [{LuminaWrapper.GetDataCenterName(dcID)}] 大区可通行状态\n检测到可通行时: [{(JustGo ? "直接前往" : "发送通知")}]");
            }
        }

        public void RemoveMonitor(uint dcID)
        {
            if (disposed) return;

            if (activeMonitors.TryRemove(dcID, out var cts))
            {
                Debug($"[FastWorldTravel] 停止监控 {LuminaWrapper.GetDataCenterName(dcID)} ({dcID}) 大区状态");
                Chat($"[{GetLoc("FastWorldTravelTitle")}]\n已停止对 [{LuminaWrapper.GetDataCenterName(dcID)}] 大区可通行状态的实时监控");

                try
                {
                    cts.Cancel();
                }
                catch (ObjectDisposedException)
                {
                    // ignored
                }
            }
        }

        public void Clear()
        {
            var monitors = activeMonitors.ToArray();
            foreach (var monitor in monitors)
                RemoveMonitor(monitor.Key);
        }

        public IEnumerable<uint> GetActiveMonitors() =>
            activeMonitors.Keys;

        private async Task ProcessChannelRequestsAsync(CancellationToken serviceToken)
        {
            try
            {
                while (await requestChannel.Reader.WaitToReadAsync(serviceToken))
                {
                    while (requestChannel.Reader.TryRead(out var serverId))
                    {
                        if (activeMonitors.ContainsKey(serverId)) continue;

                        var taskCts = CancellationTokenSource.CreateLinkedTokenSource(serviceToken);

                        if (activeMonitors.TryAdd(serverId, taskCts))
                            _ = MonitorRoutineAsync(serverId, taskCts);
                        else
                            taskCts.Dispose();
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // ignored
            }
            catch (Exception ex)
            {
                Error("[FastWorldTravel] 主循环发生预期外错误", ex);
            }
        }

        private async Task MonitorRoutineAsync(uint dcID, CancellationTokenSource cts)
        {
            try
            {
                using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));

                while (await timer.WaitForNextTickAsync(cts.Token))
                {
                    if (checkLogicFunc(dcID) is { Item1: true } result)
                    {
                        var message = $"大区 [{LuminaWrapper.GetDataCenterName(dcID)}] 已为可通行状态, 停止监控";
                        Chat(message);
                        NotificationInfo(message);
                        Speak(message);
                        
                        if (JustGo)
                            ChatManager.Instance().SendCommand($"/pdr worldtravel {result.Item2}");

                        break;
                    }

                    await Task.Delay(100).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                Debug($"[FastWorldTravel] 对大区 {dcID} 的状态监控已被取消");
            }
            finally
            {
                activeMonitors.TryRemove(dcID, out _);
                cts.Dispose();
            }
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;

            requestChannel.Writer.TryComplete();
            serviceCts.Cancel();

            foreach (var kvp in activeMonitors)
            {
                try
                {
                    kvp.Value.Cancel();
                }
                catch
                {
                    // ignored
                }
            }

            activeMonitors.Clear();
            serviceCts.Dispose();
        }
    }
}
