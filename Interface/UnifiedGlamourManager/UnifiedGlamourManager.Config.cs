using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Extensions;
using OmenTools.Interop.Game.Lumina;
using ItemSheet = Lumina.Excel.Sheets.Item;

namespace DailyRoutines.ModulesPublic.Interface.UnifiedGlamourManager;

public partial class UnifiedGlamourManager
{
    private Config config = null!;

    private sealed class Config : ModuleConfig
    {
        public bool            UseGridView = true;
        public List<SavedItem> Favorites   = [];
    }

    private void NormalizeConfig()
    {
        config.Favorites = config.Favorites
                                 .Where(static x => x.ItemID != 0 && LuminaGetter.TryGetRow<ItemSheet>(x.ItemID, out _))
                                 .Select
                                 (static x =>
                                     {
                                         var name = x.Name ?? string.Empty;
                                         if (string.IsNullOrWhiteSpace(name)                              &&
                                             LuminaGetter.TryGetRow<ItemSheet>(x.ItemID, out var itemRow) &&
                                             TryGetItemName(itemRow, out var itemName))
                                             name = itemName;

                                         return new SavedItem
                                         {
                                             ItemID  = x.ItemID,
                                             Name    = name,
                                             AddedAt = Math.Max(0, x.AddedAt)
                                         };
                                     }
                                 )
                                 .GroupBy(static x => x.ItemID)
                                 .Select(static x => x.OrderByDescending(y => y.AddedAt).First())
                                 .OrderByDescending(static x => x.AddedAt)
                                 .ToList();
        favoriteItemIDs.Clear();
        favoriteItemIDs.UnionWith(config.Favorites.Select(static x => x.ItemID));
    }

    private bool IsFavorite(uint itemID) =>
        favoriteItemIDs.Contains(itemID);

    private void ToggleFavorite(UnifiedItem item)
    {
        if (config.Favorites.RemoveAll(x => x.ItemID == item.ItemID) == 0)
        {
            config.Favorites.Add
            (
                new()
                {
                    ItemID  = item.ItemID,
                    Name    = item.Name,
                    AddedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                }
            );
        }

        SaveConfig();
    }

    private void SaveConfig()
    {
        NormalizeConfig();
        config.Save(this);
        MarkFilteredItemsDirty();
    }
}
