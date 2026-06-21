using DailyRoutines.Manager;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel.Sheets;
using OmenTools.Dalamud;
using OmenTools.Info.Game.AetheryteRecord;
using OmenTools.Interop.Game.Helpers;
using OmenTools.Interop.Game.Lumina;

namespace DailyRoutines.ModulesPublic.Interface.BetterTeleport;

public partial class BetterTeleport
{
    // 模块收藏点
    private void RefreshFavoritesInfo()
    {
        if (config.Favorites.Count == 0) return;
        favorites = config.Favorites
                          .Select(x => AllRecords.FirstOrDefault(d => d.RowID == x))
                          .Where(x => x != null)
                          .OfType<AetheryteRecord>()
                          .OrderBy(x => x.RowID)
                          .ToList();
    }

    // 房区
    private void RefreshHouseInfo()
    {
        houseRecords.Clear();

        var allHousingMarkers = LuminaGetter.GetSub<HousingMapMarkerInfo>()
                                            .SelectMany(x => x)
                                            .Where(x => x.Map.ValueNullable != null)
                                            .ToList();

        foreach (var aetheryte in DService.Instance().AetheryteList)
        {
            if (!LuminaGetter.TryGetRow<Aetheryte>(aetheryte.AetheryteID, out var aetheryteRow)) continue;
            if (aetheryteRow.PlaceName.RowId is not (1145 or 1160)) continue;

            var housingMarkers = allHousingMarkers.Where(x => x.Map.Value.TerritoryType.RowId == aetheryteRow.Territory.RowId).ToList();

            // 公寓
            if (aetheryte.IsApartment)
            {
                var aptHouseInfo = HousingManager.GetOwnedHouseId(EstateType.ApartmentBuilding);
                var aptRoomInfo  = HousingManager.GetOwnedHouseId(EstateType.ApartmentRoom);
                if (aptHouseInfo.Id == INVALID_HOUSE_ID || aptRoomInfo.Id == INVALID_HOUSE_ID) continue;

                var aptMarker = housingMarkers.FirstOrDefault(x => x.SubrowId == 60);
                if (aptMarker.RowId == 0) continue;

                houseRecords.Add
                (
                    new AetheryteRecord
                    (
                        aetheryte.AetheryteID,
                        aetheryte.SubIndex,
                        255,
                        0,
                        aetheryte.TerritoryID,
                        aptMarker.Map.RowId,
                        true,
                        new(aptMarker.X, aptMarker.Y, aptMarker.Z),
                        Lang.Get
                        (
                            "BetterTeleport-HouseInfo-Apartment",
                            aetheryteRow.Territory.Value.ExtractPlaceName(),
                            LuminaWrapper.GetAddonText(6760),
                            aptHouseInfo.WardIndex + 1,
                            aptRoomInfo.RoomNumber
                        )
                    )
                );
                continue;
            }

            // 共享房屋
            if (aetheryte.IsSharedHouse)
            {
                var sharedHouseMarker = housingMarkers.FirstOrDefault(x => x.SubrowId == aetheryte.Plot);
                if (sharedHouseMarker.RowId == 0) continue;

                houseRecords.Add
                (
                    new AetheryteRecord
                    (
                        aetheryte.AetheryteID,
                        aetheryte.SubIndex,
                        255,
                        0,
                        aetheryte.TerritoryID,
                        sharedHouseMarker.Map.RowId,
                        true,
                        new(sharedHouseMarker.X, sharedHouseMarker.Y, sharedHouseMarker.Z),
                        Lang.Get
                        (
                            "BetterTeleport-HouseInfo-Estate",
                            aetheryteRow.Territory.Value.ExtractPlaceName(),
                            Lang.Get("BetterTeleport-HouseType-SharedHouse"),
                            aetheryte.Ward,
                            aetheryte.Plot
                        )
                    )
                );
                continue;
            }

            // 部队房屋
            if (aetheryteRow.PlaceName.RowId == 1145)
            {

                var fcHouseInfo = HousingManager.GetOwnedHouseId(EstateType.FreeCompanyEstate);
                if (fcHouseInfo.Id == INVALID_HOUSE_ID) continue;

                var fcMarker = housingMarkers.FirstOrDefault(x => x.SubrowId == fcHouseInfo.PlotIndex);
                if (fcMarker.RowId == 0) continue;

                houseRecords.Add
                (
                    new AetheryteRecord
                    (
                        aetheryte.AetheryteID,
                        aetheryte.SubIndex,
                        255,
                        0,
                        aetheryte.TerritoryID,
                        fcMarker.Map.RowId,
                        true,
                        new(fcMarker.X, fcMarker.Y, fcMarker.Z),
                        Lang.Get
                        (
                            "BetterTeleport-HouseInfo-Estate",
                            aetheryteRow.Territory.Value.ExtractPlaceName(),
                            Lang.Get("BetterTeleport-HouseType-FreeCompany"),
                            fcHouseInfo.WardIndex + 1,
                            fcHouseInfo.PlotIndex + 1
                        )
                    )
                );
                continue;
            }

            // 个人房屋
            if (aetheryteRow.PlaceName.RowId == 1160)
            {
                var personalHouseInfo = HousingManager.GetOwnedHouseId(EstateType.PersonalEstate);
                if (personalHouseInfo.Id == INVALID_HOUSE_ID) continue;

                var personalMarker = housingMarkers.FirstOrDefault(x => x.SubrowId == personalHouseInfo.PlotIndex);
                if (personalMarker.RowId == 0) continue;

                houseRecords.Add
                (
                    new AetheryteRecord
                    (
                        aetheryte.AetheryteID,
                        aetheryte.SubIndex,
                        255,
                        0,
                        aetheryte.TerritoryID,
                        personalMarker.Map.RowId,
                        true,
                        new(personalMarker.X, personalMarker.Y, personalMarker.Z),
                        Lang.Get
                        (
                            "BetterTeleport-HouseInfo-Estate",
                            aetheryteRow.Territory.Value.ExtractPlaceName(),
                            Lang.Get("BetterTeleport-HouseType-Personal"),
                            personalHouseInfo.WardIndex + 1,
                            personalHouseInfo.PlotIndex + 1
                        )
                    )
                );

                continue;
            }

            DLog.Warning($"[{nameof(BetterTeleport)}] 检测到房屋相关以太之光 (ID: {aetheryte.AetheryteID}), 但无法归属至目前任一已知房屋类型, 已忽略");
        }
    }

    // 天穹街
    private void RefreshFirmamentInfo()
    {
        var markers = LuminaGetter.GetRowOrDefault<TerritoryType>(886)
                                  .GetMapMarkers()
                                  .Where(x => x.DataType is 3 or 4)
                                  .Select
                                  (x => new
                                      {
                                          Name     = AetheryteRecord.TryParseName(x, out var markerName) ? markerName : string.Empty,
                                          Position = PositionHelper.TextureToWorld(new(x.X, x.Y), LuminaGetter.GetRowOrDefault<Map>(574)).ToVector3(0),
                                          Marker   = x
                                      }
                                  )
                                  .DistinctBy(x => x.Name);

        byte indexCounter = 0;

        foreach (var marker in markers)
        {
            var record = new AetheryteRecord(70, indexCounter, 254, 1, 886, 574, false, marker.Position, marker.Name);

            records.TryAdd("3.0", []);
            records["3.0"].Add(record);

            indexCounter++;
        }
    }
}
