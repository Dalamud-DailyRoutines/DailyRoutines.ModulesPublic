using DailyRoutines.Abstracts;
using DailyRoutines.Managers;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.Gui.Dtr;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;
using System;
using System.Linq;
using System.Text;

namespace DailyRoutines.Modules;

public class AutoCountPlayers : DailyModuleBase
{
    public override ModuleInfo Info => new()
    {
        Title = GetLoc("AutoCountPlayersTitle"),
        Description = GetLoc("AutoCountPlayersDescription"),
        Category = ModuleCategories.General,
    };

    private static readonly uint LineColor;
    private static readonly uint DotColor;
    private static readonly uint TextColor;

    private static Config ModuleConfig = null!;

    private static IDtrBarEntry? Entry;

    private static string SearchInput = string.Empty;

    static AutoCountPlayers()
    {
        LineColor = ImGui.ColorConvertFloat4ToU32(LightSkyBlue);
        DotColor = ImGui.ColorConvertFloat4ToU32(RoyalBlue);
        TextColor = ImGui.ColorConvertFloat4ToU32(Orange);
    }

    public override void Init()
    {
        ModuleConfig = LoadConfig<Config>() ?? new();
        
        Overlay ??= new(this);
        Overlay.Flags &= ~ImGuiWindowFlags.NoTitleBar;
        Overlay.WindowName = $"{GetLoc("AutoCountPlayers-PlayersAroundInfo")}###AutoCountPlayers-Overlay";

        Entry ??= DService.DtrBar.Get("DailyRoutines-AutoCountPlayers");
        Entry.Shown = true;
        Entry.OnClick += () => Overlay.IsOpen ^= true;
        
        FrameworkManager.Register(false, OnUpdate);
    }

    public override void ConfigUI()
    {
        ImGui.SetNextItemWidth(120f * GlobalFontScale);
        if (ImGui.InputFloat(GetLoc("Scale"), ref ModuleConfig.ScaleFactor, 0, 0, "%.1f"))
            ModuleConfig.ScaleFactor = Math.Max(0.1f, ModuleConfig.ScaleFactor);
        if (ImGui.IsItemDeactivatedAfterEdit())
            ModuleConfig.Save(this);
    }

    public override unsafe void OverlayUI()
    {
        ImGui.SetNextItemWidth(-1f);
        ImGui.InputText("###Search", ref SearchInput, 128);

        if (BetweenAreas || DService.ClientState.LocalPlayer is not { } localPlayer) return;

        var source = PlayersAroundManager.CurrentPlayers.Where(x => string.IsNullOrWhiteSpace(SearchInput) ||
                                               x.ToString().Contains(SearchInput, StringComparison.OrdinalIgnoreCase))
                                   .OrderBy(x => x.Name.Length);

        var size = ScaledVector2(300f, 400f) * ModuleConfig.ScaleFactor;
        using var child = ImRaii.Child("列表", size, true);
        if (!child) return;
        foreach (var playerAround in source)
        {
            using var id = ImRaii.PushId($"{playerAround.GameObjectID}");
            if (ImGuiOm.ButtonIcon("定位", FontAwesomeIcon.Flag, GetLoc("AutoCountPlayers-Locate")))
            {
                if (LuminaCache.TryGetRow<Map>(DService.ClientState.MapId, out var map))
                {
                    var mapPos = WorldToMap(playerAround.Character.Position.ToVector2(), map);
                    var message = new SeStringBuilder()
                                  .Add(new PlayerPayload(playerAround.Name,
                                                         playerAround.Character.ToStruct()->HomeWorld))
                                  .Append(" (")
                                  .AddIcon(playerAround.Character.ClassJob.ValueNullable.ToBitmapFontIcon())
                                  .Append($" {playerAround.Job})")
                                  .Add(new NewLinePayload())
                                  .Append("     ")
                                  .Append(SeString.CreateMapLink(DService.ClientState.TerritoryType,
                                                                 DService.ClientState.MapId, mapPos.X, mapPos.Y))
                                  .Build();
                    Chat(message);
                }
            }

            if (ImGui.IsItemHovered() &&
                DService.Gui.WorldToScreen(playerAround.Character.Position, out var screenPos) &&
                DService.Gui.WorldToScreen(localPlayer.Position, out var localScreenPos))
            {
                var drawList = ImGui.GetForegroundDrawList();

                drawList.AddLine(localScreenPos, screenPos, LineColor, 8f);
                drawList.AddCircleFilled(localScreenPos, 12f, DotColor);
                drawList.AddCircleFilled(screenPos, 12f, DotColor);
                drawList.AddText(screenPos + ScaledVector2(16f), TextColor, $"{playerAround.Name} ({playerAround.Job})");
            }

            ImGui.SameLine();
            ImGui.Text($"{playerAround.Name} ({playerAround.Job})");
        }
    }

    private static void OnUpdate(IFramework _)
    {
        if (!Throttler.Throttle("AutoCountPlayers_OnUpdate")) return;

        Entry.Text = $"{GetLoc("AutoCountPlayers-PlayersAroundCount")}: {PlayersAroundManager.PlayersCount}";
        var tooltip = new StringBuilder();
        tooltip.AppendLine($"{GetLoc("AutoCountPlayers-PlayersAroundInfo")}:");
        PlayersAroundManager.CurrentPlayers.ForEach(x => tooltip.AppendLine($"{x.Name} ({x.Job})"));
        Entry.Tooltip = tooltip.ToString().Trim();
    }

    public override void Uninit()
    {
        FrameworkManager.Unregister(OnUpdate);
        
        Entry?.Remove();
        Entry = null;
        
        base.Uninit();
    }

    public class GamePlayerAround : IEquatable<GamePlayerAround>
    {
        public ICharacter Character    { get; init; }
        public string     Name         { get; init; }
        public string     Job          { get; init; }
        public ulong      GameObjectID { get; init; }

        private readonly string identifier;

        public GamePlayerAround(IGameObject obj)
        {
            Character = obj as ICharacter;

            GameObjectID = obj?.GameObjectId ?? 0;
            Name = Character?.Name.TextValue ?? string.Empty;
            Job = Character?.ClassJob.ValueNullable?.Name.ExtractText() ?? string.Empty;

            identifier = $"{Name}_{Job}_{GameObjectID}";
        }

        public bool IsValid() => Character.IsValid();

        public override string ToString() => identifier;

        public bool Equals(GamePlayerAround? other)
        {
            if(ReferenceEquals(null, other)) return false;
            if(ReferenceEquals(this, other)) return true;
            return GameObjectID == other.GameObjectID;
        }

        public override bool Equals(object? obj)
        {
            if(ReferenceEquals(null, obj)) return false;
            if(ReferenceEquals(this, obj)) return true;
            if(obj.GetType() != this.GetType()) return false;
            return Equals((GamePlayerAround)obj);
        }

        public override int GetHashCode() => GameObjectID.GetHashCode();
    }

    public class Config : ModuleConfiguration
    {
        public float ScaleFactor = 1;
    }
}
