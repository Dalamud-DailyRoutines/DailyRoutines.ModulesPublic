using System.Reflection;
using DailyRoutines.Common.Info.Abstractions;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using Dalamud.Game.Gui.ContextMenu;
using Dalamud.Utility;
using Lumina.Data;
using Lumina.Excel.Sheets;
using OmenTools.OmenService;
using MenuItem = Dalamud.Game.Gui.ContextMenu.MenuItem;

namespace DailyRoutines.ModulesPublic;

public class ExpandItemMenuSearch : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("ExpandItemMenuSearchTitle"),
        Description = Lang.Get("ExpandItemMenuSearchDescription"),
        Category    = ModuleCategory.UIOptimization,
        Author      = ["HSS"]
    };

    public override ModulePermission Permission { get; } = new() { AllDefaultEnabled = true };

    private SearchMenuItemBase[] SearchMenuItems
    {
        get
        {
            if (field is { Length: > 0 }) return field;

            return field = typeof(ExpandItemMenuSearch)
                           .GetNestedTypes(BindingFlags.NonPublic)
                           .Where(type => !type.IsAbstract && typeof(SearchMenuItemBase).IsAssignableFrom(type))
                           .Select(type => (SearchMenuItemBase)Activator.CreateInstance(type, true)!)
                           .OrderBy(searchMenuItem => searchMenuItem.Order)
                           .ThenBy(searchMenuItem => searchMenuItem.ConfigKey, StringComparer.Ordinal)
                           .ToArray();
        }
    }
    
    private Config config = null!;

    private readonly UpperContainerItem menu;

    public ExpandItemMenuSearch() =>
        menu = new(this);

    protected override void Init()
    {
        config = Config.Load(this) ?? new();

        DService.Instance().ContextMenu.OnMenuOpened += OnMenuOpened;
    }
    
    protected override void Uninit() =>
        DService.Instance().ContextMenu.OnMenuOpened -= OnMenuOpened;

    protected override void ConfigUI()
    {
        foreach (var searchMenuItem in SearchMenuItems)
        {
            var value = config.SearchMenuEnabledStates
                                    .GetValueOrDefault(searchMenuItem.ConfigKey, searchMenuItem.DefaultEnabled);
            if (!ImGui.Checkbox(Lang.Get(searchMenuItem.LocKey), ref value)) continue;

            config.SearchMenuEnabledStates[searchMenuItem.ConfigKey] = value;
            config.Save(this);
        }

        ImGui.Separator();
        RenderCheckbox
        (
            Lang.Get("ExpandItemMenuSearch-GlamourTakesPriority"),
            ref config.GlamourPrioritize
        );
    }

    private void RenderCheckbox(string label, ref bool value)
    {
        if (ImGui.Checkbox(label, ref value))
            config.Save(this);
    }
    
    #region 右键菜单处理

    private void OnMenuOpened(IMenuOpenedArgs args)
    {
        // 检查是否有有效的物品ID
        if (!ContextMenuItemManager.Instance().IsValidItem) return;

        // 添加菜单项
        AddContextMenuItemsByConfig(args);
    }

    private void AddContextMenuItemsByConfig(IMenuOpenedArgs args)
    {
        var shouldProcess = SearchMenuItems.Any
        (searchMenuItem => config.SearchMenuEnabledStates
                                 .GetValueOrDefault(searchMenuItem.ConfigKey, searchMenuItem.DefaultEnabled)
        );

        if (shouldProcess)
            args.AddMenuItem(menu.Get());
    }

    #endregion

    private class Config : ModuleConfig
    {
        public bool                     GlamourPrioritize       = true;
        public Dictionary<string, bool> SearchMenuEnabledStates = [];
    }

    private abstract class SearchMenuItemBase : MenuItemBase
    {
        protected readonly ExpandItemMenuSearch module;

        protected SearchMenuItemBase(ExpandItemMenuSearch module)
        {
            this.module = module;
            Name        = Lang.Get(LocKey);
        }

        public sealed override string Name       { get; protected set; }
        public sealed override string Identifier { get; protected set; } = nameof(ExpandItemMenuSearch);

        public abstract string LocKey         { get; }
        public abstract string ConfigKey      { get; }
        public virtual  bool   DefaultEnabled => false;
        public virtual  int    Order          => 0;
    }

    private class UpperContainerItem(ExpandItemMenuSearch module) : MenuItemBase
    {
        public override string Name       { get; protected set; } = Lang.Get("ExpandItemMenuSearch-SearchTitle");
        public override string Identifier { get; protected set; } = nameof(ExpandItemMenuSearch);

        protected override bool WithDRPrefix { get; set; } = true;
        protected override bool IsSubmenu    { get; set; } = true;

        protected override void OnClicked(IMenuItemClickedArgs args) =>
            args.OpenSubmenu(Name, ProcessMenuItems());

        private List<MenuItem> ProcessMenuItems()
        {
            var list = new List<MenuItem>();

            foreach (var searchMenuItem in module.SearchMenuItems)
            {
                if (!module.config.SearchMenuEnabledStates
                           .GetValueOrDefault(searchMenuItem.ConfigKey, searchMenuItem.DefaultEnabled))
                    continue;
                
                list.Add(searchMenuItem.Get());
            }

            return list;
        }
    }

    // 光之收藏家
    private class FFXIVSCItem
    (
        ExpandItemMenuSearch module
    ) : SearchMenuItemBase(module)
    {
        private const string URL = "https://ff14risingstones.web.sdo.com/pc/index.html#/search?equipmentid={0}&section=glamour";

        public override string LocKey         => "ExpandItemMenuSearch-SearchFFXIVSC";
        public override string ConfigKey      => nameof(FFXIVSCItem);
        public override bool   DefaultEnabled => GameState.IsCN || GameState.IsTC;
        public override int    Order          => 100;

        protected override void OnClicked(IMenuItemClickedArgs args)
        {
            var itemID = 0U;

            // 优先使用幻化物品 (如果配置了优先幻化且有幻化物品)
            if (module.config.GlamourPrioritize && ContextMenuItemManager.Instance().CurrentGlamourID > 0)
                itemID = ContextMenuItemManager.Instance().CurrentGlamourItem?.RowId ?? 0;
            else
                itemID = ContextMenuItemManager.Instance().CurrentItem?.RowId ?? 0;

            if (itemID != 0)
                Util.OpenLink(string.Format(URL, itemID));
        }
    }

    // 最终幻想 14 中文维基
    private class HuijiWikiItem
    (
        ExpandItemMenuSearch module
    ) : SearchMenuItemBase(module)
    {
        private const string URL = "https://ff14.huijiwiki.com/wiki/%E7%89%A9%E5%93%81:{0}";

        public override string LocKey         => "ExpandItemMenuSearch-SearchHuijiWiki";
        public override string ConfigKey      => nameof(HuijiWikiItem);
        public override bool   DefaultEnabled => GameState.IsCN || GameState.IsTC;
        public override int    Order          => 10;

        protected override void OnClicked(IMenuItemClickedArgs args)
        {
            var itemName = string.Empty;

            // 优先使用幻化物品名称（如果配置了优先幻化且有幻化物品）
            if (module.config.GlamourPrioritize && ContextMenuItemManager.Instance().CurrentGlamourID > 0)
                itemName = ContextMenuItemManager.Instance().CurrentGlamourItem?.Name.ToString();
            else
                itemName = ContextMenuItemManager.Instance().CurrentItem?.Name.ToString();

            if (!string.IsNullOrWhiteSpace(itemName))
                Util.OpenLink(string.Format(URL, itemName));
        }
    }

    // Console Games Wiki
    private class ConsoleGameWikiItem
    (
        ExpandItemMenuSearch module
    ) : SearchMenuItemBase(module)
    {
        private const string URL =
            "https://ffxiv.consolegameswiki.com/mediawiki/index.php?search={0}&title=Special%3ASearch&go=%E5%89%8D%E5%BE%80";

        public override string LocKey         => "ExpandItemMenuSearch-SearchConsoleGamesWiki";
        public override string ConfigKey      => nameof(ConsoleGameWikiItem);
        public override bool   DefaultEnabled => GameState.IsGL;
        public override int    Order          => 20;

        protected override void OnClicked(IMenuItemClickedArgs args)
        {
            var itemName = string.Empty;

            // 优先使用幻化物品名称（如果配置了优先幻化且有幻化物品）
            if (module.config.GlamourPrioritize && ContextMenuItemManager.Instance().CurrentGlamourID > 0)
                itemName = ContextMenuItemManager.Instance().CurrentGlamourItem?.Name.ToString();
            else
                itemName = ContextMenuItemManager.Instance().CurrentItem?.Name.ToString();

            if (!string.IsNullOrWhiteSpace(itemName))
                Util.OpenLink(string.Format(URL, Uri.EscapeDataString(itemName)));
        }
    }

    // Garland Tools DB (国服)
    private class GarlandToolsDBCNItem
    (
        ExpandItemMenuSearch module
    ) : SearchMenuItemBase(module)
    {
        private const string URL =
            "https://www.garlandtools.cn/db/#item/{0}";

        public override string LocKey         => "ExpandItemMenuSearch-SearchGarlandToolsDBCN";
        public override string ConfigKey      => nameof(GarlandToolsDBCNItem);
        public override bool   DefaultEnabled => GameState.IsCN || GameState.IsTC;
        public override int    Order          => 30;

        protected override void OnClicked(IMenuItemClickedArgs args)
        {
            var itemID = 0U;

            // 优先使用幻化物品ID（如果配置了优先幻化且有幻化物品）
            if (module.config.GlamourPrioritize && ContextMenuItemManager.Instance().CurrentGlamourID > 0)
                itemID = ContextMenuItemManager.Instance().CurrentGlamourID;
            else
                itemID = ContextMenuItemManager.Instance().CurrentItemID;

            if (itemID != 0)
                Util.OpenLink(string.Format(URL, itemID));
        }
    }

    // Garland Tools DB (国服)
    private class GarlandToolsDBItem
    (
        ExpandItemMenuSearch module
    ) : SearchMenuItemBase(module)
    {
        private const string URL =
            "https://www.garlandtools.org/db/#item/{0}";

        public override string LocKey         => "ExpandItemMenuSearch-SearchGarlandToolsDB";
        public override string ConfigKey      => nameof(GarlandToolsDBItem);
        public override bool   DefaultEnabled => GameState.IsGL;
        public override int    Order          => 40;

        protected override void OnClicked(IMenuItemClickedArgs args)
        {
            var itemID = 0U;

            // 优先使用幻化物品ID（如果配置了优先幻化且有幻化物品）
            if (module.config.GlamourPrioritize && ContextMenuItemManager.Instance().CurrentGlamourID > 0)
                itemID = ContextMenuItemManager.Instance().CurrentGlamourID;
            else
                itemID = ContextMenuItemManager.Instance().CurrentItemID;

            if (itemID != 0)
                Util.OpenLink(string.Format(URL, itemID));
        }
    }

    // Lodestone DB
    private class LodestoneDBItem
    (
        ExpandItemMenuSearch module
    ) : SearchMenuItemBase(module)
    {
        private const string URL =
            "https://na.finalfantasyxiv.com/lodestone/playguide/db//search/?patch=&db_search_category=&q={0}";

        public override string LocKey         => "ExpandItemMenuSearch-SearchLodestoneDB";
        public override string ConfigKey      => nameof(LodestoneDBItem);
        public override bool   DefaultEnabled => GameState.IsGL;
        public override int    Order          => 110;

        protected override void OnClicked(IMenuItemClickedArgs args)
        {
            var itemName = string.Empty;

            // 优先使用幻化物品名称（如果配置了优先幻化且有幻化物品）
            if (module.config.GlamourPrioritize && ContextMenuItemManager.Instance().CurrentGlamourID > 0)
                itemName = ContextMenuItemManager.Instance().CurrentGlamourItem?.Name.ToString();
            else
                itemName = ContextMenuItemManager.Instance().CurrentItem?.Name.ToString();

            if (!string.IsNullOrWhiteSpace(itemName))
                Util.OpenLink(string.Format(URL, itemName));
        }
    }

    // Gamer Escape Wiki
    private class GamerEscapeWikiItem
    (
        ExpandItemMenuSearch module
    ) : SearchMenuItemBase(module)
    {
        private const string URL =
            "https://ffxiv.gamerescape.com/?search={0}";

        public override string LocKey         => "ExpandItemMenuSearch-SearchGamerEscapeWiki";
        public override string ConfigKey      => nameof(GamerEscapeWikiItem);
        public override bool   DefaultEnabled => GameState.IsGL;
        public override int    Order          => 120;

        protected override void OnClicked(IMenuItemClickedArgs args)
        {
            var itemName = string.Empty;

            // 优先使用幻化物品名称（如果配置了优先幻化且有幻化物品）
            if (module.config.GlamourPrioritize && ContextMenuItemManager.Instance().CurrentGlamourID > 0)
                itemName = ContextMenuItemManager.Instance().CurrentGlamourItem?.Name.ToString();
            else
                itemName = ContextMenuItemManager.Instance().CurrentItem?.Name.ToString();

            if (!string.IsNullOrWhiteSpace(itemName))
                Util.OpenLink(string.Format(URL, Uri.EscapeDataString(itemName)));
        }
    }

    // ERIONES DB
    private class ERIONESItem
    (
        ExpandItemMenuSearch module
    ) : SearchMenuItemBase(module)
    {
        private const string URL = "https://{0}eriones.com/search?i={1}";

        public override string LocKey         => "ExpandItemMenuSearch-SearchERIONES";
        public override string ConfigKey      => nameof(ERIONESItem);
        public override bool   DefaultEnabled => GameState.IsGL;
        public override int    Order          => 130;

        protected override void OnClicked(IMenuItemClickedArgs args)
        {
            var itemName = string.Empty;

            // 优先使用幻化物品名称（如果配置了优先幻化且有幻化物品）
            if (module.config.GlamourPrioritize && ContextMenuItemManager.Instance().CurrentGlamourID > 0)
                itemName = ContextMenuItemManager.Instance().CurrentGlamourItem?.Name.ToString();
            else
                itemName = ContextMenuItemManager.Instance().CurrentItem?.Name.ToString();

            if (!string.IsNullOrWhiteSpace(itemName))
            {
                if (itemName.Length > 25)
                    itemName = itemName[..25];
                Util.OpenLink(string.Format(URL, GetPrefixByLang(), Uri.EscapeDataString(itemName)));
            }
        }

        private static string GetPrefixByLang() =>
            GameState.ClientLanguge switch
            {
                Language.English            => "en.",
                Language.French             => "fr.",
                Language.German             => "de.",
                Language.ChineseSimplified  => "cn.",
                Language.ChineseTraditional => "cn.", // 因为也是国服客户端的代码
                Language.Korean             => "ko.",
                _                           => string.Empty
            };
    }

    // 繁中工具箱
    private class TCToolboxItem
    (
        ExpandItemMenuSearch module
    ) : SearchMenuItemBase(module)
    {
        private const string URL = "https://cycleapple.github.io/ffxiv-item-search-tc?selected={0}&q={1}";

        public override string LocKey         => "ExpandItemMenuSearch-SearchTCToolbox";
        public override string ConfigKey      => nameof(TCToolboxItem);
        public override bool   DefaultEnabled => GameState.IsTC;
        public override int    Order          => 130;

        protected override void OnClicked(IMenuItemClickedArgs args)
        {
            Item? itemToSearch = null;

            // 优先使用幻化
            if (module.config.GlamourPrioritize && ContextMenuItemManager.Instance().CurrentGlamourID > 0)
                itemToSearch = ContextMenuItemManager.Instance().CurrentGlamourItem;
            else
                itemToSearch = ContextMenuItemManager.Instance().CurrentItem;

            if (itemToSearch == null) return;

            Util.OpenLink(string.Format(URL, itemToSearch?.RowId, Uri.EscapeDataString(itemToSearch?.Name.ToString() ?? string.Empty)));
        }
    }
}
