using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using DailyRoutines.IPC;
using DailyRoutines.Managers;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Fates;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using FFXIVClientStructs.FFXIV.Client.Enums;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Fate;
using FFXIVClientStructs.FFXIV.Client.Game.InstanceContent;
using TimeAgo;
using FateState = Dalamud.Game.ClientState.Fates.FateState;

namespace DailyRoutines.ModulesPublic;

public partial class OccultCrescentHelper
{
    public unsafe class CEManager(OccultCrescentHelper mainModule) : BaseIslandModule(mainModule)
    {
        private const string CommandFate = "pfate";
        private const string CommandCE  = "pce";
        
        private static          HashSet<IslandEventData> AllIslandEvents = [];
        private static readonly HashSet<string>          KnownCENames    = [];

        private static TaskHelper? CETaskHelper;

        public override void Init()
        {
            CETaskHelper ??= new() { TimeLimitMS = 180_000 };

            DService.ClientState.TerritoryChanged += OnZoneChanged;
            ExecuteCommandManager.RegPost(OnPostReceivedCommand);
            LogMessageManager.Register(OnPostReceivedMessage);
            GameState.Logout += OnLogout;

            var isAnyNewCategory = false;
            foreach (var eventType in Enum.GetValues<CrescentEventType>())
            {
                if (!ModuleConfig.IsEnabledNotifyEventsCategoried.TryAdd(eventType, true)) continue;
                isAnyNewCategory = true;
            }
            if (isAnyNewCategory)
                ModuleConfig.Save(MainModule);
            
            CommandManager.AddSubCommand(CommandFate,
                                         new(OnCommandFate) { HelpMessage = $"{GetLoc("OccultCrescentHelper-Command-PFate-Help")}" });

            CommandManager.AddSubCommand(CommandCE,
                                         new(OnCommandCE) { HelpMessage = $"{GetLoc("OccultCrescentHelper-Command-PCE-Help")}" });
        }
        
        public override void Uninit()
        {
            CommandManager.RemoveSubCommand(CommandFate);
            CommandManager.RemoveSubCommand(CommandCE);
            
            GameState.Logout -= OnLogout;
            ExecuteCommandManager.Unreg(OnPostReceivedCommand);
            LogMessageManager.Unregister(OnPostReceivedMessage);
            DService.ClientState.TerritoryChanged -= OnZoneChanged;

            // 清理资源
            OnZoneChanged(0);

            CETaskHelper?.Dispose();
            CETaskHelper = null;
        }

        public override void DrawConfig() 
        {
            using var id = ImRaii.PushId("CEManager");

            using (FontManager.UIFont.Push())
            {
                ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), GetLoc("OccultCrescentHelper-FastTeleport"));

                ImGui.SameLine(0, 8f * GlobalFontScale);
                if (ImGui.SmallButton($"{GetLoc("Stop")}##StopCE"))
                {
                    CETaskHelper.Abort();
                    vnavmeshIPC.PathStop();
                }

                using (ImRaii.PushIndent())
                {
                    foreach (var ce in AllIslandEvents)
                    {
                        if (!DService.Texture.TryGetFromGameIcon(new(ce.Event.IconID), out var texture)) continue;

                        using (ImRaii.Disabled(ce.Event.Type == CrescentEventType.CE && ce.Event.CEState != DynamicEventState.Register))
                        {
                            if (ImGuiOm.SelectableImageWithText(texture.GetWrapOrEmpty().Handle, new(ImGui.GetTextLineHeightWithSpacing()),
                                                                $"{ce.Event.NameDisplay}", false))
                                TeleportToCE(ce);
                        }
                    }
                }
            }

            ImGui.NewLine();

