using System.Numerics;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Lumina.Excel.Sheets;
using OmenTools.Interop.Game.Lumina;
using OmenTools.Interop.Game.Models;
using OmenTools.OmenService;

namespace DailyRoutines.ModulesPublic;

public unsafe class AutoDisplayTreasureMapLocation : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoDisplayTreasureMapLocationTitle"),
        Description = Lang.Get("AutoDisplayTreasureMapLocationDescription"),
        Category    = ModuleCategory.System
    };

    public override ModulePermission Permission { get; } = new() { AllDefaultEnabled = true };

    private static readonly CompSig ShowTreasureMapSig = new("4C 8B DC 55 53 56 49 8D AB ?? ?? ?? ?? 48 81 EC ?? ?? ?? ?? 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 85 ?? ?? ?? ?? 49 89 7B ?? 48 8D 45");
    private delegate void ShowTreasureMapDelegate(nint agent, ushort rankID, ushort subRowID, byte isJustOpened);
    private Hook<ShowTreasureMapDelegate>? ShowTreasureMapHook;

    private Config config = null!;

    protected override void Init()
    {
        config = Config.Load(this) ?? new();

        ShowTreasureMapHook = ShowTreasureMapSig.GetHook<ShowTreasureMapDelegate>(ShowTreasureMapDetour);
        ShowTreasureMapHook.Enable();

        CommandManager.Instance().AddSubCommand(COMMAND, new(OnCommand)
        {
            HelpMessage = Lang.Get("AutoDisplayTreasureMapLocation-CommandHelp")
        });
    }

    protected override void Uninit() =>
        CommandManager.Instance().RemoveSubCommand(COMMAND);

    protected override void ConfigUI()
    {
        ImGui.TextColored(KnownColor.LightSkyBlue.ToUInt(), Lang.Get("Command"));
        using (ImRaii.PushIndent())
            ImGui.TextUnformatted($"/pdr {COMMAND} -> {Lang.Get("AutoDisplayTreasureMapLocation-CommandHelp")}");

        ImGui.NewLine();
        
        ImGui.TextColored(KnownColor.LightSkyBlue.ToUInt(), Lang.Get("AutoDisplayTreasureMapLocation-AutoOpen"));

        using (ImRaii.PushIndent())
        {
            if (ImGui.Checkbox
                (
                    Lang.Get("AutoDisplayTreasureMapLocation-AutoOpen-OnDecipher"),
                    ref config.ShowOnDecipher
                ))
                config.Save(this);

            if (ImGui.Checkbox
                (
                    Lang.Get("AutoDisplayTreasureMapLocation-AutoOpen-OnOpen"),
                    ref config.ShowOnOpen
                ))
                config.Save(this);
        }
    }

    private void ShowTreasureMapDetour(nint agent, ushort rankID, ushort subRowID, byte isJustOpened)
    {
        var shouldOpenLocation = isJustOpened != 0 ? config.ShowOnDecipher : config.ShowOnOpen;

        if (shouldOpenLocation && TryGetTreasureMap(rankID, subRowID, out var treasureMap))
        {
            OpenMapLocation(treasureMap);
            return;
        }

        ShowTreasureMapHook.Original(agent, rankID, subRowID, isJustOpened);
    }

    private static void OnCommand(string command, string arguments)
    {
        if (TryGetCurrentTreasureMap(out var treasureMap))
            OpenMapLocation(treasureMap);
    }

    private static bool TryGetCurrentTreasureMap(out TreasureMap treasureMap)
    {
        treasureMap = default;

        var eventItemManager = EventItemManager.Instance();
        if (eventItemManager == null) return false;

        var rankID = eventItemManager->GetTreasureHuntRank();
        if (rankID is 0 or > ushort.MaxValue)
            return false;

        return TryGetTreasureMap((ushort)rankID, eventItemManager->GetTreasureSpotSubKey(), out treasureMap);
    }

    private static bool TryGetTreasureMap(ushort rankID, ushort subRowID, out TreasureMap treasureMap)
    {
        treasureMap = default;

        if (!LuminaGetter.TryGetRow<TreasureHuntRank>(rankID, out var rank) ||
            !LuminaGetter.TryGetSubRow<TreasureSpot>(rankID, out var spot, subRowID))
            return false;

        if (spot.Location.ValueNullable is not { } location ||
            location.Map.ValueNullable is not { } map)
            return false;

        treasureMap = new
        (
            rank.KeyItemName.RowId,
            map.RowId,
            location.GetPosition()
        );

        return true;
    }

    private static void OpenMapLocation(TreasureMap treasureMap)
    {
        var agent = AgentMap.Instance();
        if (agent == null) return;

        agent->SetMapFlagAndOpen(treasureMap.MapID, treasureMap.Position);
    }

    private sealed class Config : ModuleConfig
    {
        public bool ShowOnDecipher = true;
        public bool ShowOnOpen     = true;
    }

    private readonly record struct TreasureMap
    (
        uint    EventItemID,
        uint    MapID,
        Vector3 Position
    );

    private const string COMMAND = "tmap";
}
