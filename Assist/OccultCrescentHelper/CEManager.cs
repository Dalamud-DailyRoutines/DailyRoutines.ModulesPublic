using System.Numerics;
using DailyRoutines.Extensions;
using DailyRoutines.Manager;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Fates;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using FFXIVClientStructs.FFXIV.Client.Enums;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Fate;
using FFXIVClientStructs.FFXIV.Client.Game.InstanceContent;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using OmenTools.Dalamud;
using OmenTools.Info.Game.Enums;
using OmenTools.Interop.Game.Helpers;
using OmenTools.Interop.Game.Lumina;
using OmenTools.Interop.Game.Models;
using OmenTools.OmenService;
using OmenTools.Threading;
using OmenTools.Threading.TaskHelper;
using TimeAgo;
using FateState = Dalamud.Game.ClientState.Fates.FateState;

namespace DailyRoutines.ModulesPublic;

public partial class OccultCrescentHelper
{
    private unsafe class CEManager
    (
        OccultCrescentHelper mainModule
    ) : BaseIslandModule(mainModule)
    {
        private const string COMMAND_FATE = "pfate";
        private const string COMMAND_CE   = "pce";

        private          HashSet<IslandEventData> allIslandEvents = [];
        private readonly HashSet<string>          knownCENames    = [];

        private readonly Dictionary<long, DateTime> localTimes = [];

        private TaskHelper? ceTaskHelper;

        public override void Init()
        {
            ceTaskHelper ??= new() { TimeoutMS = 180_000 };

            DService.Instance().ClientState.TerritoryChanged += OnZoneChanged;
            ExecuteCommandManager.Instance().RegPost(OnPostReceivedCommand);
            LogMessageManager.Instance().RegPost(OnPostReceivedMessage);
            GameState.Instance().Logout += OnLogout;

            var isAnyNewCategory = false;

            foreach (var eventType in Enum.GetValues<CrescentEventType>())
            {
                if (!MainModule.config.IsEnabledNotifyEventsCategoried.TryAdd(eventType, true)) continue;
                isAnyNewCategory = true;
            }

            if (isAnyNewCategory)
                MainModule.config.Save(MainModule);

            CommandManager.Instance().AddSubCommand
            (
                COMMAND_FATE,
                new(OnCommandFate) { HelpMessage = $"{Lang.Get("OccultCrescentHelper-Command-PFate-Help")}" }
            );

            CommandManager.Instance().AddSubCommand
            (
                COMMAND_CE,
                new(OnCommandCE) { HelpMessage = $"{Lang.Get("OccultCrescentHelper-Command-PCE-Help")}" }
            );
        }

        public override void Uninit()
        {
            CommandManager.Instance().RemoveSubCommand(COMMAND_FATE);
            CommandManager.Instance().RemoveSubCommand(COMMAND_CE);

            GameState.Instance().Logout -= OnLogout;
            ExecuteCommandManager.Instance().Unreg(OnPostReceivedCommand);
            LogMessageManager.Instance().Unreg(OnPostReceivedMessage);
            DService.Instance().ClientState.TerritoryChanged -= OnZoneChanged;

            // 清理资源
            OnZoneChanged(0);

            ceTaskHelper?.Dispose();
            ceTaskHelper = null;
        }

        public override void DrawConfig()
        {
            using var id = ImRaii.PushId("CEManager");

            using (FontManager.Instance().UIFont.Push())
            {
                ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), Lang.Get("OccultCrescentHelper-FastTeleport"));

                ImGui.SameLine(0, 8f * GlobalUIScale);

                if (ImGui.SmallButton($"{Lang.Get("Stop")}##StopCE"))
                {
                    ceTaskHelper.Abort();
                    vnavmeshIPC.StopPathfind();
                }

                using (ImRaii.PushIndent())
                {
                    foreach (var ce in allIslandEvents)
                    {
                        if (!DService.Instance().Texture.TryGetFromGameIcon(new(ce.Event.IconID), out var texture)) continue;

                        using (ImRaii.Disabled(ce.Event.Type == CrescentEventType.CE && ce.Event.CEState != DynamicEventState.Register))
                        {
                            if (ImGuiOm.SelectableImageWithText
                                (
                                    texture.GetWrapOrEmpty().Handle,
                                    new(ImGui.GetTextLineHeightWithSpacing()),
                                    $"{ce.Event.NameDisplay}",
                                    false
                                ))
                                TeleportToCE(ce);
                        }
                    }
                }
            }