            if (GameState.TerritoryIntendedUse == TerritoryIntendedUse.OccultCrescent && 
                ImGui.CollapsingHeader($"{GetLoc("OccultCrescentHelper-CEManager-CEHistory")} ({GetIslandID()})###CEHistory"))
            {
                using (var table = ImRaii.Table("###CEHistoryTable", 2, ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders))
                {
                    if (table)
                    {
                        ImGui.TableSetupColumn($"{GetLoc("Name")}",                                              ImGuiTableColumnFlags.WidthStretch, 30);
                        ImGui.TableSetupColumn($"{GetLoc("OccultCrescentHelper-CEManager-CEHistory-LastTime")}", ImGuiTableColumnFlags.WidthStretch, 20);

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
                            if (ModuleConfig.CEHistory.TryGetValue(GetIslandID(), out var history) &&
                                history.TryGetValue(ceID, out var time))
                            {
                                var dateTime = UnixSecondToDateTime(time);
                                ImGui.Text($"{dateTime.TimeAgo()}\t\t\t({dateTime:MM/dd HH:mm:ss})");
                            }
                            else
                                ImGui.Text("-");
                        }
                    }
                }
                
                ImGui.TextWrapped(GetLoc("OccultCrescentHelper-CEManager-CEHistory-Notify"));
            }
            
            ImGui.NewLine();


            if (ImGui.Checkbox($"{GetLoc("OccultCrescentHelper-PrioritizeMoveTo")}", ref ModuleConfig.IsEnabledMoveToEvent))
                ModuleConfig.Save(MainModule);
            ImGuiOm.HelpMarker(GetLoc("OccultCrescentHelper-CEManager-PrioritizeMoveTo-Help"), 20f * GlobalFontScale);

            if (ModuleConfig.IsEnabledMoveToEvent)
            {
                ImGui.SetNextItemWidth(150f * GlobalFontScale);
                ImGui.SliderFloat($"{GetLoc("OccultCrescentHelper-CEManager-PrioritizeMoveTo-LeftTime")}", ref ModuleConfig.LeftTimeMoveToEvent, 1f, 180f, "%.1f");
                if (ImGui.IsItemDeactivatedAfterEdit())
                    ModuleConfig.Save(MainModule);
                ImGuiOm.HelpMarker($"{GetLoc("OccultCrescentHelper-CEManager-PrioritizeMoveTo-LeftTime-Help")}", 20f * GlobalFontScale);
            }

            ImGui.NewLine();

            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), GetLoc("OccultCrescentHelper-CEManager-NotifyEventAppears"));
            ImGuiOm.HelpMarker(GetLoc("OccultCrescentHelper-CEManager-NotifyEventAppears-Help"), 20f * GlobalFontScale);

            ImGui.SameLine(0, 8f * GlobalFontScale);
            if (ImGui.Checkbox("###NotifyEventAppears", ref ModuleConfig.IsEnabledNotifyEvents))
                ModuleConfig.Save(MainModule);

            if (ModuleConfig.IsEnabledNotifyEvents)
            {
                using (ImRaii.PushIndent())
                {
                    var counter = 0;
                    foreach (var (type, isEnabled) in ModuleConfig.IsEnabledNotifyEventsCategoried)
                    {
                        using var isEnabledNotifyEventsDataID = ImRaii.PushId($"{type}");

                        using (ImRaii.Group())
                        {
                            var isEnabledCopy = isEnabled;
                            if (ImGui.Checkbox($"{CrescentEvent.GetEventTypeName(type)}##{type}", ref isEnabledCopy))
                            {
                                ModuleConfig.IsEnabledNotifyEventsCategoried[type] = isEnabledCopy;
                                ModuleConfig.Save(MainModule);
                            }
                        }

                        if (counter != 7 && counter != 11 && counter != ModuleConfig.IsEnabledNotifyEventsCategoried.Count - 1)
                            ImGui.SameLine(0, 4f * GlobalFontScale);
                        counter++;
                    }
                }
            }

            ImGui.NewLine();

            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), GetLoc("OccultCrescentHelper-CEManager-NotifyCEStarts"));
            ImGuiOm.HelpMarker(GetLoc("OccultCrescentHelper-CEManager-NotifyCEStarts-Help"), 20f * GlobalFontScale);

            ImGui.SameLine(0, 8f * GlobalFontScale);
            if (ImGui.Checkbox("###NotifyCEStarts", ref ModuleConfig.IsEnabledNotifyCEStarts))
                ModuleConfig.Save(MainModule);
            
            ImGui.NewLine();
            
            ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), GetLoc("Command"));

            using (ImRaii.PushIndent())
            {
                ImGui.Text($"/pdr {CommandFate} {GetLoc("OccultCrescentHelper-Command-PFate-Help")}");

                ImGui.Text($"/pdr {CommandCE} {GetLoc("OccultCrescentHelper-Command-PCE-Help")}");
            }
        }
        
        private static void OnLogout() =>
            CETaskHelper.Abort();

        private static void OnZoneChanged(ushort obj)
        {
            AllIslandEvents.Clear();
            KnownCENames.Clear();
            CETaskHelper?.Abort();
        }

        public override void OnUpdate()
        {
            var publicInstance = PublicContentOccultCrescent.GetInstance();
            if (publicInstance == null) return;

            var islandID = GetIslandID();
            ModuleConfig.CEHistory.TryAdd(islandID, []);

            var currentCENames = new HashSet<string>();
            var newCEData      = new List<IslandEventData>();

            // FATE
            foreach (var fate in DService.Fate)
            {
                if (IslandEventData.Parse(fate) is not { } safeFate) continue;

                newCEData.Add(safeFate);
                currentCENames.Add(safeFate.Event.Name);

                if (AllIslandEvents.TryGetValue(safeFate, out var existed))
                    existed.Update(fate);
                else
                    AllIslandEvents.Add(safeFate);

                if (KnownCENames.Add(safeFate.Event.Name))
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
                
                if (AllIslandEvents.TryGetValue(safeCE, out var existed))
                    existed.Update(dynamicEvent);
                else
                    AllIslandEvents.Add(safeCE);

                if (KnownCENames.Add(safeCE.Event.Name))
                    NotifyNewCE(safeCE);

                // 因为从刷新到正式开始时间为 3 分钟
                ModuleConfig.CEHistory[islandID][safeCE.Event.DataID] = safeCE.Event.CEStartTime - 180;
            }

            KnownCENames.IntersectWith(currentCENames);
            AllIslandEvents.IntersectWith(newCEData);
            
            if (Throttler.Throttle("OccultCrescentHelper-CEManager-OnUpdate-SaveCEHistory", 10_000))
                ModuleConfig.Save(MainModule);
        }

        private void OnPostReceivedCommand(ExecuteCommandFlag command, uint param1, uint param2, uint param3, uint param4)
        {
            if (command                        != ExecuteCommandFlag.FateLoad ||
                GameState.TerritoryIntendedUse != TerritoryIntendedUse.OccultCrescent)
                return;

            OnUpdate();
        }

        // CE 开始
        private static void OnPostReceivedMessage(uint logMessageID)
        {
            if (logMessageID                   != 11002                               ||
                GameState.TerritoryIntendedUse != TerritoryIntendedUse.OccultCrescent ||
                !ModuleConfig.IsEnabledNotifyCEStarts)
                return;

            CETaskHelper.Abort();

            var message = GetLoc("OccultCrescentHelper-CEManager-Notification-CEStart");
            NotificationInfo(message);
            Speak(message);
        }

        private static void OnClickTeleport(uint id, SeString message)
        {
            if (AllIslandEvents.FirstOrDefault(x => x.LinkPayloadID == id) is not { } ce) return;
            TeleportToCE(ce);
        }
        
        private static void OnCommandFate(string command, string args)
        {
            if (GameState.TerritoryIntendedUse != TerritoryIntendedUse.OccultCrescent) return;

            args = args.Trim().ToLowerInvariant();
            if (args == "abort")
            {
                CETaskHelper.Abort();
                vnavmeshIPC.PathStop();
                return;
            }
            
            var fate = AllIslandEvents.Where(x => x.Event is { Type: CrescentEventType.FATE, Progress: < 80 }).OrderBy(x => x.Event.Progress).FirstOrDefault();
            if (fate == null) return;

            TeleportToCE(fate);
        }
        
        private static void OnCommandCE(string command, string args)
        {
            if (GameState.TerritoryIntendedUse != TerritoryIntendedUse.OccultCrescent) return;

            args = args.Trim().ToLowerInvariant();
            if (args == "abort")
            {
                CETaskHelper.Abort();
                vnavmeshIPC.PathStop();
                return;
            }
            
            var ce = AllIslandEvents.FirstOrDefault(x => x.Event is { Type: CrescentEventType.CE, CEState: DynamicEventState.Register, CELeftTimeSecond: > 15 });
            if (ce == null) return;

            TeleportToCE(ce);
        }

        private static void TeleportToCE(IslandEventData data)
        {
            if (DService.ObjectTable.LocalPlayer is null) return;

            // 不在开始前状态, 禁止 TP 过去, 太危险了
            if (data.Event.Type == CrescentEventType.CE && data.Event.CEState != DynamicEventState.Register)
                return;

            // 没开绿玩移动或时间不够了
            if (!ModuleConfig.IsEnabledMoveToEvent ||
                (data.Event.Type == CrescentEventType.CE && data.Event.CELeftTimeSecond < ModuleConfig.LeftTimeMoveToEvent))
            {
                CETaskHelper.Abort();

                TP(data.Event.GetRandomPointNearEdge() + new Vector3(0, 1, 0), CETaskHelper);
                return;
            }

            // 先跑去使用以太之光
            if (CrescentAetheryte.TryGetNearestSouthHorn(data.Event.Position, out var aetheryte))
            {
                // 进化的毒鸟——高等魔鸟
                if (data.Event.DataID == 1967)
                    aetheryte = CrescentAetheryte.CrystallizedCaverns;

                CETaskHelper.Abort();
                CETaskHelper.Enqueue(() => AetheryteManager.UseAetheryte(aetheryte));

                CETaskHelper.DelayNext(1000);
                CETaskHelper.Enqueue(() => !AetheryteManager.IsTaskHelperBusy);
            }

            CETaskHelper.Enqueue(() =>
            {
                if (OccupiedInEvent) return false;
                if (DService.Condition[ConditionFlag.Mounted]) return true;
                return UseActionManager.UseAction(ActionType.GeneralAction, 9);
            });

            CETaskHelper.Enqueue(() =>
            {
                if (!Throttler.Throttle("OccultCrescentHelper-CEManager-MoveTo")) return false;
                if (vnavmeshIPC.PathIsRunning()) return true;

                vnavmeshIPC.PathfindAndMoveTo(data.Event.GetRandomPointNearEdge(), false);
                return false;
            });

            CETaskHelper.Enqueue(() =>
            {
                if (!Throttler.Throttle("OccultCrescentHelper-CEManager-WaitMoveTo")) return false;

                // CE / FATE 寄了
                if (AllIslandEvents.FirstOrDefault(x => x == data) is null)
                {
                    CETaskHelper.Abort();
                    return true;
                }

                if (!vnavmeshIPC.PathIsRunning() ||
                    (data.Event.Type is CrescentEventType.FATE or CrescentEventType.MagicPot &&
                     FateManager.Instance()->CurrentFate         != null                     &&
                     FateManager.Instance()->CurrentFate->FateId == data.Event.DataID))
                {
                    ExecuteCommandManager.ExecuteCommand(ExecuteCommandFlag.Dismount);
                    vnavmeshIPC.PathStop();
                    return true;
                }

                return false;
            });

            if (data.Event.Type is CrescentEventType.FATE or CrescentEventType.MagicPot)
            {
                CETaskHelper.Enqueue(() =>
                {
                    if (DService.Condition[ConditionFlag.Mounted]) return false;

                    ChatManager.SendMessage("/tenemy");
                    return true;
                });
                CETaskHelper.DelayNext(100);
                CETaskHelper.Enqueue(() => ChatManager.SendMessage("/facetarget"));
                CETaskHelper.DelayNext(100);
                CETaskHelper.Enqueue(() => ChatManager.SendMessage("/automove on"));
            }
            else if (data.Event.Type is CrescentEventType.CE)
            {
                if (Random.Shared.NextDouble() >= 0.6)
                {
                    CETaskHelper.DelayNext(Random.Shared.Next(500, 3000));
                    CETaskHelper.Enqueue(() =>
                    {
                        if (DService.Condition[ConditionFlag.Mounted]) return false;

                        vnavmeshIPC.PathfindAndMoveTo(data.Event.GetRandomPointNearEdge(), false);
                        return true;
                    });

                    CETaskHelper.DelayNext(Random.Shared.Next(500, 3000));
                    CETaskHelper.Enqueue(() =>
                    {
                        if (DService.Condition[ConditionFlag.Mounted] || vnavmeshIPC.PathIsRunning()) return false;

                        vnavmeshIPC.PathfindAndMoveTo(data.Event.Position, false);
                        return true;
                    });

                    CETaskHelper.DelayNext(200);
                    CETaskHelper.Enqueue(vnavmeshIPC.PathStop);
                }
            }
        }

        private void NotifyNewCE(IslandEventData ce)
        {
            if (!ModuleConfig.IsEnabledNotifyEvents || !ModuleConfig.IsEnabledNotifyEventsCategoried.GetValueOrDefault(ce.Event.Type, false)) return;

            var ceName   = ce.Event.NameDisplay;
            var position = ce.Event.Position;

            var mapPos      = WorldToMap(position.ToVector2(), GameState.MapData);
            var linkPayload = ce.GetOrAddLinkPayload();

            var message = new SeStringBuilder()
                          .AddUiForeground(25)
                          .AddText($"[{MainModule.Info.Title}] ")
                          .AddUiForegroundOff()
                          .AddText($"{ce.GetNotificationTitle()}")
                          .Add(NewLinePayload.Payload)
                          .AddText($"{GetLoc("Name")}: ")
                          .AddUiForeground(45)
                          .AddText(ceName)
                          .AddUiForegroundOff()
                          .Add(NewLinePayload.Payload)
                          .AddText($"{GetLoc("Position")}: ")
                          .Append(SeString.CreateMapLink(GameState.TerritoryTypeData.ExtractPlaceName(), mapPos.X, mapPos.Y));

            if (ce.Event.DemiatmaID != 0)
                message.Add(NewLinePayload.Payload)
                       .AddText($"{GetLoc("Item")}: ")
                       .AddItemLink(ce.Event.DemiatmaID, false)
                       .AddText($" ({LuminaWrapper.GetAddonText(358)}: {LocalPlayerState.GetItemCount(ce.Event.DemiatmaID)})");

            if (ce.Event.SpecialRewards is { Count: > 0 } specialRewards)
            {
                var prefix = GetLoc("OccultCrescentHelper-CEManager-SpecialRewards");
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
                message.Add(NewLinePayload.Payload)
                       .AddText($"{GetLoc("Operation")}: ")
                       .Add(RawPayload.LinkTerminator)
                       .Add(linkPayload)
                       .AddText("[")
                       .AddIcon(BitmapFontIcon.Aethernet)
                       .AddUiForeground(35)
                       .AddText($"{GetLoc("Teleport")}")
                       .AddUiForegroundOff()
                       .AddText("]")
                       .Add(RawPayload.LinkTerminator);

            Chat(message.Build());

            NotificationInfo($"{ceName}", $"{ce.GetNotificationTitle()}");
            Speak($"{ce.GetNotificationTitle()}");
        }

        public class IslandEventData(uint dataID) : IEquatable<IslandEventData>
        {
            public CrescentEvent Event { get; } = new(dataID);

            public int                LinkPayloadID { get; private set; } = -1;
            public DalamudLinkPayload LinkPayload   { get; private set; }

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

                var name = ce.Name.ExtractText();
                if (data.RowId != 48) // 两歧塔 力之塔
                {
                    name = ce.State switch
                    {
                        DynamicEventState.Battle   => $"{ce.Name} ({GetLoc("OccultCrescentHelper-CEManager-CEName-InBattle", ce.Participants, ce.Progress)})",
                        DynamicEventState.Register => $"{ce.Name} ({GetLoc("OccultCrescentHelper-CEManager-CEName-Register", leftTime)})",
                        DynamicEventState.Warmup   => $"{ce.Name} ({GetLoc("OccultCrescentHelper-CEManager-CEName-WarmUp")})",
                        _                          => $"{ce.Name}"
                    };
                }

                if (string.IsNullOrEmpty(name)) return null;

                var returnValue = new IslandEventData(data.RowId);
                returnValue.Event.UpdateTempDataCE(name, 
                                                   ce.Progress,
                                                   ce.State, 
                                                   ce.State == DynamicEventState.Register ? ce.StartTimestamp : ce.StartTimestamp - 1200, 
                                                   leftTime);
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

                var name = ce.Name.ExtractText();
                if (data.RowId != 48)
                {
                    name = ce.State switch
                    {
                        DynamicEventState.Battle   => $"{ce.Name} ({GetLoc("OccultCrescentHelper-CEManager-CEName-InBattle", ce.Participants, ce.Progress)})",
                        DynamicEventState.Register => $"{ce.Name} ({GetLoc("OccultCrescentHelper-CEManager-CEName-Register", leftTime)})",
                        DynamicEventState.Warmup   => $"{ce.Name} ({GetLoc("OccultCrescentHelper-CEManager-CEName-WarmUp")})",
                        _                          => $"{ce.Name}"
                    };
                }

                Event.UpdateTempDataCE(name, 
                                       ce.Progress,
                                       ce.State, 
                                       ce.State == DynamicEventState.Register ? ce.StartTimestamp : ce.StartTimestamp - 1200, 
                                       leftTime);
            }

            public string GetNotificationTitle() => Event.Type switch
            {
                CrescentEventType.FATE      => GetLoc("OccultCrescentHelper-CEManager-Notification-FATE"),
                CrescentEventType.MagicPot  => GetLoc("OccultCrescentHelper-CEManager-Notification-MagicPot"),
                CrescentEventType.CE        => GetLoc("OccultCrescentHelper-CEManager-Notification-CE"),
                CrescentEventType.ForkTower => GetLoc("OccultCrescentHelper-CEManager-Notification-ForkTower"),
                _                           => GetLoc("OccultCrescentHelper-CEManager-Notification-FATE")
            };

            public DalamudLinkPayload GetOrAddLinkPayload()
            {
                if (LinkPayloadID != -1) return LinkPayload;

                LinkPayload   = LinkPayloadManager.Register(OnClickTeleport, out var id);
                LinkPayloadID = (int)id;

                return LinkPayload;
            }

            public bool Equals(IslandEventData? other)
            {
                if (other is null) return false;
                if (ReferenceEquals(this, other)) return true;

                return Event == other.Event;
            }

            public override bool Equals(object? obj) => Equals(obj as IslandEventData);

            public override int GetHashCode() => HashCode.Combine(Event);

            public static bool operator ==(IslandEventData? left, IslandEventData? right) => Equals(left, right);

            public static bool operator !=(IslandEventData? left, IslandEventData? right) => !Equals(left, right);
        }
    }
}
