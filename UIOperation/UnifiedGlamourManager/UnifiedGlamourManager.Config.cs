using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Extensions;

namespace DailyRoutines.ModulesPublic;

public unsafe partial class UnifiedGlamourManager
{
    #region 配置模型

    private sealed class Config : ModuleConfig
    {
        public List<SavedItem> Favorites = [];
        public List<CachedPreviewItem> PreviewItems = [];
        public bool ShowRetainerPreview = true;
        public bool ShowInventoryPreview = true;
    }

    #endregion

    #region 配置读写

    private void SaveConfig()
    {
        NormalizeConfig();
        config.Save(this);
        MarkFilteredItemsDirty();
    }

    private void NormalizeConfig()
    {
        config.Favorites ??= [];
        config.PreviewItems ??= [];

        CleanPreviewCache();
        SyncFavoriteItemIDs();
    }

    #endregion

    #region 喜爱

    private int GetLoadedFavoriteCount()
    {
        if (favoriteItemIDs.Count == 0 || items.Count == 0)
            return 0;

        return items
            .Where(x => IsFavorite(x.ItemID))
            .Select(x => x.ItemID)
            .Distinct()
            .Count();
    }

    private SavedItem? GetSaved(uint itemID)
        => config.Favorites.FirstOrDefault(x => x.ItemID == itemID);

    private bool IsFavorite(uint itemID)
        => favoriteItemIDs.Contains(itemID);

    private void ToggleFavorite(UnifiedItem item)
    {
        var existing = GetSaved(item.ItemID);
        if (existing != null)
        {
            config.Favorites.Remove(existing);
        }
        else
        {
            config.Favorites.Add(new()
            {
                ItemID = item.ItemID,
                Name = item.Name,
                AddedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            });
        }

        SaveConfig();
    }

    private void SyncFavoriteItemIDs()
    {
        favoriteItemIDs.Clear();

        foreach (var favorite in config.Favorites)
        {
            if (favorite.ItemID > MIN_VALID_ITEM_ID)
                favoriteItemIDs.Add(favorite.ItemID);
        }
    }

    #endregion

    #region 预览缓存

    private void CleanPreviewCache()
    {
        NormalizeRetainerPreviewRecords();
        RemoveInvalidPreviewItems();
        RemoveDuplicatePreviewItems();
    }

    private void NormalizeRetainerPreviewRecords()
    {
        foreach (var item in config.PreviewItems.Where(x => x.Source == PREVIEW_SOURCE_RETAINER))
        {
            item.SourceKey = NormalizeSourceKey(item.SourceKey, item.Owner);

            if (string.IsNullOrWhiteSpace(item.Owner))
                item.Owner = Lang.Get("UnifiedGlamourManager-PreviewSourceRetainer");
        }
    }

    private void RemoveInvalidPreviewItems()
    {
        config.PreviewItems.RemoveAll(x => x.ItemID <= MIN_VALID_ITEM_ID);
    }

    private void RemoveDuplicatePreviewItems()
    {
        config.PreviewItems = config.PreviewItems
            .GroupBy(GetPreviewCacheKey)
            .Select(x => x.OrderByDescending(y => y.UpdatedAt).First())
            .ToList();
    }

    private static string GetPreviewCacheKey(CachedPreviewItem item)
        => $"{item.Source}|{item.SourceKey}|{item.Owner}|{item.SlotIndex}|{item.ItemID}";

    #endregion
}
