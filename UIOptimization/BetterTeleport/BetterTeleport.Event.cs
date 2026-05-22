using DailyRoutines.Manager;
using Dalamud.Game.ClientState.Keys;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.System.Input;
using FFXIVClientStructs.FFXIV.Client.UI;
using OmenTools.Interop.Game.Lumina;
using OmenTools.OmenService;
using Control = FFXIVClientStructs.FFXIV.Client.Game.Control.Control;

namespace DailyRoutines.ModulesPublic.BetterTeleport;

public unsafe partial class BetterTeleport
{
    private void OnPostUseAction
    (
        ref bool                        isPrevented,
        ref ActionType                  actionType,
        ref uint                        actionID,
        ref ulong                       targetID,
        ref uint                        extraParam,
        ref ActionManager.UseActionMode queueState,
        ref uint                        comboRouteID
    )
    {
        if (actionType != ActionType.GeneralAction || actionID != 7)
            return;

        isPrevented = true;

        if (GameMain.Instance()->CurrentContentFinderConditionId != 0 ||
            isRefreshing                                              ||
            DService.Instance().Condition.IsBetweenAreas              ||
            Control.GetLocalPlayer() == null                          ||
            !UIModule.IsScreenReady())
            return;

        UIGlobals.PlaySoundEffect(23);

        if (fullWindow.IsOpen)
        {
            fullWindow.IsOpen = false;
            return;
        }
        
        Overlay.IsOpen ^= true;
    }

    private void OnZoneChanged(uint zone)
    {
        Overlay.IsOpen    = false;
        fullWindow.IsOpen = false;
        TaskHelper.RemoveQueueTasks(1);

        if (GameState.ContentFinderCondition != 0 ||
            !GameState.IsLoggedIn)
            return;

        TaskHelper.Enqueue(() => GameMain.Instance()->TerritoryLoadState == 3);
        TaskHelper.Enqueue
        (
            () =>
            {
                try
                {
                    isRefreshing = true;

                    if (DService.Instance().ObjectTable.LocalPlayer is null || DService.Instance().Condition.IsBetweenAreas) return false;

                    var instance = Telepo.Instance();
                    if (instance == null) return false;

                    var otherName = LuminaWrapper.GetAddonText(832);

                    RefreshHouseInfo();

                    records.Clear();

                    foreach (var aetheryte in MovementManager.Aetherytes)
                    {
                        if (!aetheryte.IsUnlocked()) continue;

                        if (aetheryte.Group == 5)
                        {
                            records.TryAdd(otherName, []);
                            records[otherName].Add(aetheryte);
                        }
                        else if (aetheryte.Version == 0)
                        {
                            var regionRow  = aetheryte.GetZone().PlaceNameRegion.Value;
                            var regionName = regionRow.RowId is 22 or 23 or 24 ? aetheryte.GetZone().PlaceNameRegion.Value.Name.ToString() : otherName;

                            records.TryAdd(regionName, []);
                            records[regionName].Add(aetheryte);
                        }
                        else
                        {
                            var versionName = $"{aetheryte.Version + 2}.0";

                            records.TryAdd(versionName, []);
                            records[versionName].Add(aetheryte);
                        }
                    }

                    RefreshHwdInfo();

                    RefreshFavoritesInfo();

                    foreach (var record in AllRecords)
                        record.Update();

                    RefreshDefaultOverlayItems();
                }
                finally
                {
                    isRefreshing = false;
                }

                return true;
            },
            "初始化信息",
            weight: 1
        );
    }

    private void OnCommand(string command, string args)
    {
        args = args.Trim();

        if (string.IsNullOrWhiteSpace(args))
        {
            Overlay.IsOpen ^= true;
            return;
        }

        var result = records.Values
                            .SelectMany(x => x)
                            .Concat(houseRecords)
                            .Where
                            (x =>
                                {
                                    var name = string.Empty;

                                    try
                                    {
                                        name = x.ToString();
                                    }
                                    catch
                                    {
                                        // ignored
                                    }

                                    return name.Contains(args, StringComparison.OrdinalIgnoreCase);
                                }
                            )
                            .OrderByDescending(x => x.IsAetheryte)
                            .ThenBy(x => x.Name.Length)
                            .FirstOrDefault();

        if (result == null) return;

        HandleTeleport(result);
    }
    
    private void OnPreInputIDPressed(ref bool? overrideResult, ref InputId id)
    {
        if (!Overlay.IsOpen)
            return;

        // Enter 键
        if (id == InputId.CMD_CHAT && DService.Instance().KeyState[VirtualKey.RETURN])
        {
            shouldFocusSearchBar = true;
            overrideResult       = false;
        }

    }

    private void OnLogin() =>
        OnZoneChanged(0);
}
