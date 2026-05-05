using DailyRoutines.Common.Module.Abstractions;

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

    private void LoadModuleConfig()
    {
        config = LoadConfig<Config>() ?? new();
        NormalizeConfig();
    }

    private void SaveModuleConfig()
    {
        NormalizeConfig();
        SaveConfig(config);
    }

    private void NormalizeConfig()
    {
        config.Favorites ??= [];
        config.PreviewItems ??= [];

        CleanPreviewCache();
    }

    #endregion

    #region 喜爱

    private int GetLoadedFavoriteCount()
    {
        if (config.Favorites.Count == 0 || items.Count == 0)
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
        => GetSaved(itemID) != null;

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

        SaveModuleConfig();
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
        config.PreviewItems.RemoveAll(x => x.ItemID <= 1);
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
