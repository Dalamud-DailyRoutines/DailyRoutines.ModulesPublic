using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using DailyRoutines.Abstracts;
using DailyRoutines.Infos;
using DailyRoutines.Managers;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Gui.Dtr;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit;
using KamiToolKit.Classes;
using KamiToolKit.Nodes;
using Lumina.Excel.Sheets;
using AgentWorldTravel = OmenTools.Infos.AgentWorldTravel;

namespace DailyRoutines.ModulesPublic;

public class FastWorldTravel : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title = GetLoc("FastWorldTravelTitle"),
        Description = GetLoc("FastWorldTravelDescription", Command) +
                      (!GameState.IsCN ? string.Empty : "\n支持快捷超域旅行并实时显示各服务器超域旅行拥挤度 [国服特供]"),
        Category         = ModuleCategories.System,
        ModulesRecommend = ["InstantReturn", "InstantTeleport"]
    };

    public override ModulePermission Permission { get; } = new() { AllDefaultEnabled = true };

    internal const string Command = "worldtravel";

    private static readonly HashSet<uint> WorldTravelValidZones = [132, 129, 130];

    private static readonly ConditionFlag[] InvalidConditions =
    [
        ConditionFlag.BoundByDuty, ConditionFlag.BoundByDuty56, ConditionFlag.BoundByDuty95, ConditionFlag.InDutyQueue,
        ConditionFlag.DutyRecorderPlayback
    ];

    private static Config? ModuleConfig;

    public static Dictionary<uint, string> CurrentWorlds = []; // 当前的服务器
    
    private static IDtrBarEntry? Entry;

    private static AddonDRFastWorldTravel? Addon;

    private static readonly Dictionary<uint, List<Tuple<uint, string>>> CNDataCenter = [];

    [IPCSubscriber("DCTravelerX.Travel")]
    private static IPCSubscriber<int, int, ulong, bool, string, Task<Exception?>> SendDCTravel;
    
    [IPCSubscriber("DCTravelerX.IsValid", DefaultValue = "false")]
    private static IPCSubscriber<bool> IsDCTravelerValid;
    
    [IPCSubscriber("DCTravelerX.QueryAllWaitTime")]
    private static IPCSubscriber<Task> RequestDCTravelInfo;
    
    [IPCSubscriber("DCTravelerX.GetWaitTime", DefaultValue = "-1")]
    private static IPCSubscriber<uint, int> GetDCTravelWaitTime;

    static FastWorldTravel()
    {
        if (!GameState.IsCN) return;
        
        CNDataCenter = PresetSheet.CNWorlds
                                  .GroupBy(world => world.Value.DataCenter.RowId)
                                  .ToDictionary(group => group.Key,
                                                group => group.Select(world => new Tuple<uint, string>(world.Key, world.Value.Name.ExtractText())).ToList());
    }

    protected override unsafe void Init()
    {
        ModuleConfig =   LoadConfig<Config>() ?? new();
        TaskHelper   ??= new() { TimeLimitMS = int.MaxValue, ShowDebug = true };

        Addon ??= new(TaskHelper)
        {
            InternalName = "DRFastWorldTravel",
            Title        = GameState.IsCN ? $"Daily Routines {Info.Title}" : LuminaWrapper.GetAddonText(12510),
            Size         = new(GameState.IsCN ? 710f : 180f, 480f),
        };
        Addon.SetWindowPosition(ModuleConfig.AddonPosition);
        
        GameState.Login += OnLogin;
        OnLogin();
        
        CommandManager.AddSubCommand(Command, new(OnCommand) { HelpMessage = GetLoc("FastWorldTravel-CommandHelp") });

        if (ModuleConfig.AddDtrEntry)
            HandleDtrEntry(true);
        
        FrameworkManager.Reg(OnUpdate, throttleMS: 1_000);
        
        DService.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "WorldTravelSelect", OnAddon);
        if (IsAddonAndNodesReady(WorldTravelSelect))
            OnAddon(AddonEvent.PostSetup, null);
    }

    protected override void ConfigUI()
    {
        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), $"{GetLoc("Command")}:");
        using (ImRaii.PushIndent())
            ImGui.Text($"/pdr {Command} \u2192 {GetLoc("FastWorldTravel-CommandHelp")}");

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
                
                Entry         ??= DService.DtrBar.Get("DailyRoutines-FastWorldTravel");
                Entry.OnClick +=  _ => Addon.Toggle();
                Entry.Shown   =   true;
                Entry.Tooltip =   GetLoc("FastWorldTravel-DtrEntryTooltip");
                Entry.Text    =   LuminaWrapper.GetAddonText(12510);
                return;
            case false when Entry != null:
                Entry.Remove();
                Entry = null;
                break;
        }
    }

    #region 事件
    
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

        Entry.Shown = !DService.Condition.Any(InvalidConditions);

        if (DService.Condition.Any(ConditionFlag.WaitingToVisitOtherWorld, ConditionFlag.ReadyingVisitOtherWorld))
            return;

        Entry.Text = new SeStringBuilder().AddIcon(BitmapFontIcon.CrossWorld)
                                          .Append($"{GameState.CurrentWorldData.Name.ExtractText()}")
                                          .Build();
    }
    
    // 更新区服数据
    private void OnLogin()
    {
        TaskHelper.RemoveAllTasks(1);
        
        if (GameState.HomeWorld == 0 || GameState.CurrentWorld == 0) return;
        
        var dataCenter = GameState.CurrentDataCenter;
        if (dataCenter == 0) return;
        
        CurrentWorlds = PresetSheet.Worlds
                                   .Where(x => x.Value.DataCenter.RowId == dataCenter)
                                   .OrderBy(x => x.Key                  == GameState.HomeWorld)
                                   .ThenBy(x => x.Value.Name.ExtractText())
                                   .ToDictionary(x => x.Key, x => x.Value.Name.ExtractText().ToLowerInvariant());
    }

    // 指令
    private void OnCommand(string command, string args)
    {
        if (!Throttler.Throttle("FastWorldTravel-OnCommand", 1_000)) return;

        if (!DService.ClientState.IsLoggedIn          ||
            DService.ObjectTable.LocalPlayer == null  ||
            DService.Condition.Any(InvalidConditions) ||
            DService.Condition.Any(ConditionFlag.WaitingToVisitOtherWorld))
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
            worldID = PresetSheet.Worlds.FirstOrDefault(x => x.Value.Name.ExtractText().Contains(args, StringComparison.OrdinalIgnoreCase)).Key;

        if (worldID != 0)
        {
            // 国际服仅允许同大区
            if (!GameState.IsCN)
            {
                if (!CurrentWorlds.ContainsKey(worldID))
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
        
        TaskHelper.Enqueue(() =>
        {
            if (Entry == null) return;
            Entry.Text = $"\ue06f {targetWorld.Name.ExtractText()}";
        }, "更新 DTR 目标服务器信息");

        if (ModuleConfig.AutoLeaveParty)
            TaskHelper.Enqueue(LeaveNonCrossWorldParty, "离开非跨服小队");

        if (!WorldTravelValidZones.Contains(GameState.TerritoryType))
        {
            var nearestAetheryte = DService.AetheryteList
                                           .Where(x => WorldTravelValidZones.Contains(x.TerritoryID))
                                           .MinBy(x => x.GilCost);
            if (nearestAetheryte == null) return;

            TaskHelper.Enqueue(() => Telepo.Instance()->Teleport(nearestAetheryte.AetheryteID, 0),               "传送回可跨服区域");
            TaskHelper.Enqueue(() => GameState.TerritoryType == nearestAetheryte.TerritoryID && IsScreenReady(), "等待跨服完成");
        }

        TaskHelper.Enqueue(() =>
        {
            AgentWorldTravel.Instance()->TravelTo(worldID);
            NotificationInfo(GetLoc("FastWorldTravel-Notice-TravelTo",
                                    $"{char.ToUpper(targetWorld.Name.ExtractText()[0])}{targetWorld.Name.ExtractText()[1..]}"));
        }, "发起大区内跨服请求");
    }

    private void EnqueueDCTravel(uint targetWorldID)
    {
        if (GameState.CurrentWorld == 0 || GameState.HomeWorld == 0 ||
            targetWorldID          == 0 || !LuminaGetter.TryGetRow(targetWorldID, out World targetWorld)) return;
        
        Travel travel;
        
        // 现在就在原始大区, 要去其他大区
        if (GameState.HomeDataCenter == GameState.CurrentDataCenter)
        {
            // 但是不在原始服务器
            if (GameState.CurrentWorld != GameState.HomeWorld)
                EnqueueWorldTravel(GameState.HomeWorld);

            TaskHelper.Enqueue(() => GameState.HomeWorld == GameState.CurrentWorld && IsScreenReady(), "等待返回原始服务器的跨服完成");

            travel = new Travel
            {
                CurrentWorldID = GameState.HomeWorld,
                TargetWorldID  = targetWorldID,
                ContentID      = LocalPlayerState.ContentID,
                IsBack         = false,
                Name           = LocalPlayerState.Name,
                Description    = targetWorld.DataCenter.Value.Name.ExtractText()
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
                Description    = targetWorld.DataCenter.Value.Name.ExtractText()
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
                Description    = targetWorld.DataCenter.Value.Name.ExtractText(),
                HomeWorldID    = GameState.HomeWorld,
            };

            EnqueueLogout();
            TaskHelper.EnqueueAsync(() => EnqueueDCTravelRequest([travel]), "发送跨服请求");
            TaskHelper.Enqueue(() =>
            {
                if (GameState.CurrentWorld != GameState.HomeWorld || !GameState.IsLoggedIn) return false;
                
                EnqueueWorldTravel(targetWorldID);
                return true;
            }, "回到原始服务器, 跨服到其他服务器", weight: -1);
            return;
        }
        
        var travel0 = new Travel
        {
            CurrentWorldID = GameState.CurrentWorld,
            TargetWorldID  = targetWorldID,
            ContentID      = LocalPlayerState.ContentID,
            IsBack         = true,
            Name           = LocalPlayerState.Name,
            Description    = targetWorld.DataCenter.Value.Name.ExtractText()
        };

        var travel1 = new Travel
        {
            CurrentWorldID = GameState.HomeWorld,
            TargetWorldID  = targetWorldID,
            ContentID      = LocalPlayerState.ContentID,
            IsBack         = false,
            Name           = LocalPlayerState.Name,
            Description    = targetWorld.DataCenter.Value.Name.ExtractText()
        };
        
        EnqueueLogout();
        TaskHelper.EnqueueAsync(() => EnqueueDCTravelRequest([travel0, travel1]), "发送跨服请求");
    }
    
    private unsafe void EnqueueLogout()
    {
        TaskHelper.EnqueueAsync(() => ModuleManager.UnloadAsync(ModuleManager.GetModuleByName("AutoLogin")), "禁用自动登录");
        
        TaskHelper.DelayNext(500, "等待 500 毫秒");
        TaskHelper.Enqueue(() => ExecuteCommandManager.ExecuteCommand((ExecuteCommandFlag)445), "登出游戏");

        TaskHelper.Enqueue(() => IsAddonAndNodesReady(Dialogue), "等待界面出现");
        
        TaskHelper.DelayNext(500, "等待 500 毫秒");
        
        TaskHelper.Enqueue(() =>
        {
            if (IsAddonAndNodesReady(TitleMenu)) return true;
            if (!IsAddonAndNodesReady(Dialogue)) return false;
                
            var buttonNode = Dialogue->GetComponentButtonById(4);
            if (buttonNode == null) return false;
                
            buttonNode->ClickAddonButton(Dialogue);
            return true;
        }, "点击确认键");

        TaskHelper.Enqueue(() => IsAddonAndNodesReady(TitleMenu), "等待标题界面");
    }
    
    private async Task EnqueueDCTravelRequest(Travel[] data)
    {
        try
        {
            NotificationInfo("DCTravelrX 正在处理超域旅行请求, 请稍等");

            var isOneRequest = data.Length == 1;
            for (var i = 0; i < data.Length; i++)
            {
                var travelData = data[i];

                var exception = await SendDCTravel.InvokeFunc((int)travelData.CurrentWorldID,
                                                            (int)travelData.TargetWorldID,
                                                            travelData.ContentID,
                                                            travelData.IsBack,
                                                            travelData.Name);
                if (exception != null)
                {
                    NotificationWarning("超域旅行失败: 请查看日志获取详细信息");
                    throw exception;
                }

                if (isOneRequest || i == 1)
                {
                    unsafe
                    {
                        TaskHelper.Enqueue(() => CharaSelect != null || CharaSelectListMenu != null, "等待角色选择界面可用");
                    }
                    
                    if (DRConfig.Instance().ModuleEnabled.GetValueOrDefault("AutoLogin", false))
                        TaskHelper.EnqueueAsync(() => ModuleManager.LoadAsync(ModuleManager.GetModuleByName("AutoLogin")), "启用自动登录");
                    
                    TaskHelper.Enqueue(() => EnqueueLogin(travelData), "入队登录");
                    return;
                }
                
                // 第二个请求限流, 不然拂晓不给过
                NotificationInfo("等待 35 秒, 避免被超域旅行服务判断为频繁请求");
            }
        }
        catch (Exception ex)
        {
            Debug($"超域旅行失败: {ex.Message}", ex);
        }
    }

    private unsafe void EnqueueLogin(Travel traveldata)
    {
        TaskHelper.Enqueue(() => CharaSelect != null || CharaSelectListMenu != null, "等待角色选择界面可用", weight: 1);

        TaskHelper.Enqueue(() =>
        {
            var worldName = LuminaWrapper.GetWorldName(traveldata.TargetWorldID);
            if (traveldata.IsBack && traveldata.HomeWorldID != 0)
                worldName = LuminaWrapper.GetWorldName(traveldata.HomeWorldID);

            var stringArray = AtkStage.Instance()->GetStringArrayData(StringArrayType.CharaSelect)->StringArray;
            for (var i = 0; i < 8; i++)
            {
                try
                {
                    var worldString = SeString.Parse(stringArray[i].Value).ExtractText();
                    if (!worldString.Contains(worldName, StringComparison.OrdinalIgnoreCase)) continue;

                    SendEvent(AgentId.Lobby, 0, 25, 0, i);

                    var agent = AgentLobby.Instance();
                    if (agent == null) return;

                    var addon = CharaSelectListMenu;
                    if (addon == null) return;

                    var index = 0;
                    foreach (var vEntry in agent->LobbyData.CharaSelectEntries)
                    {
                        if (vEntry.Value->ContentId == traveldata.ContentID)
                        {
                            Callback(addon, true, 21, index);
                            Callback(addon, true, 29, 0, index);
                            Callback(addon, true, 21, index);

                            TaskHelper.Enqueue(() => ClickSelectYesnoYes(), "点击确认登录", weight: 1);
                            return;
                        }

                        index++;
                    }
                }
                catch
                {
                    // ignored
                }
            }
        }, "尝试登录", weight: 1);
    }

    #endregion

    #region 工具

    private static bool? LeaveNonCrossWorldParty()
    {
        if (DService.PartyList.Length < 2 || DService.Condition[ConditionFlag.ParticipatingInCrossWorldPartyOrAlliance]) 
            return true;
        if (!Throttler.Throttle("FastWorldTravel-LeaveNonCrossWorldParty")) 
            return false;

        ChatManager.SendMessage("/leave");
        return DService.PartyList.Length < 2;
    }

    #endregion

    protected override void Uninit()
    {
        DService.AddonLifecycle.UnregisterListener(OnAddon);
        
        HandleDtrEntry(false);
        
        Addon?.Dispose();
        Addon = null;

        FrameworkManager.Unreg(OnUpdate);
        GameState.Login -= OnLogin;
        CommandManager.RemoveSubCommand(Command);
    }

    private class Config : ModuleConfiguration
    {
        public bool    AutoLeaveParty   = true;
        public bool    AddDtrEntry      = true;
        public bool    ReplaceOrigAddon = true;
        public Vector2 AddonPosition    = new(800f, 350f);
    }

    private struct Travel
    {
        public uint    CurrentWorldID;
        public uint    HomeWorldID;
        public uint    TargetWorldID;
        public ulong   ContentID;
        public bool    IsBack;
        public string  Name;
        public string? Description; // 需要切换大区的名字
    }

    private class AddonDRFastWorldTravel(TaskHelper TaskHelper) : NativeAddon
    {
        private static NodeBase TeleportWidget;

        private static readonly Version MinDCTravelerXVersion = new("0.1.6.0");
        
        private static bool IsPluginEnabled => 
            IsPluginEnabled("DCTravelerX", MinDCTravelerXVersion);

        private static bool IsPluginValid => 
            IsPluginEnabled && IsDCTravelerValid;

        private static bool LastOpenPluginState;

        private static bool LastForegroundState;

        private static Dictionary<uint, TextButtonNode> WorldToButtons = [];
        
        protected override unsafe void OnSetup(AtkUnitBase* addon)
        {
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
                        SeString         = message.Encode(),
                        FontSize         = 14,
                        IsVisible        = true,
                        Size             = new(150f, 25f),
                        AlignmentType    = AlignmentType.Center,
                        Position         = new(305f, -22f),
                        TextFlags        = TextFlags.Bold | TextFlags.Edge,
                        TextOutlineColor = ColorHelper.GetColor(7)
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

            if (Throttler.Throttle("FastWorldTravel-OnAddonUpdate") && LastOpenPluginState != IsPluginValid)
            {
                Close();
                
                TaskHelper.Abort();
                TaskHelper.DelayNext(100);
                TaskHelper.Enqueue(() => !IsOpen, "等待界面完全关闭");
                TaskHelper.Enqueue(() => Open(), "重新打开");
                
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
            if (Throttler.Throttle("FastWorldTravel-OnAddonUpdate-RequestQueueTime", GameState.IsForeground ? 15_000 : 90_000))
                RequestWaitTimeInfoUpdate();
            
            if (Throttler.Throttle("FastWorldTravel-OnAddonUpdate-UpdateQueueTime", 1_000))
                UpdateWaitTimeInfo();
        }

        protected override unsafe void OnFinalize(AtkUnitBase* addon)
        {
            if (addon != null && this != null)
            {
                ModuleConfig.AddonPosition = RootNode.Position;
                ModuleConfig.Save(ModuleManager.GetModule<FastWorldTravel>());
            }
            
            base.OnFinalize(addon);
        }

        private void RequestWaitTimeInfoUpdate()
        {
            DService.Framework.RunOnTick(async () =>
            {
                if (!IsOpen || !IsPluginValid || WorldToButtons is not { Count: > 0 }) return;
                await RequestDCTravelInfo.InvokeFunc();
            });
        }
        
        private void UpdateWaitTimeInfo()
        {
            if (!IsOpen || !IsPluginValid || WorldToButtons is not { Count: > 0 }) return;
            
            foreach (var (worldID, node) in WorldToButtons)
            {
                var time = GetDCTravelWaitTime.InvokeFunc(worldID);
                if (time == -1) return;
                
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
                    
                    
                node.Tooltip = builder.Build().Encode();
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
            var mainLayoutContainer = new HorizontalListNode
            {
                IsVisible = true,
            };

            // 1. 当前大区
            var currentDCWorlds = CurrentWorlds.Select(kvp => new Tuple<uint, string>(kvp.Key, kvp.Value));
            var currentDCColumn = CreateDataCenterColumn(GameState.CurrentDataCenterData.Name.ExtractText() ?? "Current", currentDCWorlds);
            mainLayoutContainer.AddNode(currentDCColumn);

            if (!GameState.IsCN) return mainLayoutContainer;

            // 2. 其他大区 (仅国服)
            var otherDataCenters = CNDataCenter
                                   .Where(kvp => kvp.Key != GameState.CurrentWorldData.DataCenter.RowId)
                                   .Select(kvp => new
                                   {
                                       Name = LuminaGetter.GetRow<WorldDCGroupType>(kvp.Key)?.Name.ExtractText() ?? "Unknown",
                                       Worlds = kvp.Value.OrderBy(w => w.Item2).ToList()
                                   })
                                   .OrderBy(dc => dc.Name)
                                   .ToList();

            foreach (var dataCenter in otherDataCenters)
            {
                mainLayoutContainer.AddNode(new ResNode { Size = new Vector2(25, 0), IsVisible = true });

                var otherDCColumn = CreateDataCenterColumn(dataCenter.Name, dataCenter.Worlds);
                mainLayoutContainer.AddNode(otherDCColumn);
            }

            return mainLayoutContainer;
        }

        private static VerticalListNode CreateDataCenterColumn(string dcName, IEnumerable<Tuple<uint, string>> worlds)
        {
            var column = new VerticalListNode { IsVisible = true };
            var totalHeight = 0f;

            var header = new TextNode
            {
                SeString      = dcName,
                FontSize      = 20,
                IsVisible     = true,
                Size          = new(150f, 30f),
                AlignmentType = AlignmentType.Center,
                TextFlags     = TextFlags.Edge | TextFlags.Emboss,
                TextColor     = ColorHelper.GetColor(1)
            };
            column.AddNode(header);
            totalHeight += header.Size.Y;

            var headerSpacer = new ResNode { Size = new(0, 15), IsVisible = true };
            column.AddNode(headerSpacer);
            totalHeight += headerSpacer.Size.Y;

            foreach (var (worldID, worldName) in worlds)
            {
                var worldNameT = $"{char.ToUpper(worldName[0])}{worldName[1..]}";
                var worldNameBuilder = new SeStringBuilder().Append(worldNameT);
                if (GameState.HomeWorld == worldID)
                {
                    worldNameBuilder.Append(" ");
                    worldNameBuilder.AddIcon(BitmapFontIcon.CrossWorld);
                }

                var button = new TextButtonNode
                {
                    Size      = new(150f, 40f),
                    IsVisible = true,
                    SeString  = worldNameBuilder.Build().Encode(),
                    OnClick = () =>
                    {
                        Addon.Close();
                        ChatManager.SendMessage($"/pdr worldtravel {worldName}");
                    },
                    IsEnabled = GameState.CurrentWorld != worldID && (CurrentWorlds.ContainsKey(worldID) || IsPluginValid)
                };

                button.LabelNode.TextOutlineColor =  KnownColor.Black.ToVector4();
                button.LabelNode.TextFlags        |= TextFlags.Edge | TextFlags.Emboss;
                
                column.AddNode(button);
                if (GameState.IsCN)
                    WorldToButtons.Add(worldID, button);
                totalHeight += button.Size.Y;

                var buttonSpacer = new ResNode { Size = new(0, 5), IsVisible = true };
                column.AddNode(buttonSpacer);
                totalHeight += buttonSpacer.Size.Y;
            }
            column.Size = new(150f, totalHeight);
            
            return column;
        }
    }
}