            ImGui.NewLine();

            if (GameState.TerritoryIntendedUse == TerritoryIntendedUse.OccultCrescent &&
                ImGui.CollapsingHeader($"{Lang.Get("OccultCrescentHelper-CEManager-CEHistory")} ({MainModule.GetIslandID()})###CEHistory"))
            {
                using (var table = ImRaii.Table("###CEHistoryTable", 2, ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders))
                {
                    if (table)
                    {
                        ImGui.TableSetupColumn($"{Lang.Get("Name")}",                                              ImGuiTableColumnFlags.WidthStretch, 30);
                        ImGui.TableSetupColumn($"{Lang.Get("OccultCrescentHelper-CEManager-CEHistory-LastTime")}", ImGuiTableColumnFlags.WidthStretch, 20);

                        ImGui.TableHeadersRow();

                        foreach (var ceID in CrescentEvent.EventToItem.Keys)
                        {
                            if (LuminaWrapper.GetDynamicEventName(ceID) is not { } name ||
                                string.IsNullOrEmpty(name))
                                continue;

                            ImGui.TableNextRow();

                            ImGui.TableNextColumn();
                            ImGuiOm.TextOutlined(*ImGui.GetStyleColorVec4(ImGuiCol.Text), $"{name}", KnownColor.LightSkyBlue.ToVector4(), 0.1f);

                            ImGui.TableNextColumn();

                            if (MainModule.config.CEHistory.TryGetValue(MainModule.GetIslandID(), out var history) &&
                                history.TryGetValue(ceID, out var time))
                            {
                                var dateTime = localTimes.GetOrAdd(time, _ => time.ToUTCDateTimeFromUnixSeconds().ToLocalTime());
                                ImGui.TextUnformatted($"{dateTime.TimeAgo()}\t\t\t({dateTime:MM/dd HH:mm:ss})");
                            }
                            else
                                ImGui.TextUnformatted("-");
                        }
                    }
                }

                ImGui.TextWrapped(Lang.Get("OccultCrescentHelper-CEManager-CEHistory-Notify"));
            }

            ImGui.NewLine();


            if (ImGui.Checkbox($"{Lang.Get("OccultCrescentHelper-PrioritizeMoveTo")}", ref MainModule.config.IsEnabledMoveToEvent))
                MainModule.config.Save(MainModule);
            ImGuiOm.HelpMarker(Lang.Get("OccultCrescentHelper-CEManager-PrioritizeMoveTo-Help"), 20f * GlobalUIScale);

            if (MainModule.config.IsEnabledMoveToEvent)
            {
                ImGui.SetNextItemWidth(150f * GlobalUIScale);
                ImGui.SliderFloat($"{Lang.Get("OccultCrescentHelper-CEManager-PrioritizeMoveTo-LeftTime")}", ref MainModule.config.LeftTimeMoveToEvent, 1f, 180f, "%.1f");
                if (ImGui.IsItemDeactivatedAfterEdit())
                    MainModule.config.Save(MainModule);
                ImGuiOm.HelpMarker($"{Lang.Get("OccultCrescentHelper-CEManager-PrioritizeMoveTo-LeftTime-Help")}", 20f * GlobalUIScale);
            }

