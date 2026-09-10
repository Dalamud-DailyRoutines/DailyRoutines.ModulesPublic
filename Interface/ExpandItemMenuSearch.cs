using System.Reflection;
using DailyRoutines.Common.Extensions;
using DailyRoutines.Common.Info;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using Dalamud.Utility;
using Lumina.Data;
using Lumina.Excel.Sheets;
using OmenTools.OmenService;

namespace DailyRoutines.ModulesPublic.Interface;

public class ExpandItemMenuSearch : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("ExpandItemMenuSearchTitle"),
        Description = Lang.Get("ExpandItemMenuSearchDescription"),
        Category    = ModuleCategory.Interface,
        Author      = ["HSS"]
    };

    public override ModulePermission Permission { get; } = new() { AllDefaultEnabled = true };

    private SearchMenuItemBase[] SearchMenuItems
    {
        get
        {
            if (field is { Length: > 0 }) return field;

            return field =
            [
                .. typeof(ExpandItemMenuSearch)
                   .GetNestedTypes(BindingFlags.NonPublic)
                   .Where(type => !type.IsAbstract && typeof(SearchMenuItemBase).IsAssignableFrom(type))
                   .Select
                   (type => (SearchMenuItemBase)Activator.CreateInstance
                    (
                        type,
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                        null,
                        [this],
                        null
                    )
                   )
            ];
        }
    }

    private Config config = null!;

    private readonly UpperContainerItem menu;
    private readonly ClickAllItem       clickAllMenu;

    public ExpandItemMenuSearch()
    {
        menu         = new(this);
        clickAllMenu = new(this);
    }

    protected override void Init()
    {
        config = Config.Load(this) ?? new();

        ContextMenuManager.Instance().Reg(menu);
    }

    protected override void Uninit() =>
        ContextMenuManager.Instance().Unreg(menu);

    protected override void ConfigUI()
    {
        using var heading = ImRaii.Heading1(Lang.Get("SearchPlatform"));

        foreach (var searchMenuItem in SearchMenuItems)
        {
            var value = config.SearchMenuEnabledStates
                              .GetValueOrDefault(searchMenuItem.ConfigKey, searchMenuItem.DefaultEnabled);

            if (ImGui.Checkbox(searchMenuItem.PlatformName, ref value))
            {
                config.SearchMenuEnabledStates[searchMenuItem.ConfigKey] = value;
                config.Save(this);
            }

        }

        ImGui.NewLine();

        if (ImGui.Checkbox(Lang.Get("ExpandItemMenuSearch-GlamourTakesPriority"), ref config.GlamourPrioritize))
            config.Save(this);
    }

    private sealed class Config : ModuleConfig
    {
        public bool                     GlamourPrioritize       = true;
        public Dictionary<string, bool> SearchMenuEnabledStates = [];
    }

    private sealed class UpperContainerItem
    (
        ExpandItemMenuSearch module
    ) : ContextMenuEntry
    {
        public override string Identifier => nameof(ExpandItemMenuSearch);

        public override ContextMenuItem? Create
        (
            ContextMenuOpenedArgs args
        )
        {
            if (!args.TargetItemRow.IsValid)
                return null;

            List<SearchMenuItemBase> searchItems = [];

            foreach (var searchMenuItem in module.SearchMenuItems)
            {
                if (!module.config.SearchMenuEnabledStates.GetValueOrDefault(searchMenuItem.ConfigKey, searchMenuItem.DefaultEnabled))
                    continue;

                if (!searchMenuItem.IsDisplay(args))
                    continue;

                searchItems.Add(searchMenuItem);
            }

            if (searchItems.Count == 0) return null;

            return new()
            {
                Name = Lang.Get("ExpandItemMenuSearch-ContextMenu-Name"),
                Submenu = new()
                {
                    Title = Lang.Get("ExpandItemMenuSearch-ContextMenu-Name"),
                    Entries = searchItems.Count == 1 ?
                                  searchItems :
                                  [.. searchItems, module.clickAllMenu]
                }
            };
        }
    }

    private sealed class ClickAllItem
    (
        ExpandItemMenuSearch module
    ) : ContextMenuEntry
    {
        public override string Identifier => nameof(ExpandItemMenuSearch);

        public override int? Priority => 1000;
        
        public override ContextMenuItem Create
        (
            ContextMenuOpenedArgs args
        )
        {
            using var rented  = new RentedSeStringBuilder();
            var       builder = rented.Builder;

            builder.PushEdgeColorType(AtkColors.ValueEmphasize.EdgeColor)
                   .PushColorType(AtkColors.ValueEmphasize.TextColor)
                   .Append(Lang.Get("ExpandItemMenuSearch-ContextMenu-SearchAll"))
                   .PopColorType()
                   .PopEdgeColorType();

            return new()
            {
                Name = builder.ToReadOnlySeString(),
                OnClicked = _ =>
                {
                    foreach (var searchMenuItem in module.SearchMenuItems)
                    {
                        if (!module.config.SearchMenuEnabledStates
                                   .GetValueOrDefault(searchMenuItem.ConfigKey, searchMenuItem.DefaultEnabled))
                            continue;

                        if (searchMenuItem.IsDisplay(args))
                            searchMenuItem.OnClicked(args);
                    }
                }
            };
        }
    }

    private abstract class SearchMenuItemBase
    (
        ExpandItemMenuSearch module
    ) : ContextMenuEntry
    {
        protected readonly ExpandItemMenuSearch module = module;

        public override string Identifier => nameof(ExpandItemMenuSearch);
        
        public abstract string PlatformName   { get; }
        public abstract string ConfigKey      { get; }
        public virtual  bool   DefaultEnabled => false;

        protected Item GetSearchItem
        (
            ContextMenuOpenedArgs args
        ) =>
            module.config.GlamourPrioritize && args.TargetGlamourRow is { RowId: > 0, IsValid: true } glamour ?
                glamour.Value :
                args.TargetItemRow.Value;

        public abstract void OnClicked
        (
            ContextMenuOpenedArgs args
        );

        public abstract bool IsDisplay
        (
            ContextMenuOpenedArgs args
        );

        public override ContextMenuItem Create
        (
            ContextMenuOpenedArgs args
        ) =>
            new()
            {
                Name      = Lang.Get("ExpandItemMenuSearch-ContextMenu-Search", PlatformName),
                OnClicked = clicked => OnClicked(clicked.Source)
            };
    }

    private sealed class RisingStonesItem
    (
        ExpandItemMenuSearch module
    ) : SearchMenuItemBase(module)
    {
        public override string PlatformName   => "石之家";
        public override string ConfigKey      => nameof(RisingStonesItem);
        public override bool   DefaultEnabled => GameState.IsCN;

        public override void OnClicked
        (
            ContextMenuOpenedArgs args
        ) =>
            Util.OpenLink(string.Format(URL, GetSearchItem(args).RowId));

        public override bool IsDisplay
        (
            ContextMenuOpenedArgs args
        ) =>
            GetSearchItem(args).EquipSlotCategory.RowId > 0;

        #region 常量

        private const string URL =
            "https://ff14risingstones.web.sdo.com/pc/index.html#/search?equipmentid={0}&section=glamour";

        #endregion
    }

    private sealed class HuijiWikiItem
    (
        ExpandItemMenuSearch module
    ) : SearchMenuItemBase(module)
    {
        public override string PlatformName   => "最终幻想 XIV 中文维基";
        public override string ConfigKey      => nameof(HuijiWikiItem);
        public override bool   DefaultEnabled => GameState.IsCN || GameState.IsTC;

        public override void OnClicked
        (
            ContextMenuOpenedArgs args
        ) =>
            Util.OpenLink(string.Format(URL, GetSearchItem(args).Name));

        public override bool IsDisplay
        (
            ContextMenuOpenedArgs args
        ) =>
            true;

        #region 常量

        private const string URL =
            "https://ff14.huijiwiki.com/wiki/%E7%89%A9%E5%93%81:{0}";

        #endregion
    }

    private sealed class UniversalisItem
    (
        ExpandItemMenuSearch module
    ) : SearchMenuItemBase(module)
    {
        public override string PlatformName   => "Universalis";
        public override string ConfigKey      => nameof(UniversalisItem);
        public override bool   DefaultEnabled => true;

        public override void OnClicked
        (
            ContextMenuOpenedArgs args
        ) =>
            Util.OpenLink(string.Format(URL, GetSearchItem(args).RowId));

        public override bool IsDisplay
        (
            ContextMenuOpenedArgs args
        ) =>
            GetSearchItem(args).ItemSearchCategory.RowId > 0;

        #region 常量

        private const string URL =
            "https://universalis.app/market/{0}";

        #endregion
    }

    private sealed class TeamCraftItem
    (
        ExpandItemMenuSearch module
    ) : SearchMenuItemBase(module)
    {
        public override string PlatformName   => "TeamCraft";
        public override string ConfigKey      => nameof(TeamCraftItem);
        public override bool   DefaultEnabled => true;

        public override void OnClicked
        (
            ContextMenuOpenedArgs args
        ) =>
            Util.OpenLink(string.Format(URL, GetVariant(), GetSearchItem(args).RowId));

        public override bool IsDisplay
        (
            ContextMenuOpenedArgs args
        ) => true;

        private static string GetVariant() =>
            GameState.ClientLanguge switch
            {
                Language.Japanese           => "ja",
                Language.English            => "en",
                Language.French             => "fr",
                Language.German             => "de",
                Language.ChineseSimplified  => "zh",
                Language.ChineseTraditional => "zh",
                Language.TraditionalChinese => "tw",
                Language.Korean             => "ko",
                _                           => "en"
            };

        #region 常量

        private const string URL =
            "https://ffxivteamcraft.com/db/{0}/item/{1}";

        #endregion
    }

    private sealed class ConsoleGameWikiItem
    (
        ExpandItemMenuSearch module
    ) : SearchMenuItemBase(module)
    {
        public override string PlatformName   => "Console Games Wiki";
        public override string ConfigKey      => nameof(ConsoleGameWikiItem);
        public override bool   DefaultEnabled => GameState.IsGL;

        public override void OnClicked
        (
            ContextMenuOpenedArgs args
        ) =>
            Util.OpenLink(string.Format(URL, Uri.EscapeDataString(GetSearchItem(args).Name.ToString())));

        public override bool IsDisplay
        (
            ContextMenuOpenedArgs args
        ) => true;

        #region 常量

        private const string URL =
            "https://ffxiv.consolegameswiki.com/mediawiki/index.php?search={0}&title=Special%3ASearch&go=%E5%89%8D%E5%BE%80";

        #endregion
    }

    private sealed class GarlandToolsCNItem
    (
        ExpandItemMenuSearch module
    ) : SearchMenuItemBase(module)
    {
        public override string PlatformName   => "Garland Tools 国服站";
        public override string ConfigKey      => nameof(GarlandToolsCNItem);
        public override bool   DefaultEnabled => GameState.IsCN || GameState.IsTC;

        public override void OnClicked
        (
            ContextMenuOpenedArgs args
        ) =>
            Util.OpenLink(string.Format(URL, GetSearchItem(args).RowId));

        public override bool IsDisplay
        (
            ContextMenuOpenedArgs args
        ) => true;

        #region 常量

        private const string URL =
            "https://www.garlandtools.cn/db/#item/{0}";

        #endregion
    }

    private sealed class GarlandToolsItem
    (
        ExpandItemMenuSearch module
    ) : SearchMenuItemBase(module)
    {
        public override string PlatformName   => "Garland Tools";
        public override string ConfigKey      => nameof(GarlandToolsItem);
        public override bool   DefaultEnabled => GameState.IsGL;

        public override void OnClicked
        (
            ContextMenuOpenedArgs args
        ) =>
            Util.OpenLink(string.Format(URL, GetSearchItem(args).RowId));

        public override bool IsDisplay
        (
            ContextMenuOpenedArgs args
        ) => true;

        #region 常量

        private const string URL =
            "https://www.garlandtools.org/db/#item/{0}";

        #endregion
    }

    private sealed class LodestoneItem
    (
        ExpandItemMenuSearch module
    ) : SearchMenuItemBase(module)
    {
        public override string PlatformName   => "Lodestone";
        public override string ConfigKey      => nameof(LodestoneItem);
        public override bool   DefaultEnabled => GameState.IsGL;

        public override void OnClicked
        (
            ContextMenuOpenedArgs args
        ) =>
            Util.OpenLink(string.Format(URL, GetSearchItem(args).Name, GetPrefix()));

        public override bool IsDisplay
        (
            ContextMenuOpenedArgs args
        ) => true;

        private static string GetPrefix() =>
            GameState.ClientLanguge switch
            {
                Language.Japanese => "jp",
                Language.French   => "fr",
                Language.German   => "de",
                _                 => "na"
            };

        #region 常量

        private const string URL =
            "https://{1}.finalfantasyxiv.com/lodestone/playguide/db//search/?patch=&db_search_category=&q={0}";

        #endregion
    }

    private sealed class GamerEscapeItem
    (
        ExpandItemMenuSearch module
    ) : SearchMenuItemBase(module)
    {
        public override string PlatformName   => "Gamer Escape";
        public override string ConfigKey      => nameof(GamerEscapeItem);
        public override bool   DefaultEnabled => GameState.IsGL;

        public override void OnClicked
        (
            ContextMenuOpenedArgs args
        ) =>
            Util.OpenLink(string.Format(URL, Uri.EscapeDataString(GetSearchItem(args).Name.ToString())));

        public override bool IsDisplay
        (
            ContextMenuOpenedArgs args
        ) => true;

        #region 常量

        private const string URL =
            "https://ffxiv.gamerescape.com/?search={0}";

        #endregion
    }

    private sealed class ERIONESItem
    (
        ExpandItemMenuSearch module
    ) : SearchMenuItemBase(module)
    {
        public override string PlatformName   => "ERIONES";
        public override string ConfigKey      => nameof(ERIONESItem);
        public override bool   DefaultEnabled => GameState.IsGL;

        public override void OnClicked
        (
            ContextMenuOpenedArgs args
        )
        {
            var itemName = GetSearchItem(args).Name.ToString();

            if (!string.IsNullOrWhiteSpace(itemName))
            {
                if (itemName.Length > 25)
                    itemName = itemName[..25];

                Util.OpenLink(string.Format(URL, GetPrefixByLang(), Uri.EscapeDataString(itemName)));
            }
        }

        public override bool IsDisplay
        (
            ContextMenuOpenedArgs args
        ) => true;

        private static string GetPrefixByLang() =>
            GameState.ClientLanguge switch
            {
                Language.English            => "en.",
                Language.French             => "fr.",
                Language.German             => "de.",
                Language.ChineseSimplified  => "cn.",
                Language.ChineseTraditional => "cn.",
                Language.TraditionalChinese => "cn.",
                Language.Korean             => "ko.",
                _                           => string.Empty
            };

        #region 常量

        private const string URL =
            "https://{0}eriones.com/search?i={1}";

        #endregion
    }

    private sealed class FFXIVItemSearchTCItem
    (
        ExpandItemMenuSearch module
    ) : SearchMenuItemBase(module)
    {
        public override string PlatformName   => "FFXIV 繁中物品搜尋站";
        public override string ConfigKey      => nameof(FFXIVItemSearchTCItem);
        public override bool   DefaultEnabled => GameState.IsTC;

        public override void OnClicked
        (
            ContextMenuOpenedArgs args
        )
        {
            var item = GetSearchItem(args);
            Util.OpenLink(string.Format(URL, item.RowId, Uri.EscapeDataString(item.Name.ToString())));
        }

        public override bool IsDisplay
        (
            ContextMenuOpenedArgs args
        ) => true;

        #region 常量

        private const string URL =
            "https://cycleapple.github.io/ffxiv-item-search-tc?selected={0}&q={1}";

        #endregion
    }
}
