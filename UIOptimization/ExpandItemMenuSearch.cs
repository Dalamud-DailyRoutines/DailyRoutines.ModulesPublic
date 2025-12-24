using System;
using System.Collections.Generic;
using DailyRoutines.Abstracts;
using DailyRoutines.Infos;
using Dalamud.Game.Gui.ContextMenu;
using Dalamud.Utility;

namespace DailyRoutines.ModulesPublic;

public class ExpandItemMenuSearch : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("ExpandItemMenuSearchTitle"),
        Description = GetLoc("ExpandItemMenuSearchDescription"),
        Category    = ModuleCategories.UIOptimization,
        Author      = ["HSS"]
    };
    
    public override ModulePermission Permission { get; } = new() { AllDefaultEnabled = true };
    
    private static Config ModuleConfig = null!;

    private static readonly UpperContainerItem UpperContainerMenu = new();

    protected override void Init()
    {
        ModuleConfig = LoadConfig<Config>() ?? new();

        DService.ContextMenu.OnMenuOpened += OnMenuOpened;
    }

    protected override void ConfigUI()
    {
        RenderCheckbox(GetLoc("ExpandItemMenuSearch-SearchHuijiWiki"),        ref ModuleConfig.HuijiWikiEnabled);
        RenderCheckbox(GetLoc("ExpandItemMenuSearch-SearchConsoleGamesWiki"), ref ModuleConfig.ConsoleGamesWikiEnabled);
        RenderCheckbox(GetLoc("ExpandItemMenuSearch-SearchFFXIVSC"),          ref ModuleConfig.FFXIVSCEnabled);
        RenderCheckbox(GetLoc("ExpandItemMenuSearch-SearchGarlandToolsDBCN"), ref ModuleConfig.GarlandToolsDBCNEnabled);
        RenderCheckbox(GetLoc("ExpandItemMenuSearch-SearchGarlandToolsDB"),   ref ModuleConfig.GarlandToolsDBEnabled);
        RenderCheckbox(GetLoc("ExpandItemMenuSearch-SearchLodestoneDB"),      ref ModuleConfig.LodestoneDBEnabled);
        RenderCheckbox(GetLoc("ExpandItemMenuSearch-SearchGamerEscapeWiki"),  ref ModuleConfig.GamerEscapeWikiEnabled);
        RenderCheckbox(GetLoc("ExpandItemMenuSearch-SearchERIONES"),          ref ModuleConfig.ERIONESEnabled);
        
        ImGui.Separator();
        RenderCheckbox(GetLoc("ExpandItemMenuSearch-GlamourTakesPriority"),
                       ref ModuleConfig.GlamourPrioritize);
    }
    
    private void RenderCheckbox(string label, ref bool value)
    {
        if (ImGui.Checkbox(label, ref value))
            SaveConfig(ModuleConfig);
    }

    #region 右键菜单处理

    private static void OnMenuOpened(IMenuOpenedArgs args)
    {
        // 检查是否有有效的物品ID
        if (!ContextMenuItemManager.IsValidItem) return;
        
        // 添加菜单项
        AddContextMenuItemsByConfig(args);
    }

    private static void AddContextMenuItemsByConfig(IMenuOpenedArgs args)
    {
        var shouldProcess = ModuleConfig.HuijiWikiEnabled      || ModuleConfig.GamerEscapeWikiEnabled  ||
                           ModuleConfig.LodestoneDBEnabled    || ModuleConfig.ConsoleGamesWikiEnabled ||
                           ModuleConfig.GarlandToolsDBEnabled || ModuleConfig.GarlandToolsDBCNEnabled ||
                           ModuleConfig.ERIONESEnabled        || ModuleConfig.FFXIVSCEnabled;
        
        if (shouldProcess)
            args.AddMenuItem(UpperContainerMenu.Get());
    }

    #endregion

    protected override void Uninit() => 
        DService.ContextMenu.OnMenuOpened -= OnMenuOpened;

    private class Config : ModuleConfiguration
    {
        // 优先搜索幻化
        public bool GlamourPrioritize = true;

        public bool FFXIVSCEnabled          = GameState.IsCN || GameState.IsTC;
        public bool HuijiWikiEnabled        = GameState.IsCN || GameState.IsTC;
        public bool ConsoleGamesWikiEnabled = GameState.IsGL;
        public bool GarlandToolsDBCNEnabled = GameState.IsCN || GameState.IsTC;
        public bool GarlandToolsDBEnabled   = GameState.IsGL;
        public bool LodestoneDBEnabled      = GameState.IsGL;
        public bool GamerEscapeWikiEnabled  = GameState.IsGL;
        public bool ERIONESEnabled          = GameState.IsGL;
    }

    private class UpperContainerItem : MenuItemBase
    {
        public override string Name       { get; protected set; } = GetLoc("ExpandItemMenuSearch-SearchTitle");
        public override string Identifier { get; protected set; } = nameof(ExpandItemMenuSearch);
        
        protected override bool WithDRPrefix { get; set; } = true;
        protected override bool IsSubmenu    { get; set; } = true;

        private static readonly FFXIVSCItem          FFXIVSCMenu          = new();
        private static readonly HuijiWikiItem        HuijiWikiMenu        = new();
        private static readonly ConsoleGameWikiItem  ConsoleGameWikiMenu  = new();
        private static readonly GarlandToolsDBCNItem GarlandToolsDBCNMenu = new();
        private static readonly GarlandToolsDBItem   GarlandToolsDBMenu   = new();
        private static readonly LodestoneDBItem      LodestoneDBMenu      = new();
        private static readonly GamerEscapeWikiItem  GamerEscapeWikiMenu  = new();
        private static readonly ERIONESItem          ERIONESMenu          = new();
        
        protected override void OnClicked(IMenuItemClickedArgs args) => 
            args.OpenSubmenu(Name, ProcessMenuItems());

        private static List<MenuItem> ProcessMenuItems()
        {
            var list = new List<MenuItem>();
            
            ProcessMenuItem(ModuleConfig.HuijiWikiEnabled,        HuijiWikiMenu);
            ProcessMenuItem(ModuleConfig.ConsoleGamesWikiEnabled, ConsoleGameWikiMenu);
            ProcessMenuItem(ModuleConfig.GarlandToolsDBCNEnabled, GarlandToolsDBCNMenu);
            ProcessMenuItem(ModuleConfig.GarlandToolsDBEnabled,   GarlandToolsDBMenu);
            ProcessMenuItem(ModuleConfig.FFXIVSCEnabled,          FFXIVSCMenu);
            ProcessMenuItem(ModuleConfig.LodestoneDBEnabled,      LodestoneDBMenu);
            ProcessMenuItem(ModuleConfig.GamerEscapeWikiEnabled,  GamerEscapeWikiMenu);
            ProcessMenuItem(ModuleConfig.ERIONESEnabled,          ERIONESMenu);

            return list;

            void ProcessMenuItem(bool config, MenuItemBase item)
            {
                if (!config) return;
            
                list.Add(item.Get());
            }
        }
    }
    
    // 光之收藏家
    private class FFXIVSCItem : MenuItemBase
    {
        public override string Name       { get; protected set; } = GetLoc("ExpandItemMenuSearch-SearchFFXIVSC");
        public override string Identifier { get; protected set; } = nameof(ExpandItemMenuSearch);

        private const string Url = "https://v1.ffxivsc.cn/#/search?text={0}&type=armor";

        protected override void OnClicked(IMenuItemClickedArgs args)
        {
            var itemName = string.Empty;

            // 优先使用幻化物品名称（如果配置了优先幻化且有幻化物品）
            if (ModuleConfig.GlamourPrioritize && ContextMenuItemManager.CurrentGlamourID > 0)
                itemName = ContextMenuItemManager.CurrentGlamourItem?.Name.ExtractText();
            else
                itemName = ContextMenuItemManager.CurrentItem?.Name.ExtractText();

            if (!string.IsNullOrWhiteSpace(itemName))
                Util.OpenLink(string.Format(Url, itemName));
        }
    }

    // 最终幻想 14 中文维基
    private class HuijiWikiItem : MenuItemBase
    {
        public override string Name       { get; protected set; } = GetLoc("ExpandItemMenuSearch-SearchHuijiWiki");
        public override string Identifier { get; protected set; } = nameof(ExpandItemMenuSearch);

        private const string Url = "https://ff14.huijiwiki.com/wiki/%E7%89%A9%E5%93%81:{0}";

        protected override void OnClicked(IMenuItemClickedArgs args)
        {
            var itemName = string.Empty;

            // 优先使用幻化物品名称（如果配置了优先幻化且有幻化物品）
            if (ModuleConfig.GlamourPrioritize && ContextMenuItemManager.CurrentGlamourID > 0)
                itemName = ContextMenuItemManager.CurrentGlamourItem?.Name.ExtractText();
            else
                itemName = ContextMenuItemManager.CurrentItem?.Name.ExtractText();

            if (!string.IsNullOrWhiteSpace(itemName))
                Util.OpenLink(string.Format(Url, itemName));
        }
    }

    // Console Games Wiki
    private class ConsoleGameWikiItem : MenuItemBase
    {
        public override string Name       { get; protected set; } = GetLoc("ExpandItemMenuSearch-SearchConsoleGamesWiki");
        public override string Identifier { get; protected set; } = nameof(ExpandItemMenuSearch);

        private const string Url =
            "https://ffxiv.consolegameswiki.com/mediawiki/index.php?search={0}&title=Special%3ASearch&go=%E5%89%8D%E5%BE%80";

        protected override void OnClicked(IMenuItemClickedArgs args)
        {
            var itemName = string.Empty;

            // 优先使用幻化物品名称（如果配置了优先幻化且有幻化物品）
            if (ModuleConfig.GlamourPrioritize && ContextMenuItemManager.CurrentGlamourID > 0)
                itemName = ContextMenuItemManager.CurrentGlamourItem?.Name.ExtractText();
            else
                itemName = ContextMenuItemManager.CurrentItem?.Name.ExtractText();

            if (!string.IsNullOrWhiteSpace(itemName))
                Util.OpenLink(string.Format(Url, Uri.EscapeDataString(itemName)));
        }
    }

    // Garland Tools DB (国服)
    private class GarlandToolsDBCNItem : MenuItemBase
    {
        public override string Name       { get; protected set; } = GetLoc("ExpandItemMenuSearch-SearchGarlandToolsDBCN");
        public override string Identifier { get; protected set; } = nameof(ExpandItemMenuSearch);

        private const string Url =
            "https://www.garlandtools.cn/db/#item/{0}";

        protected override void OnClicked(IMenuItemClickedArgs args)
        {
            var itemID = 0U;

            // 优先使用幻化物品ID（如果配置了优先幻化且有幻化物品）
            if (ModuleConfig.GlamourPrioritize && ContextMenuItemManager.CurrentGlamourID > 0)
                itemID = ContextMenuItemManager.CurrentGlamourID;
            else
                itemID = ContextMenuItemManager.CurrentItemID;

            if (itemID != 0)
                Util.OpenLink(string.Format(Url, itemID));
        }
    }

    // Garland Tools DB (国服)
    private class GarlandToolsDBItem : MenuItemBase
    {
        public override string Name       { get; protected set; } = GetLoc("ExpandItemMenuSearch-SearchGarlandToolsDB");
        public override string Identifier { get; protected set; } = nameof(ExpandItemMenuSearch);

        private const string Url =
            "https://www.garlandtools.org/db/#item/{0}";

        protected override void OnClicked(IMenuItemClickedArgs args)
        {
            var itemID = 0U;

            // 优先使用幻化物品ID（如果配置了优先幻化且有幻化物品）
            if (ModuleConfig.GlamourPrioritize && ContextMenuItemManager.CurrentGlamourID > 0)
                itemID = ContextMenuItemManager.CurrentGlamourID;
            else
                itemID = ContextMenuItemManager.CurrentItemID;

            if (itemID != 0)
                Util.OpenLink(string.Format(Url, itemID));
        }
    }
    
    // Lodestone DB
    private class LodestoneDBItem : MenuItemBase
    {
        public override string Name       { get; protected set; } = GetLoc("ExpandItemMenuSearch-SearchLodestoneDB");
        public override string Identifier { get; protected set; } = nameof(ExpandItemMenuSearch);

        private const string Url =
            "https://na.finalfantasyxiv.com/lodestone/playguide/db//search/?patch=&db_search_category=&q={0}";

        protected override void OnClicked(IMenuItemClickedArgs args)
        {
            var itemName = string.Empty;

            // 优先使用幻化物品名称（如果配置了优先幻化且有幻化物品）
            if (ModuleConfig.GlamourPrioritize && ContextMenuItemManager.CurrentGlamourID > 0)
                itemName = ContextMenuItemManager.CurrentGlamourItem?.Name.ExtractText();
            else
                itemName = ContextMenuItemManager.CurrentItem?.Name.ExtractText();

            if (!string.IsNullOrWhiteSpace(itemName))
                Util.OpenLink(string.Format(Url, itemName));
        }
    }
    
    // Gamer Escape Wiki
    private class GamerEscapeWikiItem : MenuItemBase
    {
        public override string Name       { get; protected set; } = GetLoc("ExpandItemMenuSearch-SearchGamerEscapeWiki");
        public override string Identifier { get; protected set; } = nameof(ExpandItemMenuSearch);

        private const string Url =
            "https://ffxiv.gamerescape.com/?search={0}";

        protected override void OnClicked(IMenuItemClickedArgs args)
        {
            var itemName = string.Empty;

            // 优先使用幻化物品名称（如果配置了优先幻化且有幻化物品）
            if (ModuleConfig.GlamourPrioritize && ContextMenuItemManager.CurrentGlamourID > 0)
                itemName = ContextMenuItemManager.CurrentGlamourItem?.Name.ExtractText();
            else
                itemName = ContextMenuItemManager.CurrentItem?.Name.ExtractText();

            if (!string.IsNullOrWhiteSpace(itemName))
                Util.OpenLink(string.Format(Url, Uri.EscapeDataString(itemName)));
        }
    }
    
    // ERIONES DB
    private class ERIONESItem : MenuItemBase
    {
        public override string Name       { get; protected set; } = GetLoc("ExpandItemMenuSearch-SearchERIONES");
        public override string Identifier { get; protected set; } = nameof(ExpandItemMenuSearch);

        private const string Url = "https://{0}eriones.com/search?i={1}";

        protected override void OnClicked(IMenuItemClickedArgs args)
        {
            var itemName = string.Empty;

            // 优先使用幻化物品名称（如果配置了优先幻化且有幻化物品）
            if (ModuleConfig.GlamourPrioritize && ContextMenuItemManager.CurrentGlamourID > 0)
                itemName = ContextMenuItemManager.CurrentGlamourItem?.Name.ExtractText();
            else
                itemName = ContextMenuItemManager.CurrentItem?.Name.ExtractText();
            
            if (!string.IsNullOrWhiteSpace(itemName))
            {
                if (itemName.Length > 25) 
                    itemName = itemName[..25];
                Util.OpenLink(string.Format(Url, GetPrefixByLang(), Uri.EscapeDataString(itemName)));
            }
        }
        
        private static string GetPrefixByLang() =>
            DService.ClientState.ClientLanguage switch
            {
                ClientLanguage.English  => "en.",
                ClientLanguage.Japanese => string.Empty,
                ClientLanguage.French   => "fr.",
                ClientLanguage.German   => "de.",
                (ClientLanguage)4       => "cn.",
                (ClientLanguage)5       => "kr.",
                _                       => string.Empty
            };
    }
}
