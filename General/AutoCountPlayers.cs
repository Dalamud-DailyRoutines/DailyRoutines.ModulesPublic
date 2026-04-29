using System.Numerics;
using DailyRoutines.Common.Info.Models;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using DailyRoutines.Manager;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Gui.Dtr;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Hooking;
using Dalamud.Interface.Utility;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using Lumina.Excel.Sheets;
using OmenTools.Interop.Game.Helpers;
using OmenTools.Interop.Game.Lumina;
using OmenTools.OmenService;
using OmenTools.Threading;
using Control = FFXIVClientStructs.FFXIV.Client.Game.Control.Control;
using TerritoryIntendedUse = FFXIVClientStructs.FFXIV.Client.Enums.TerritoryIntendedUse;

namespace DailyRoutines.ModulesPublic;

public unsafe class AutoCountPlayers : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoCountPlayersTitle"),
        Description = Lang.Get("AutoCountPlayersDescription"),
        Category    = ModuleCategory.General
    };

    public override ModulePermission Permission { get; } = new() { AllDefaultEnabled = true };
    
    private delegate void                                InfoProxy24EndRequestDelegate(InfoProxy24* instance);
    private          Hook<InfoProxy24EndRequestDelegate> InfoProxy24EndRequestHook;

    private Config        config = null!;
    private IDtrBarEntry? entry;

    private readonly Dictionary<uint, byte[]>              jobIcons          = [];
    private readonly Dictionary<uint, PlayerTargetingInfo> lastTargetingData = [];

    private string searchInput = string.Empty;

    protected override void Init()
    {
        config = Config.Load(this) ?? new();

        entry         ??= DService.Instance().DTRBar.Get("DailyRoutines-AutoCountPlayers");
        entry.Shown   =   true;
        entry.Text    =   $"{Lang.Get("AutoCountPlayers-PlayersAroundCount")}: 0";
        entry.OnClick =  _ =>
        {
            EnsureOverlay();
            Overlay.IsOpen ^= true;
        };

        WindowManager.Instance().PostDraw += OnDraw;

        PlayersManager.Instance().ReceivePlayersAround      += OnReceivePlayers;
        PlayersManager.Instance().ReceivePlayersTargetingMe += OnPlayersTargetingMeUpdate;

        var instance = (InfoProxy24*)InfoModule.Instance()->GetInfoProxyById((InfoProxyId)24);
        InfoProxy24EndRequestHook ??= instance->VirtualTable->HookVFuncFromName
        (
            "EndRequest",
            (InfoProxy24EndRequestDelegate)InfoProxy24EndRequestDetour
        );
        InfoProxy24EndRequestHook.Enable();

        DService.Instance().ClientState.TerritoryChanged += OnZoneChanged;
        FrameworkManager.Instance().Reg(OnUpdate, 10_000);
        OnUpdate(DService.Instance().Framework);
    }

    protected override void Uninit()
    {
        DService.Instance().ClientState.TerritoryChanged -= OnZoneChanged;

        FrameworkManager.Instance().Unreg(OnUpdate);

        WindowManager.Instance().PostDraw                       -= OnDraw;
        PlayersManager.Instance().ReceivePlayersAround      -= OnReceivePlayers;
        PlayersManager.Instance().ReceivePlayersTargetingMe -= OnPlayersTargetingMeUpdate;

        foreach (var info in lastTargetingData.Values)
        {
            var duration = DateTime.Now - info.TargetingStartTime;
            config.TargetingHistories.Add
            (
                new()
                {
                    Name        = info.Player.Name.ToString(),
                    HomeWorldID = info.Player.HomeWorld.RowId,
                    JobID       = info.Player.ClassJob.RowId,
                    StartTime   = info.TargetingStartTime,
                    Duration    = duration,
                    ZoneID      = GameState.TerritoryType
                }
            );
        }

        if (lastTargetingData.Count > 0)
        {
            lastTargetingData.Clear();
            if (config.TargetingHistories.Count > 100)
                config.TargetingHistories.RemoveRange(0, config.TargetingHistories.Count - 100);
            config.Save(this);
        }

        entry?.Remove();
        entry = null;
    }

    protected override void ConfigUI()
    {
        ImGui.SetNextItemWidth(120f * GlobalUIScale);
        if (ImGui.InputFloat(Lang.Get("Scale"), ref config.ScaleFactor, 0, 0, "%.1f"))
            config.ScaleFactor = Math.Max(0.1f, config.ScaleFactor);
        if (ImGui.IsItemDeactivatedAfterEdit())
            config.Save(this);

        ImGui.NewLine();

        if (ImGui.Checkbox(Lang.Get("AutoCountPlayers-DisplayLineWhenTargetingMe"), ref config.DisplayLineWhenTargetingMe))
            config.Save(this);

        if (config.DisplayLineWhenTargetingMe)
        {
            using (ImRaii.PushIndent())
            {
                if (ImGui.Checkbox(Lang.Get("SendChat"), ref config.SendChat))
                    config.Save(this);

                if (ImGui.Checkbox(Lang.Get("SendNotification"), ref config.SendNotification))
                    config.Save(this);

                if (ImGui.Checkbox(Lang.Get("SendTTS"), ref config.SendTTS))
                    config.Save(this);

                if (config.SendNotification || config.SendTTS)
                {
                    using (ImRaii.PushIndent())
                    {
                        if (ImGui.Checkbox(Lang.Get("AutoCountPlayers-FilterFriend"), ref config.FilterFriend))
                            config.Save(this);
                    }
                }
            }
        }


    }

    protected override void OverlayUI()
    {
        using var tabBar = ImRaii.TabBar("##Tab");
        if (!tabBar) return;

        using (var item = ImRaii.TabItem(Lang.Get("AutoCountPlayers-PlayersAround")))
        {
            if (item)
            {
                ImGui.SetNextItemWidth(-1f);
                ImGui.InputText("###Search", ref searchInput, 128);

                if (DService.Instance().Condition.IsBetweenAreas) return;

                using var child = ImRaii.Child("列表", ImGui.GetContentRegionAvail() - ImGui.GetStyle().ItemSpacing, true);
                if (!child) return;

                foreach (var playerAround in PlayersManager.Instance().PlayersAround)
                {
                    using var id = ImRaii.PushId($"{playerAround.GameObjectID}");

                    if (!string.IsNullOrWhiteSpace(searchInput) && !playerAround.Name.ToString().Contains(searchInput)) continue;

                    if (ImGuiOm.ButtonIcon("定位", FontAwesomeIcon.Flag, Lang.Get("Locate")))
                    {
                        var mapPos = PositionHelper.WorldToMap(playerAround.Position.ToVector2(), GameState.MapData);
                        var message = new SeStringBuilder()
                                      .Add
                                      (
                                          new PlayerPayload
                                          (
                                              playerAround.Name.TextValue,
                                              playerAround.ToStruct()->HomeWorld
                                          )
                                      )
                                      .Append(" (")
                                      .AddIcon(playerAround.ClassJob.Value.ToBitmapFontIcon())
                                      .Append($" {playerAround.ClassJob.Value.Name})")
                                      .Add(new NewLinePayload())
                                      .Append("     ")
                                      .Append(SeString.CreateMapLink(GameState.TerritoryType, GameState.Map, mapPos.X, mapPos.Y))
                                      .Build();
                        NotifyHelper.Instance().Chat(message);
                    }

                    if (DService.Instance().GameGUI.WorldToScreen(playerAround.Position,            out var screenPos) &&
                        DService.Instance().GameGUI.WorldToScreen(LocalPlayerState.Object.Position, out var localScreenPos))
                    {
                        if (!ImGui.IsAnyItemHovered() || ImGui.IsItemHovered())
                            DrawLine(localScreenPos, screenPos, playerAround);
                    }


                    ImGui.SameLine();
                    ImGui.TextUnformatted($"{playerAround.Name} ({playerAround.ClassJob.Value.Name})");
                }
            }
        }

        using (var item = ImRaii.TabItem(Lang.Get("AutoCountPlayers-TargetedHistory")))
        {
            if (item)
            {
                foreach (var record in config.TargetingHistories.AsEnumerable().Reverse())
                {
                    ImGui.TextDisabled($"{record.StartTime:MM/dd HH:mm:ss}");

                    ImGui.SameLine();
                    var jobIcon = jobIcons.GetOrAdd
                    (
                        record.JobID,
                        _ => new SeStringBuilder().AddIcon(record.JobID.ToLuminaRowRef<ClassJob>().Value.ToBitmapFontIcon()).Encode()
                    );
                    ImGuiHelpers.SeStringWrapped(jobIcon);

                    ImGui.SameLine();
                    ImGui.TextUnformatted($"{record.Name}@{LuminaWrapper.GetWorldName(record.HomeWorldID)}");

                    ImGui.SameLine();
                    ImGui.TextColored(KnownColor.Orange.ToVector4(), $"[{record.Duration:mm\\:ss}]");

                    ImGui.SameLine();
                    ImGui.TextDisabled($"({LuminaWrapper.GetZonePlaceName(record.ZoneID)})");
                }
            }
        }
    }

    private void OnDraw()
    {
        if (!config.DisplayLineWhenTargetingMe || PlayersManager.Instance().PlayersTargetingMe.Count == 0) return;

        if (!GameState.IsForeground) return;

        var localPlayer = Control.GetLocalPlayer();
        if (localPlayer == null) return;

        if (NamePlate->IsAddonAndNodesReady())
        {
            var node = NamePlate->GetNodeById(1);

            if (node != null)
            {
                var nodeState = node->GetNodeState();

                if (ImGui.Begin($"AutoCountPlayers-{localPlayer->EntityId}", WINDOW_FLAGS))
                {
                    ImGui.SetWindowPos(nodeState.Center - ImGui.GetWindowSize() * 0.75f);

                    using (FontManager.Instance().UIFont140.Push())
                    using (ImRaii.Group())
                    {
                        ImGuiHelpers.SeStringWrapped(new SeStringBuilder().AddIcon(BitmapFontIcon.Warning).Encode());

                        ImGui.SameLine();
                        ImGui.SetCursorPosY(ImGui.GetCursorPosY() - 1.2f * GlobalUIScale);
                        ImGuiOm.TextOutlined(KnownColor.Orange.ToVector4(), $"{PlayersManager.Instance().PlayersTargetingMe.Count}", KnownColor.SaddleBrown.ToVector4());

                        if (GameState.ContentFinderCondition == 0)
                        {
                            using (FontManager.Instance().UIFont80.Push())
                            {
                                var text = Lang.Get("AutoCountPlayers-Notification-SomeoneTargetingMe");
                                ImGuiOm.TextOutlined
                                (
                                    ImGui.GetCursorScreenPos() - new Vector2(ImGui.CalcTextSize(text).X * 0.3f, 0),
                                    KnownColor.Orange.ToVector4().ToUInt(),
                                    $"({text})",
                                    KnownColor.SaddleBrown.ToVector4().ToUInt()
                                );
                            }
                        }
                    }

                    ImGui.End();
                }
            }
        }

        var currentWindowSize = ImGui.GetMainViewport().Size;
        if (!DService.Instance().GameGUI.WorldToScreen(localPlayer->Position, out var localScreenPos))
            localScreenPos = currentWindowSize with { X = currentWindowSize.X / 2 };

        foreach (var playerInfo in PlayersManager.Instance().PlayersTargetingMe)
        {
            if (DService.Instance().GameGUI.WorldToScreen(playerInfo.Player.Position, out var screenPos))
                DrawLine(localScreenPos, screenPos, playerInfo.Player, LineColorRed, $" [{TimeSpan.FromSeconds(playerInfo.TargetingDurationSeconds)}]");
        }
    }

    private static void OnZoneChanged(uint u)
    {
        FrameworkManager.Instance().Unreg(OnUpdate);

        if (GameState.TerritoryIntendedUse != TerritoryIntendedUse.OccultCrescent ||
            AgentModule.Instance()->GetAgentByInternalId(AgentId.ContentMemberList)->IsAgentActive())
            return;

        FrameworkManager.Instance().Reg(OnUpdate, 30_000);
    }

    private static void OnUpdate(IFramework framework)
    {
        if (GameState.TerritoryIntendedUse != TerritoryIntendedUse.OccultCrescent ||
            AgentModule.Instance()->GetAgentByInternalId(AgentId.ContentMemberList)->IsAgentActive())
        {
            FrameworkManager.Instance().Unreg(OnUpdate);
            return;
        }

        var proxy = (InfoProxy24*)InfoModule.Instance()->GetInfoProxyById((InfoProxyId)24);
        if (proxy == null) return;

        AgentId.ContentMemberList.SendEvent(0, 1);
    }

    private void OnReceivePlayers(IReadOnlyList<IPlayerCharacter> characters)
    {
        if (entry == null) return;

        // 新月岛
        if (GameState.TerritoryIntendedUse == TerritoryIntendedUse.OccultCrescent)
            entry.Shown = true;
        else
            entry.Shown = !DService.Instance().Condition[ConditionFlag.InCombat] || GameState.IsInPVPArea;

        if (!entry.Shown)
        {
            EnsureOverlay();
            Overlay.IsOpen = false;
            return;
        }

        entry.Text = $"{Lang.Get("AutoCountPlayers-PlayersAroundCount")}: {PlayersManager.Instance().PlayersAroundCount}" +
                     (PlayersManager.Instance().PlayersTargetingMe.Count == 0 ? string.Empty : $" ({PlayersManager.Instance().PlayersTargetingMe.Count})");

        // 新月岛
        if (GameState.TerritoryIntendedUse == TerritoryIntendedUse.OccultCrescent)
        {
            entry.Text.Append
            (
                $" / {Lang.Get("AutoCountPlayers-PlayersZoneCount")}: " +
                $"{((InfoProxy24*)InfoModule.Instance()->GetInfoProxyById((InfoProxyId)24))->EntryCount}"
            );
        }

        if (characters.Count == 0)
        {
            entry.Tooltip = string.Empty;
            return;
        }

        var tooltip = new SeStringBuilder();

        if (PlayersManager.Instance().PlayersTargetingMe.Count > 0)
        {
            tooltip.AddUiForeground(32)
                   .AddText($"{Lang.Get("AutoCountPlayers-PlayersTargetingMe")}")
                   .AddUiForegroundOff()
                   .Add(NewLinePayload.Payload);

            PlayersManager.Instance().PlayersTargetingMe.ForEach
            (info =>
                 tooltip.AddText($"{info.Player.Name} (")
                        .AddIcon(info.Player.ClassJob.Value.ToBitmapFontIcon())
                        .AddText($"{info.Player.ClassJob.Value.Name.ToString()})")
                        .Add(NewLinePayload.Payload)
            );
        }

        tooltip.AddUiForeground(32)
               .AddText($"{Lang.Get("AutoCountPlayers-PlayersAroundInfo")}")
               .AddUiForegroundOff()
               .Add(NewLinePayload.Payload);

        characters.ForEach
        (info => tooltip.AddText($"{info.Name} (")
                        .AddIcon(info.ClassJob.Value.ToBitmapFontIcon())
                        .AddText($"{info.ClassJob.Value.Name.ToString()})")
                        .Add(NewLinePayload.Payload)
        );

        var message = tooltip.Build();
        if (message.Payloads.Last() is NewLinePayload)
            message.Payloads.RemoveAt(message.Payloads.Count - 1);

        entry.Tooltip = message;
    }

    private void OnPlayersTargetingMeUpdate(IReadOnlyList<PlayerTargetingInfo> targetingPlayersInfo)
    {
        var currentIds     = targetingPlayersInfo.Select(x => x.Player.EntityID).ToHashSet();
        var endedTargeting = lastTargetingData.Where(x => !currentIds.Contains(x.Key)).ToList();

        if (endedTargeting.Count > 0)
        {
            foreach (var (key, info) in endedTargeting)
            {
                var duration = DateTime.Now - info.TargetingStartTime;

                config.TargetingHistories.Add
                (
                    new()
                    {
                        Name        = info.Player.Name.ToString(),
                        HomeWorldID = info.Player.HomeWorld.RowId,
                        JobID       = info.Player.ClassJob.RowId,
                        StartTime   = info.TargetingStartTime,
                        Duration    = duration,
                        ZoneID      = GameState.TerritoryType
                    }
                );

                lastTargetingData.Remove(key);
            }

            if (config.TargetingHistories.Count > 100)
                config.TargetingHistories.RemoveRange(0, config.TargetingHistories.Count - 100);

            config.Save(this);
        }

        foreach (var info in targetingPlayersInfo)
            lastTargetingData[info.Player.EntityID] = info;

        if (targetingPlayersInfo.Count > 0 &&
            (GameState.ContentFinderCondition == 0 || DService.Instance().PartyList.Length < 2))
        {
            var newTargetingPlayers = targetingPlayersInfo.Where(info => info.IsNew).ToList();

            if (newTargetingPlayers.Any(info => Throttler.Shared.Throttle($"AutoCountPlayers-Player-{info.Player.EntityID}", 30_000)))
            {
                if (config.SendTTS)
                {
                    if (!config.FilterFriend || targetingPlayersInfo.All(x => !x.Player.ToStruct()->IsFriend))
                        NotifyHelper.Speak(Lang.Get("AutoCountPlayers-Notification-SomeoneTargetingMe"));
                }

                if (config.SendNotification)
                {
                    if (!config.FilterFriend || targetingPlayersInfo.All(x => !x.Player.ToStruct()->IsFriend))
                        NotifyHelper.Instance().NotificationWarning(Lang.Get("AutoCountPlayers-Notification-SomeoneTargetingMe"));
                }

                if (config.SendChat)
                {
                    var builder = new SeStringBuilder();

                    builder.Append($"{Lang.Get("AutoCountPlayers-Notification-SomeoneTargetingMe")}:");
                    builder.Add(new NewLinePayload());

                    foreach (var info in targetingPlayersInfo)
                    {
                        builder.Add(new PlayerPayload(info.Player.Name.ToString(), info.Player.HomeWorld.RowId))
                               .Append(" (")
                               .AddIcon(info.Player.ClassJob.Value.ToBitmapFontIcon())
                               .Append($" {info.Player.ClassJob.Value.Name})");
                        builder.Add(new NewLinePayload());
                    }

                    var message = builder.Build();
                    if (message.Payloads.Last() is NewLinePayload)
                        message.Payloads.RemoveAt(message.Payloads.Count - 1);

                    NotifyHelper.Instance().Chat(builder.Build());
                }
            }
        }
    }

    private void InfoProxy24EndRequestDetour(InfoProxy24* proxy)
    {
        InfoProxy24EndRequestHook.Original(proxy);
        OnReceivePlayers(PlayersManager.Instance().PlayersAround);
    }

    private void DrawLine(Vector2 startPos, Vector2 endPos, ICharacter chara, uint lineColor = 0, string? extraInfo = null)
    {
        lineColor = lineColor == 0 ? LineColorBlue : lineColor;

        var drawList = ImGui.GetForegroundDrawList();

        drawList.AddLine(startPos, endPos, lineColor, 8f);
        drawList.AddCircleFilled(startPos, 12f, DotColor);
        drawList.AddCircleFilled(endPos,   12f, DotColor);

        ImGui.SetNextWindowPos(endPos);

        if (ImGui.Begin($"AutoCountPlayers-{chara.EntityID}", WINDOW_FLAGS))
        {
            using (ImRaii.Group())
            {
                ImGuiOm.ScaledDummy(12f);

                var icon = jobIcons.GetOrAdd
                (
                    chara.ClassJob.RowId,
                    _ => new SeStringBuilder().AddIcon(chara.ClassJob.Value.ToBitmapFontIcon()).Encode()
                );
                ImGui.SameLine();
                ImGuiHelpers.SeStringWrapped(icon);

                ImGui.SameLine();
                ImGuiOm.TextOutlined(KnownColor.Orange.ToVector4(), $"{chara.Name}" + (extraInfo ?? string.Empty));
            }

            ImGui.End();
        }
    }

    private void EnsureOverlay()
    {
        if (Overlay != null) return;
        
        Overlay            =  new(this);
        Overlay.Flags      &= ~ImGuiWindowFlags.NoTitleBar;
        Overlay.Flags      &= ~ImGuiWindowFlags.AlwaysAutoResize;
        Overlay.WindowName =  $"{Lang.Get("AutoCountPlayers-PlayersAroundInfo")}###AutoCountPlayers-Overlay";
    }
    
    private class Config : ModuleConfig
    {
        public bool DisplayLineWhenTargetingMe = true;

        public bool  FilterFriend;
        public float ScaleFactor = 1;

        public bool SendChat         = true;
        public bool SendNotification = true;
        public bool SendTTS          = true;

        public List<TargetingRecord> TargetingHistories = [];
    }

    private class TargetingRecord
    {
        public string   Name        { get; set; } = string.Empty;
        public uint     HomeWorldID { get; set; }
        public uint     JobID       { get; set; }
        public uint     ZoneID      { get; set; }
        public DateTime StartTime   { get; set; }
        public TimeSpan Duration    { get; set; }
    }
    
    #region 常量

    private const ImGuiWindowFlags WINDOW_FLAGS =
        ImGuiWindowFlags.NoScrollbar           |
        ImGuiWindowFlags.AlwaysAutoResize      |
        ImGuiWindowFlags.NoTitleBar            |
        ImGuiWindowFlags.NoBackground          |
        ImGuiWindowFlags.NoBringToFrontOnFocus |
        ImGuiWindowFlags.NoFocusOnAppearing    |
        ImGuiWindowFlags.NoNavFocus            |
        ImGuiWindowFlags.NoDocking             |
        ImGuiWindowFlags.NoMove                |
        ImGuiWindowFlags.NoResize              |
        ImGuiWindowFlags.NoScrollWithMouse     |
        ImGuiWindowFlags.NoInputs              |
        ImGuiWindowFlags.NoSavedSettings;
    
    private static readonly uint LineColorBlue = KnownColor.LightSkyBlue.ToUInt();
    private static readonly uint LineColorRed  = KnownColor.Red.ToUInt();
    private static readonly uint DotColor      = KnownColor.RoyalBlue.ToUInt();

    #endregion
}