            ImGui.NewLine();

            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), Lang.Get("OccultCrescentHelper-CEManager-NotifyEventAppears"));
            ImGuiOm.HelpMarker(Lang.Get("OccultCrescentHelper-CEManager-NotifyEventAppears-Help"), 20f * GlobalUIScale);

            ImGui.SameLine(0, 8f * GlobalUIScale);
            if (ImGui.Checkbox("###NotifyEventAppears", ref MainModule.config.IsEnabledNotifyEvents))
                MainModule.config.Save(MainModule);

            if (MainModule.config.IsEnabledNotifyEvents)
            {
                using (ImRaii.PushIndent())
                {
                    var counter = 0;

                    foreach (var (type, isEnabled) in MainModule.config.IsEnabledNotifyEventsCategoried)
                    {
                        using var isEnabledNotifyEventsDataID = ImRaii.PushId($"{type}");

                        using (ImRaii.Group())
                        {
                            var isEnabledCopy = isEnabled;

                            if (ImGui.Checkbox($"{CrescentEvent.GetEventTypeName(type)}##{type}", ref isEnabledCopy))
                            {
                                MainModule.config.IsEnabledNotifyEventsCategoried[type] = isEnabledCopy;
                                MainModule.config.Save(MainModule);
                            }
                        }

                        if (counter != 7 && counter != 11 && counter != MainModule.config.IsEnabledNotifyEventsCategoried.Count - 1)
                            ImGui.SameLine(0, 4f * GlobalUIScale);
                        counter++;
                    }
                }
            }

            ImGui.NewLine();

            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), Lang.Get("OccultCrescentHelper-CEManager-NotifyCEStarts"));
            ImGuiOm.HelpMarker(Lang.Get("OccultCrescentHelper-CEManager-NotifyCEStarts-Help"), 20f * GlobalUIScale);

            ImGui.SameLine(0, 8f * GlobalUIScale);
            if (ImGui.Checkbox("###NotifyCEStarts", ref MainModule.config.IsEnabledNotifyCEStarts))
                MainModule.config.Save(MainModule);

            ImGui.NewLine();

            ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), Lang.Get("Command"));

            using (ImRaii.PushIndent())
            {
                ImGui.TextUnformatted($"/pdr {COMMAND_FATE} {Lang.Get("OccultCrescentHelper-Command-PFate-Help")}");

                ImGui.TextUnformatted($"/pdr {COMMAND_CE} {Lang.Get("OccultCrescentHelper-Command-PCE-Help")}");
            }
        }

        private void OnLogout() =>
            ceTaskHelper.Abort();

        private void OnZoneChanged(ushort obj)
        {
            allIslandEvents.Clear();
            knownCENames.Clear();
            ceTaskHelper?.Abort();
        }

        public override void OnUpdate()
        {
            var publicInstance = PublicContentOccultCrescent.GetInstance();
            if (publicInstance == null) return;

            var islandID = MainModule.GetIslandID();
            MainModule.config.CEHistory.TryAdd(islandID, []);

            var currentCENames = new HashSet<string>();
            var newCEData      = new List<IslandEventData>();

            // FATE
            foreach (var fate in DService.Instance().Fate)
            {
                if (IslandEventData.Parse(fate) is not { } safeFate) continue;

                newCEData.Add(safeFate);
                currentCENames.Add(safeFate.Event.Name);

                if (allIslandEvents.TryGetValue(safeFate, out var existed))
                    existed.Update(fate);
                else
                    allIslandEvents.Add(safeFate);

                if (knownCENames.Add(safeFate.Event.Name))
                    NotifyNewCE(safeFate);
            }

            // CE
            var data = publicInstance->DynamicEventContainer.Events
                                                            .ToArray()
                                                            .Select(x => x)
                                                            .ToList();

            foreach (var dynamicEvent in data)
            {
                if (IslandEventData.Parse(dynamicEvent) is not { } safeCE) continue;

                newCEData.Add(safeCE);
                currentCENames.Add(safeCE.Event.Name);

                if (allIslandEvents.TryGetValue(safeCE, out var existed))
                    existed.Update(dynamicEvent);
                else
                    allIslandEvents.Add(safeCE);

                if (knownCENames.Add(safeCE.Event.Name))
                    NotifyNewCE(safeCE);

                // 因为从刷新到正式开始时间为 3 分钟
                MainModule.config.CEHistory[islandID][safeCE.Event.DataID] = safeCE.Event.CEStartTime - 180;
            }

            knownCENames.IntersectWith(currentCENames);
            allIslandEvents.IntersectWith(newCEData);

            if (Throttler.Shared.Throttle("OccultCrescentHelper-CEManager-OnUpdate-SaveCEHistory", 10_000))
                MainModule.config.Save(MainModule);
        }

        private void OnPostReceivedCommand(ExecuteCommandFlag command, uint param1, uint param2, uint param3, uint param4)
        {
            if (command                        != ExecuteCommandFlag.FateLoad ||
                GameState.TerritoryIntendedUse != TerritoryIntendedUse.OccultCrescent)
                return;

            OnUpdate();
        }

        // CE 开始
        private void OnPostReceivedMessage(uint logMessageID, LogMessageQueueItem values)
        {
            if (logMessageID                   != 11002                               ||
                GameState.TerritoryIntendedUse != TerritoryIntendedUse.OccultCrescent ||
                !MainModule.config.IsEnabledNotifyCEStarts)
                return;

            ceTaskHelper.Abort();

            var message = Lang.Get("OccultCrescentHelper-CEManager-Notification-CEStart");
            NotifyHelper.Instance().NotificationInfo(message);
            NotifyHelper.Speak(message);
        }

        private void OnClickTeleport(uint id, SeString message)
        {
            if (allIslandEvents.FirstOrDefault(x => x.LinkPayloadID == id) is not { } ce) return;
            TeleportToCE(ce);
        }

        private void OnCommandFate(string command, string args)
        {
            if (GameState.TerritoryIntendedUse != TerritoryIntendedUse.OccultCrescent) return;

            args = args.Trim().ToLowerInvariant();

            if (args == "abort")
            {
                ceTaskHelper.Abort();
                vnavmeshIPC.StopPathfind();
                return;
            }

            var fate = allIslandEvents.Where(x => x.Event is { Type: CrescentEventType.FATE, Progress: < 80 }).OrderBy(x => x.Event.Progress).FirstOrDefault();
            if (fate == null) return;

            TeleportToCE(fate);
        }

        private void OnCommandCE(string command, string args)
        {
            if (GameState.TerritoryIntendedUse != TerritoryIntendedUse.OccultCrescent) return;

            args = args.Trim().ToLowerInvariant();

            if (args == "abort")
            {
                ceTaskHelper.Abort();
                vnavmeshIPC.StopPathfind();
                return;
            }

            var ce = allIslandEvents.FirstOrDefault(x => x.Event is { Type: CrescentEventType.CE, CEState: DynamicEventState.Register, CELeftTimeSecond: > 15 });
            if (ce == null) return;

            TeleportToCE(ce);
        }

        private void TeleportToCE(IslandEventData data)
        {
            if (DService.Instance().ObjectTable.LocalPlayer is null) return;

            // 不在开始前状态, 禁止 TP 过去, 太危险了
            if (data.Event.Type == CrescentEventType.CE && data.Event.CEState != DynamicEventState.Register)
                return;

            // 没开绿玩移动或时间不够了
            if (!MainModule.config.IsEnabledMoveToEvent ||
                data.Event.Type == CrescentEventType.CE && data.Event.CELeftTimeSecond < MainModule.config.LeftTimeMoveToEvent)
            {
                ceTaskHelper.Abort();

                TP(data.Event.GetRandomPointNearEdge() + new Vector3(0, 1, 0), ceTaskHelper);
                return;
            }

            // 先跑去使用以太之光
            if (CrescentAetheryte.TryGetNearestSouthHorn(data.Event.Position, out var aetheryte))
            {
                // 进化的毒鸟——高等魔鸟
                if (data.Event.DataID == 1967)
                    aetheryte = CrescentAetheryte.CrystallizedCaverns;

                ceTaskHelper.Abort();
                ceTaskHelper.Enqueue(() => MainModule.aetheryteModule.UseAetheryte(aetheryte));

                ceTaskHelper.DelayNext(1000);
                ceTaskHelper.Enqueue(() => !MainModule.aetheryteModule.IsTaskHelperBusy);
            }

            ceTaskHelper.Enqueue
            (() =>
                {
                    if (DService.Instance().Condition.IsOccupiedInEvent) return false;
                    if (DService.Instance().Condition[ConditionFlag.Mounted]) return true;
                    return UseActionManager.Instance().UseAction(ActionType.GeneralAction, 9);
                }
            );

            ceTaskHelper.Enqueue
            (() =>
                {
                    if (!Throttler.Shared.Throttle("OccultCrescentHelper-CEManager-MoveTo")) return false;
                    if (vnavmeshIPC.GetIsPathfindRunning()) return true;

                    vnavmeshIPC.PathfindAndMoveTo(data.Event.GetRandomPointNearEdge(), false);
                    return false;
                }
            );

            ceTaskHelper.Enqueue
            (() =>
                {
                    if (!Throttler.Shared.Throttle("OccultCrescentHelper-CEManager-WaitMoveTo")) return false;

                    // CE / FATE 寄了
                    if (allIslandEvents.FirstOrDefault(x => x == data) is null)
                    {
                        ceTaskHelper.Abort();
                        return true;
                    }

                    if (!vnavmeshIPC.GetIsPathfindRunning() ||
                        data.Event.Type is CrescentEventType.FATE or CrescentEventType.MagicPot &&
                        FateManager.Instance()->CurrentFate         != null                     &&
                        FateManager.Instance()->CurrentFate->FateId == data.Event.DataID)
                    {
                        ExecuteCommandManager.Instance().ExecuteCommand(ExecuteCommandFlag.Dismount);
                        vnavmeshIPC.StopPathfind();
                        return true;
                    }

                    return false;
                }
            );

            if (data.Event.Type is CrescentEventType.FATE or CrescentEventType.MagicPot)
            {
                ceTaskHelper.Enqueue
                (() =>
                    {
                        if (DService.Instance().Condition[ConditionFlag.Mounted]) return false;

                        ChatManager.Instance().SendMessage("/tenemy");
                        return true;
                    }
                );
                ceTaskHelper.DelayNext(100);
                ceTaskHelper.Enqueue(() => ChatManager.Instance().SendMessage("/facetarget"));
                ceTaskHelper.DelayNext(100);
                ceTaskHelper.Enqueue(() => ChatManager.Instance().SendMessage("/automove on"));
            }
            else if (data.Event.Type is CrescentEventType.CE)
            {
                if (Random.Shared.NextDouble() >= 0.6)
                {
                    ceTaskHelper.DelayNext(Random.Shared.Next(500, 3000));
                    ceTaskHelper.Enqueue
                    (() =>
                        {
                            if (DService.Instance().Condition[ConditionFlag.Mounted]) return false;

                            vnavmeshIPC.PathfindAndMoveTo(data.Event.GetRandomPointNearEdge(), false);
                            return true;
                        }
                    );

                    ceTaskHelper.DelayNext(Random.Shared.Next(500, 3000));
                    ceTaskHelper.Enqueue
                    (() =>
                        {
                            if (DService.Instance().Condition[ConditionFlag.Mounted] || vnavmeshIPC.GetIsPathfindRunning()) return false;

                            vnavmeshIPC.PathfindAndMoveTo(data.Event.Position, false);
                            return true;
                        }
                    );

                    ceTaskHelper.DelayNext(200);
                    ceTaskHelper.Enqueue(vnavmeshIPC.StopPathfind);
                }
            }
        }

        private void NotifyNewCE(IslandEventData ce)
        {
            if (!MainModule.config.IsEnabledNotifyEvents ||
                !MainModule.config.IsEnabledNotifyEventsCategoried.GetValueOrDefault(ce.Event.Type, false))
                return;

            var ceName   = ce.Event.NameDisplay;
            var position = ce.Event.Position;

            var mapPos      = PositionHelper.WorldToMap(position.ToVector2(), GameState.MapData);
            var linkPayload = ce.GetOrAddLinkPayload(this);

            var message = new SeStringBuilder()
                          .AddUiForeground(25)
                          .AddText($"[{MainModule.Info.Title}] ")
                          .AddUiForegroundOff()
                          .AddText($"{ce.GetNotificationTitle()}")
                          .Add(NewLinePayload.Payload)
                          .AddText($"{Lang.Get("Name")}: ")
                          .AddUiForeground(45)
                          .AddText(ceName)
                          .AddUiForegroundOff()
                          .Add(NewLinePayload.Payload)
                          .AddText($"{Lang.Get("Position")}: ")
                          .Append(SeString.CreateMapLink(GameState.TerritoryTypeData.ExtractPlaceName(), mapPos.X, mapPos.Y));

            if (ce.Event.DemiatmaID != 0)
            {
                message.Add(NewLinePayload.Payload)
                       .AddText($"{Lang.Get("Item")}: ")
                       .AddItemLink(ce.Event.DemiatmaID, false)
                       .AddText($" ({LuminaWrapper.GetAddonText(358)}: {LocalPlayerState.GetItemCount(ce.Event.DemiatmaID)})");
            }

            if (ce.Event.SpecialRewards is { Count: > 0 } specialRewards)
            {
                var prefix = Lang.Get("OccultCrescentHelper-CEManager-SpecialRewards");
                message.Add(NewLinePayload.Payload)
                       .AddText($"{prefix}: ");

                foreach (var specialReward in specialRewards)
                {
                    var isObtained = CrescentEvent.IsSpecialRewardUnlocked(specialReward);

                    ushort textColor = isObtained switch
                    {
                        true  => 45,
                        false => 17,
                        null  => 32
                    };

                    var text = isObtained switch
                    {
                        true  => "✓",
                        false => "x",
                        null  => "?"
                    };

                    message.Add(NewLinePayload.Payload)
                           .AddText("      ")
                           .AddItemLink(specialReward)
                           .AddText(" (")
                           .AddUiForeground(textColor)
                           .AddText(text)
                           .AddUiForegroundOff()
                           .AddText(")");
                }
            }

            if (ce.Event.Type != CrescentEventType.CE || ce.Event.CEState == DynamicEventState.Register)
            {
                message.Add(NewLinePayload.Payload)
                       .AddText($"{Lang.Get("Operation")}: ")
                       .Add(RawPayload.LinkTerminator)
                       .Add(linkPayload)
                       .AddText("[")
                       .AddIcon(BitmapFontIcon.Aethernet)
                       .AddUiForeground(35)
                       .AddText($"{Lang.Get("Teleport")}")
                       .AddUiForegroundOff()
                       .AddText("]")
                       .Add(RawPayload.LinkTerminator);
            }

            NotifyHelper.Instance().Chat(message.Build());

            NotifyHelper.Instance().NotificationInfo($"{ceName}", $"{ce.GetNotificationTitle()}");
            NotifyHelper.Speak($"{ce.GetNotificationTitle()}");
        }

        public class IslandEventData
        (
            uint dataID
        ) : IEquatable<IslandEventData>
        {
            public CrescentEvent Event { get; } = new(dataID);

            public int                LinkPayloadID { get; private set; } = -1;
            public DalamudLinkPayload LinkPayload   { get; private set; }

            public bool Equals(IslandEventData? other)
            {
                if (other is null) return false;
                if (ReferenceEquals(this, other)) return true;

                return Event == other.Event;
            }

            public static IslandEventData? Parse(IFate fate)
            {
                if (fate.MapIconId == 0 || fate.State is FateState.Ended or FateState.WaitingForEnd or FateState.Failed) return null;
                if (fate.Position == default) return null;

                var name = $"{fate.Name} ({fate.Progress}%)";
                if (string.IsNullOrEmpty(name)) return null;

                var data = new IslandEventData(fate.FateId);
                data.Event.UpdateTempDataFATE(name, fate.Progress, fate.State);
                data.Event.UpdatePositionAndRadius(fate.Position, fate.Radius);

                return data;
            }

            public static IslandEventData? Parse(DynamicEvent ce)
            {
                if (!LuminaGetter.TryGetRow(ce.DynamicEventId, out Lumina.Excel.Sheets.DynamicEvent data)) return null;
                if (ce.State is DynamicEventState.Inactive) return null;
                if (data.RowId != 48 && ce.MapMarker.Position == default) return null;

                var leftTime = ce.StartTimestamp - GameState.ServerTimeUnix;
                if (leftTime < 0)
                    leftTime = 0;

                var name = ce.Name.ToString();

                if (data.RowId != 48) // 两歧塔 力之塔
                {
                    name = ce.State switch
                    {
                        DynamicEventState.Battle   => $"{ce.Name} ({Lang.Get("OccultCrescentHelper-CEManager-CEName-InBattle", ce.Participants, ce.Progress)})",
                        DynamicEventState.Register => $"{ce.Name} ({Lang.Get("OccultCrescentHelper-CEManager-CEName-Register", leftTime)})",
                        DynamicEventState.Warmup   => $"{ce.Name} ({Lang.Get("OccultCrescentHelper-CEManager-CEName-WarmUp")})",
                        _                          => $"{ce.Name}"
                    };
                }

                if (string.IsNullOrEmpty(name)) return null;

                var returnValue = new IslandEventData(data.RowId);
                returnValue.Event.UpdateTempDataCE
                (
                    name,
                    ce.Progress,
                    ce.State,
                    ce.State == DynamicEventState.Register ? ce.StartTimestamp : ce.StartTimestamp - 1200,
                    leftTime
                );
                returnValue.Event.UpdatePositionAndRadius(ce.MapMarker.Position, 0);
                return returnValue;
            }

            public void Update(IFate fate)
            {
                var name = $"{fate.Name} ({fate.Progress}%)";
                Event.UpdateTempDataFATE(name, fate.Progress, fate.State);
            }

            public void Update(DynamicEvent ce)
            {
                if (!LuminaGetter.TryGetRow(ce.DynamicEventId, out Lumina.Excel.Sheets.DynamicEvent data))
                    return;

                var leftTime = ce.StartTimestamp - GameState.ServerTimeUnix;
                if (leftTime < 0)
                    leftTime = 0;

                var name = ce.Name.ToString();

                if (data.RowId != 48)
                {
                    name = ce.State switch
                    {
                        DynamicEventState.Battle   => $"{ce.Name} ({Lang.Get("OccultCrescentHelper-CEManager-CEName-InBattle", ce.Participants, ce.Progress)})",
                        DynamicEventState.Register => $"{ce.Name} ({Lang.Get("OccultCrescentHelper-CEManager-CEName-Register", leftTime)})",
                        DynamicEventState.Warmup   => $"{ce.Name} ({Lang.Get("OccultCrescentHelper-CEManager-CEName-WarmUp")})",
                        _                          => $"{ce.Name}"
                    };
                }

                Event.UpdateTempDataCE
                (
                    name,
                    ce.Progress,
                    ce.State,
                    ce.State == DynamicEventState.Register ? ce.StartTimestamp : ce.StartTimestamp - 1200,
                    leftTime
                );
            }

            public string GetNotificationTitle() => Event.Type switch
            {
                CrescentEventType.FATE      => Lang.Get("OccultCrescentHelper-CEManager-Notification-FATE"),
                CrescentEventType.MagicPot  => Lang.Get("OccultCrescentHelper-CEManager-Notification-MagicPot"),
                CrescentEventType.CE        => Lang.Get("OccultCrescentHelper-CEManager-Notification-CE"),
                CrescentEventType.ForkTower => Lang.Get("OccultCrescentHelper-CEManager-Notification-ForkTower"),
                _                           => Lang.Get("OccultCrescentHelper-CEManager-Notification-FATE")
            };

            public DalamudLinkPayload GetOrAddLinkPayload(CEManager manager)
            {
                if (LinkPayloadID != -1) return LinkPayload;

                LinkPayload   = LinkPayloadManager.Instance().Reg(manager.OnClickTeleport, out var id);
                LinkPayloadID = (int)id;

                return LinkPayload;
            }

            public override bool Equals(object? obj) => Equals(obj as IslandEventData);

            public override int GetHashCode() => HashCode.Combine(Event);

            public static bool operator ==(IslandEventData? left, IslandEventData? right) => Equals(left, right);

            public static bool operator !=(IslandEventData? left, IslandEventData? right) => !Equals(left, right);
        }
    }
}
