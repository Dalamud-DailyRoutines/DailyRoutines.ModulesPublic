using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.RegularExpressions;
using DailyRoutines.Abstracts;
using DailyRoutines.Managers;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using Lumina.Excel.Sheets;

namespace DailyRoutines.ModulesPublic;

public unsafe class AutoNotifySPPlayers : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("AutoNotifySPPlayersTitle"),
        Description = GetLoc("AutoNotifySPPlayersDescription"),
        Category    = ModuleCategories.Notice,
    };

    private static Config ModuleConfig = null!;

    private static readonly Dictionary<uint, OnlineStatus> OnlineStatuses =
        LuminaGetter.Get<OnlineStatus>()
                    .Where(x => x.RowId != 0 && x.RowId != 47)
                    .ToDictionary(x => x.RowId, x => x);
    
    private static readonly Throttler<ulong> ObjThrottler = new();

    private static HashSet<uint> SelectedOnlineStatus = [];
    private static HashSet<uint> SelectedZone         = [];

    private static string ZoneSearchInput         = string.Empty;
    private static string OnlineStatusSearchInput = string.Empty;

    private static string SelectName    = string.Empty;
    private static string SelectCommand = string.Empty;

    private static readonly Dictionary<ulong, long> NoticeTimeInfo = [];
    
    protected override void Init()
    {
        ModuleConfig = LoadConfig<Config>() ?? new();

        PlayersManager.ReceivePlayersAround += OnReceivePlayers;
    }

    protected override void Uninit() => 
        PlayersManager.ReceivePlayersAround -= OnReceivePlayers;

    protected override void ConfigUI()
    {
        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), $"{GetLoc("WorkTheory")}:");

        ImGui.SameLine();
        ImGui.Text(GetLoc("AutoNotifySPPlayers-WorkTheoryHelp"));

        ImGui.Spacing();

        RenderTableAddNewPreset();

        if (ModuleConfig.NotifiedPlayer.Count == 0) return;

        ImGui.Separator();
        ImGui.Spacing();

        RenderTablePreset();
    }

    private void RenderTableAddNewPreset()
    {
        var tableSize = new Vector2(ImGui.GetContentRegionAvail().X / 4 * 3, 0);
        using (var table = ImRaii.Table("###AddNewPresetTable", 2, ImGuiTableFlags.None, tableSize))
        {
            if (table)
            {
                ImGui.TableSetupColumn("Label", ImGuiTableColumnFlags.WidthStretch, 10);
                ImGui.TableSetupColumn("Content", ImGuiTableColumnFlags.WidthStretch, 60);

                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.Text($"{Lang.Get("Name")}:");

                ImGui.TableNextColumn();
                ImGui.SetNextItemWidth(-1f);
                ImGui.InputTextWithHint("###NameInput", Lang.Get("AutoNotifySPPlayers-NameInputHint"),
                                        ref SelectName, 64);

                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.Text($"{Lang.Get("OnlineStatus")}:");

                ImGui.TableNextColumn();
                ImGui.SetNextItemWidth(-1f);
                MultiSelectCombo(OnlineStatuses, ref SelectedOnlineStatus, ref OnlineStatusSearchInput,
                                 [new(GetLoc("OnlineStatus"), ImGuiTableColumnFlags.WidthStretch, 0)],
                                 [x => () =>
                                 {
                                     if (!DService.Texture.TryGetFromGameIcon(x.Icon, out var statusIcon)) return;
                                     using var id = ImRaii.PushId($"{x.Name.ExtractText()}_{x.RowId}");
                                     if (ImGuiOm.SelectableImageWithText(
                                             statusIcon.GetWrapOrEmpty().Handle, new(ImGui.GetTextLineHeightWithSpacing()),
                                             x.Name.ExtractText(),
                                             SelectedOnlineStatus.Contains(x.RowId),
                                             ImGuiSelectableFlags.DontClosePopups))
                                     {
                                         if (!SelectedOnlineStatus.Remove(x.RowId))
                                             SelectedOnlineStatus.Add(x.RowId);
                                     }
                                 }], [x => x.Name.ExtractText()]);

                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.Text($"{Lang.Get("Zone")}:");

                ImGui.TableNextColumn();
                ImGui.SetNextItemWidth(-1f);
                ZoneSelectCombo(ref SelectedZone, ref ZoneSearchInput);

                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.Text($"{Lang.Get("AutoNotifySPPlayers-ExtraCommand")}:");

                ImGui.TableNextColumn();
                ImGui.SetNextItemWidth(-1f);
                ImGui.InputTextMultiline("###CommandInput", ref SelectCommand, 1024, new(-1f, 60f * GlobalFontScale));
                if (ImGui.IsItemDeactivatedAfterEdit())
                {
                    try
                    {
                        _ = string.Format(SelectCommand, 0);
                    }
                    catch (Exception)
                    {
                        SelectCommand = string.Empty;
                    }
                }

                ImGuiOm.TooltipHover(GetLoc("AutoNotifySPPlayers-ExtraCommandInputHint"));
            }
        }

        ImGui.SameLine();
        var buttonSize = new Vector2(ImGui.CalcTextSize(GetLoc("Add")).X * 3, ImGui.GetItemRectSize().Y);
        if (ImGuiOm.ButtonIconWithTextVertical(FontAwesomeIcon.Plus, Lang.Get("Add"), buttonSize))
        {
            if (string.IsNullOrWhiteSpace(SelectName) && 
                SelectedOnlineStatus.Count == 0 && SelectedZone.Count == 0) return;

            var preset = new NotifiedPlayers
            {
                Name = SelectName,
                OnlineStatus = [..SelectedOnlineStatus], // 不这样就有引用关系了
                Zone = [..SelectedZone],
                Command = SelectCommand,
            };

            if (!ModuleConfig.NotifiedPlayer.Any(x => x.Equals(preset) || x.ToString() == preset.ToString()))
            {
                ModuleConfig.NotifiedPlayer.Add(preset);
                SaveConfig(ModuleConfig);
            }
        }
    }

    private void RenderTablePreset()
    {
        var tableSize = new Vector2(ImGui.GetContentRegionAvail().X - (20f * GlobalFontScale), 0);
        using var table = ImRaii.Table("###PresetTable", 6, ImGuiTableFlags.Borders, tableSize);
        if (!table) return;

        ImGui.TableSetupColumn("序号", ImGuiTableColumnFlags.WidthFixed, ImGui.CalcTextSize("1234").X);
        ImGui.TableSetupColumn("名称", ImGuiTableColumnFlags.None, 20);
        ImGui.TableSetupColumn("在线状态", ImGuiTableColumnFlags.None, 20);
        ImGui.TableSetupColumn("区域", ImGuiTableColumnFlags.None, 20);
        ImGui.TableSetupColumn("额外指令", ImGuiTableColumnFlags.None, 20);
        ImGui.TableSetupColumn("操作", ImGuiTableColumnFlags.None, 40);

        ImGui.TableNextRow(ImGuiTableRowFlags.Headers);
        ImGui.TableNextColumn();

        ImGui.TableNextColumn();
        ImGui.Text(GetLoc("Name"));

        ImGui.TableNextColumn();
        ImGui.Text(GetLoc("OnlineStatus"));

        ImGui.TableNextColumn();
        ImGui.Text(GetLoc("Zone"));

        ImGui.TableNextColumn();
        ImGui.Text(GetLoc("AutoNotifySPPlayers-ExtraCommand"));

        for (var i = 0; i < ModuleConfig.NotifiedPlayer.Count; i++)
        {
            var preset = ModuleConfig.NotifiedPlayer[i];
            using var id = ImRaii.PushId(preset.ToString());

            ImGui.TableNextRow();

            ImGui.TableNextColumn();
            ImGui.SetCursorPosY(ImGui.GetCursorPosY() + (2f * GlobalFontScale));
            ImGui.Text($"{i + 1}");

            ImGui.TableNextColumn();
            ImGui.SetCursorPosY(ImGui.GetCursorPosY() + (2f * GlobalFontScale));
            ImGui.Text($"{preset.Name}");
            ImGuiOm.TooltipHover(preset.Name);

            ImGui.TableNextColumn();
            ImGui.SetCursorPosY(ImGui.GetCursorPosY() + (2f * GlobalFontScale));
            RenderOnlineStatus(preset.OnlineStatus);
            if (ImGui.IsItemHovered())
            {
                using (ImRaii.Tooltip())
                    RenderOnlineStatus(preset.OnlineStatus, true);
            }

            ImGui.TableNextColumn();
            ImGui.SetCursorPosY(ImGui.GetCursorPosY() + (2f * GlobalFontScale));
            using (ImRaii.Group())
            {
                foreach (var zone in preset.Zone)
                {
                    if (!LuminaGetter.TryGetRow<TerritoryType>(zone, out var zoneData)) continue;

                    ImGui.Text($"{zoneData.ExtractPlaceName()}({zoneData.RowId})");
                    ImGui.SameLine();
                }
                ImGui.Spacing();
            }

            ImGui.TableNextColumn();
            ImGui.SetCursorPosY(ImGui.GetCursorPosY() + (2f * GlobalFontScale));
            ImGui.Text($"{preset.Command}");
            ImGuiOm.TooltipHover(preset.Command);

            ImGui.TableNextColumn();
            if (ImGuiOm.ButtonIconWithText(FontAwesomeIcon.TrashAlt, GetLoc("Delete")))
            {
                ModuleConfig.NotifiedPlayer.Remove(preset);
                ModuleConfig.Save(this);
                return;
            }

            ImGui.SameLine();
            if (ImGuiOm.ButtonIconWithText(FontAwesomeIcon.PenAlt, GetLoc("Edit")))
            {
                SelectName = preset.Name;
                SelectedOnlineStatus = [.. preset.OnlineStatus];
                SelectedZone = [.. preset.Zone];
                SelectCommand = preset.Command;

                ModuleConfig.NotifiedPlayer.Remove(preset);
                ModuleConfig.Save(this);
                return;
            }
        }

        return;

        void RenderOnlineStatus(HashSet<uint> onlineStatus, bool withText = false)
        {
            using var group = ImRaii.Group();
            foreach (var status in onlineStatus)
            {
                if (LuminaGetter.TryGetRow<OnlineStatus>(status, out var row)) continue;
                if (!DService.Texture.TryGetFromGameIcon(new(row.Icon), out var texture)) continue;

                using (ImRaii.Group())
                {
                    ImGui.Image(texture.GetWrapOrEmpty().Handle, new(ImGui.GetTextLineHeight()));
                    if (withText)
                    {
                        ImGui.SameLine();
                        ImGui.Text($"{row.Name.ExtractText()}({row.RowId})");
                    }
                }

                ImGui.SameLine();
            }
            ImGui.Spacing();
        }
    }

    private static void OnReceivePlayers(IReadOnlyList<IPlayerCharacter> characters)
    {
        foreach (var character in characters)
            CheckGameObject(character.ToStruct());
    }

    private static void CheckGameObject(BattleChara* obj)
    {
        if (ModuleConfig.NotifiedPlayer.Count == 0) 
            return;
        if (!DService.ClientState.IsLoggedIn || DService.ObjectTable.LocalPlayer is not { } localPlayer) 
            return;
        if (obj == null || (nint)obj == localPlayer.Address || !obj->IsCharacter())
            return;
        if (!ObjThrottler.Throttle(obj->GetGameObjectId(), 3_000)) 
            return;

        var currentTime = Environment.TickCount64;
        if (!NoticeTimeInfo.TryAdd(obj->GetGameObjectId(), currentTime))
        {
            if (NoticeTimeInfo.TryGetValue(obj->GetGameObjectId(), out var lastNoticeTime))
            {
                var timeDifference = currentTime - lastNoticeTime;
                switch (timeDifference)
                {
                    case < 15_000:
                        break;
                    case > 300_000:
                        NoticeTimeInfo[obj->GetGameObjectId()] = currentTime;
                        break;
                    default:
                        return;
                }
            }
        }

        foreach (var config in ModuleConfig.NotifiedPlayer)
        {
            bool[] checks = [true, true, true];
            var playerName = obj->NameString;

            if (!string.IsNullOrWhiteSpace(config.Name))
            {
                try
                {
                    checks[0] = config.Name.StartsWith('/')
                                    ? new Regex(config.Name).IsMatch(playerName)
                                    : playerName == config.Name;
                }
                catch (ArgumentException)
                {
                    checks[0] = false;
                }
            }

            if (config.OnlineStatus.Count > 0)
                checks[1] = config.OnlineStatus.Contains(obj->OnlineStatus);

            if (config.Zone.Count > 0) 
                checks[2] = config.Zone.Contains(DService.ClientState.TerritoryType);

            if (checks.All(x => x))
            {
                var message = Lang.Get("AutoNotifySPPlayers-NoticeMessage", playerName);

                Chat($"{message}\n     ({GetLoc("CurrentTime")}: {DateTime.Now})");
                NotificationInfo(message);
                Speak(message);

                if (!string.IsNullOrWhiteSpace(config.Command))
                {
                    foreach (var command in config.Command.Split('\n'))
                        ChatManager.SendMessage(string.Format(command.Trim(), playerName));
                }
            }
        }
    }

    private class NotifiedPlayers : IEquatable<NotifiedPlayers>
    {
        public string        Name         { get; set; } = string.Empty;
        public string        Command      { get; set; } = string.Empty;
        public HashSet<uint> Zone         { get; set; } = [];
        public HashSet<uint> OnlineStatus { get; set; } = [];

        public bool Equals(NotifiedPlayers? other)
        {
            if(ReferenceEquals(null, other)) return false;
            if(ReferenceEquals(this, other)) return true;
            return Name == other.Name && Command == other.Command && Zone.Equals(other.Zone) && OnlineStatus.Equals(other.OnlineStatus);
        }

        public override bool Equals(object? obj)
        {
            if(ReferenceEquals(null, obj)) return false;
            if(ReferenceEquals(this, obj)) return true;
            if(obj.GetType() != GetType()) return false;
            return Equals((NotifiedPlayers)obj);
        }

        public override int GetHashCode() => HashCode.Combine(Name, Command, Zone, OnlineStatus);

        public override string ToString() 
            => $"NotifiedPlayers_{Name}_{Command}_Zone{string.Join('.', Zone)}_OnlineStatus{string.Join('.', OnlineStatus)}";
    }

    private class Config : ModuleConfiguration
    {
        public List<NotifiedPlayers> NotifiedPlayer = [];
    }
}
