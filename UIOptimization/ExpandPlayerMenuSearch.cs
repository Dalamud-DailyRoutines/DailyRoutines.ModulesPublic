using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DailyRoutines.Abstracts;
using DailyRoutines.Helpers;
using DailyRoutines.Infos;
using DailyRoutines.Managers;
using Dalamud.Game.Gui.ContextMenu;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Lumina.Excel.Sheets;
using Newtonsoft.Json;
using Action = System.Action;

namespace DailyRoutines.ModulesPublic;

public class ExpandPlayerMenuSearch : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("ExpandPlayerMenuSearchTitle"),
        Description = GetLoc("ExpandPlayerMenuSearchDescription"),
        Category    = ModuleCategories.UIOptimization,
    };

    public override ModulePermission Permission { get; } = new() { AllDefaultEnabled = true };

    private static Config ModuleConfig = null!;
    
    private static readonly UpperContainerItem UpperContainerMenu = new();

    private static CancellationTokenSource? CancelSource;

    private static CharacterSearchInfo? TargetChara;

    protected override void Init()
    {
        ModuleConfig = LoadConfig<Config>() ?? new();
        
        CancelSource ??= new();

        DService.ContextMenu.OnMenuOpened += OnMenuOpen;
    }

    protected override void ConfigUI() => 
        UpperContainerItem.Draw();

    private static void OnMenuOpen(IMenuOpenedArgs args)
    {
        if (!IsValidAddon(args)) return;
        if (args.MenuType != ContextMenuType.Default) return;

        args.AddMenuItem(UpperContainerMenu.Get());
    }

    private static unsafe bool IsValidAddon(IMenuArgs args)
    {
        if (args.Target is MenuTargetInventory) return false;
        var menuTarget = (MenuTargetDefault)args.Target;
        var agent = DService.Gui.FindAgentInterface("ChatLog");
        if (agent != nint.Zero && *(uint*)(agent + 0x948 + 8) == 3) return false;

        var judgeCriteria0 = menuTarget.TargetCharacter != null;
        var judgeCriteria1 = !string.IsNullOrWhiteSpace(menuTarget.TargetName) &&
                             menuTarget.TargetHomeWorld.ValueNullable != null &&
                             menuTarget.TargetHomeWorld.Value.RowId != 0;

        var judgeCriteria2 = menuTarget.TargetObject != null && IGameObject.Create(menuTarget.TargetObject.Address) is ICharacter && judgeCriteria1;

        switch (args.AddonName)
        {
            default:
                return false;
            case "BlackList":
                var agentBlackList = AgentBlacklist.Instance();

                if ((nint)agentBlackList != nint.Zero && agentBlackList->AgentInterface.IsAgentActive())
                {
                    var playerName = agentBlackList->SelectedPlayerName.ExtractText();
                    var serverName = agentBlackList->SelectedPlayerFullName.ExtractText()
                                                                           .TrimStart(playerName.ToCharArray());

                    TargetChara = new()
                    {
                        Name  = playerName,
                        World = serverName,
                        WorldID = LuminaGetter.Get<World>().FirstOrDefault(x => x.Name.ExtractText().Contains(serverName, StringComparison.OrdinalIgnoreCase)).RowId
                    };
                    return true;
                }

                return false;
            case "FreeCompany":
                if (menuTarget.TargetContentId == 0) return false;

                TargetChara = new()
                {
                    Name = menuTarget.TargetName, 
                    World = menuTarget.TargetHomeWorld.ValueNullable?.Name.ExtractText() ?? string.Empty,
                    WorldID = menuTarget.TargetHomeWorld.RowId
                };
                return true;
            case "LinkShell":
            case "CrossWorldLinkshell":
                return menuTarget.TargetContentId != 0 && GeneralJudge();
            case null:
            case "ChatLog":
            case "LookingForGroup":
            case "PartyMemberList":
            case "FriendList":
            case "SocialList":
            case "ContactList":
            case "_PartyList":
            case "BeginnerChatList":
            case "ContentMemberList":
                return GeneralJudge();
        }

        bool GeneralJudge()
        {
            if (judgeCriteria0)
            {
                TargetChara = new CharacterSearchInfo()
                {
                    Name    = menuTarget.TargetCharacter.Name,
                    World   = menuTarget.TargetCharacter.HomeWorld.ValueNullable?.Name.ExtractText(),
                    WorldID = menuTarget.TargetCharacter.HomeWorld.RowId
                };
            }
            else if (menuTarget.TargetObject != null && IGameObject.Create(menuTarget.TargetObject.Address) is ICharacter chara && judgeCriteria1)
            {
                TargetChara = new CharacterSearchInfo()
                {
                    Name    = chara.Name.ExtractText(),
                    World   = LuminaGetter.GetRow<World>(((Character*)chara.Address)->HomeWorld)?.Name.ExtractText() ?? string.Empty,
                    WorldID = ((Character*)chara.Address)->HomeWorld
                };
            }
            else if (judgeCriteria1)
            {
                TargetChara = new()
                {
                    Name = menuTarget.TargetName, 
                    World = menuTarget.TargetHomeWorld.ValueNullable?.Name.ExtractText() ?? string.Empty,
                    WorldID = menuTarget.TargetHomeWorld.RowId
                };
            }

            return judgeCriteria0 || judgeCriteria2 || judgeCriteria1;
        }
    }

    protected override void Uninit()
    {
        DService.ContextMenu.OnMenuOpened -= OnMenuOpen;
        
        CancelSource?.Cancel();
        CancelSource?.Dispose();
        CancelSource = null;
    }

    public class CharacterSearchInfo
    {
        public string Name    { get; init; } = null!;
        public string World   { get; init; } = null!;
        public uint   WorldID { get; init; }
    }

    private class Config : ModuleConfiguration
    {
        public bool RisingStoneEnabled     = GameState.IsCN;
        public bool TiebaEnabled           = GameState.IsCN;
        public bool FFLogsEnabled          = true;
        public bool LodestoneEnabled       = GameState.IsGL;
        public bool LalachievementsEnabled = GameState.IsGL;
        public bool TomestoneEnabled       = GameState.IsGL;
        public bool SuMemoEnabled          = true;
    }

    private class UpperContainerItem : MenuItemBase
    {
        public override string Name       { get; protected set; } = GetLoc("ExpandPlayerMenuSearch-SearchTitle");
        public override string Identifier { get; protected set; } = nameof(ExpandPlayerMenuSearch);


        protected override bool WithDRPrefix { get; set; } = true;
        protected override bool IsSubmenu    { get; set; } = true;

        private static readonly List<(Func<bool> Config, Action SetConfig, string LocKey, MenuItemBase Item)> MenuItems =
        [
            new(() => ModuleConfig.RisingStoneEnabled, () => ModuleConfig.RisingStoneEnabled ^= true, 
                "ExpandPlayerMenuSearch-SearchRisingStone", new RisingStoneItem()),
            new(() => ModuleConfig.TiebaEnabled, () => ModuleConfig.TiebaEnabled ^= true, 
                "ExpandPlayerMenuSearch-SearchTieba", new TiebaItem()),
            new(() => ModuleConfig.FFLogsEnabled, () => ModuleConfig.FFLogsEnabled ^= true, 
                "ExpandPlayerMenuSearch-SearchFFLogs", new FFLogsItem()),
            new(() => ModuleConfig.LodestoneEnabled, () => ModuleConfig.LodestoneEnabled ^= true, 
                "ExpandPlayerMenuSearch-SearchLodestone", new LodestoneItem()),
            new(() => ModuleConfig.LalachievementsEnabled, () => ModuleConfig.LalachievementsEnabled ^= true, 
                "ExpandPlayerMenuSearch-SearchLalachievements", new LalachievementsItem()),
            new(() => ModuleConfig.TomestoneEnabled, () => ModuleConfig.TomestoneEnabled ^= true,
                "ExpandPlayerMenuSearch-SearchTomestone", new TomestoneItem()),
            new(() => ModuleConfig.SuMemoEnabled, () => ModuleConfig.SuMemoEnabled ^= true,
                "ExpandPlayerMenuSearch-SearchSuMemo", new SuMemoItem())
        ];
        
        private static readonly ClickAllItem ClickAllMenu = new();
        
        protected override void OnClicked(IMenuItemClickedArgs args) 
            => args.OpenSubmenu(Name, ProcessMenuItems());

        private static List<MenuItem> ProcessMenuItems()
        {
            var list = new List<MenuItem> { ClickAllMenu.Get() };
            
            foreach (var item in MenuItems)
            {
                if (!item.Config()) continue;
                list.Add(item.Item.Get());
            }
            
            return list;
        }

        internal static void ClickAll(IMenuItemClickedArgs args)
        {
            foreach (var item in MenuItems)
            {
                if (!item.Config()) continue;
                item.Item.ManuallyClick(args);
            }
        }

        internal static void Draw()
        {
            foreach (var menuItem in MenuItems)
            {
                var value = menuItem.Config();
                if (ImGui.Checkbox(GetLoc(menuItem.LocKey), ref value))
                {
                    menuItem.SetConfig();
                    ModuleConfig.Save(ModuleManager.GetModule<ExpandPlayerMenuSearch>());
                }
            }
        }
    }

    private class ClickAllItem : MenuItemBase
    {
        public override string Name       { get; protected set; } = GetLoc("ExpandPlayerMenuSearch-SearchInAllPlatforms");
        public override string Identifier { get; protected set; } = nameof(ExpandPlayerMenuSearch);


        protected override void OnClicked(IMenuItemClickedArgs args) => 
            UpperContainerItem.ClickAll(args);
    }

    private class RisingStoneItem : MenuItemBase
    {
        public override string Name       { get ; protected set ; } = GetLoc("ExpandPlayerMenuSearch-SearchRisingStone");
        public override string Identifier { get;  protected set; }  = nameof(ExpandPlayerMenuSearch);


        private const string SearchAPI =
            "https://apiff14risingstones.web.sdo.com/api/common/search?type=6&keywords={0}&page={1}&limit=50";
        private const string PlayerInfo = "https://ff14risingstones.web.sdo.com/pc/index.html#/me/info?uuid={0}";
        
        protected override void OnClicked(IMenuItemClickedArgs args)
        {
            DService.Framework.RunOnTick(async () =>
            {
                if (TargetChara == null) return;

                var page = 1;
                var isFound = false;
                const int delayBetweenRequests = 1000;

                while (!isFound)
                {
                    var url      = string.Format(SearchAPI, TargetChara.Name, page);
                    var response = await HttpClientHelper.Get().GetStringAsync(url);
                    var result   = JsonConvert.DeserializeObject<JsonFileFormat.RSPlayerSearchResult>(response);

                    if (result.data.Count == 0)
                    {
                        NotificationError(GetLoc("ExpandPlayerMenuSearch-PlayerInfoNotFound"));
                        break;
                    }

                    foreach (var player in result.data)
                    {
                        if (player.character_name == TargetChara.Name && player.group_name == TargetChara.World)
                        {
                            var uuid = player.uuid;
                            Util.OpenLink(string.Format(PlayerInfo, uuid));
                            isFound = true;
                            break;
                        }
                    }

                    if (!isFound)
                    {
                        await Task.Delay(delayBetweenRequests);
                        page++;
                    }
                    else break;
                }
            }, cancellationToken: CancelSource.Token);
        }
    }

    private class TiebaItem : MenuItemBase
    {
        public override string Name       { get; protected set; } = GetLoc("ExpandPlayerMenuSearch-SearchTieba");
        public override string Identifier { get; protected set; } = nameof(ExpandPlayerMenuSearch);


        private const string Url = "https://tieba.baidu.com/f/search/res?ie=utf-8&kw=ff14&qw={0}";

        protected override void OnClicked(IMenuItemClickedArgs args)
        {
            if (TargetChara == null) return;
            Util.OpenLink(string.Format(Url, $"{TargetChara.Name}@{TargetChara.World}"));
        }
    }

    private class FFLogsItem : MenuItemBase
    {
        public override string Name       { get; protected set; } = GetLoc("ExpandPlayerMenuSearch-SearchFFLogs");
        public override string Identifier { get; protected set; } = nameof(ExpandPlayerMenuSearch);


        private const string Url = "https://cn.fflogs.com/character/{0}/{1}/{2}";

        protected override void OnClicked(IMenuItemClickedArgs args)
        {
            if (TargetChara == null) return;
            var abbvr = RegionToFFLogsAbbvr(LuminaGetter.GetRow<World>(TargetChara.WorldID)?.DataCenter.ValueNullable?.Region ?? 0);
            Util.OpenLink(string.Format(Url, abbvr, TargetChara.World, TargetChara.Name));
        }

        private static string RegionToFFLogsAbbvr(uint region) =>
            region switch
            {
                1 => "JP",
                2 => "NA",
                3 => "EU",
                4 => "OC",
                5 => "CN",
                6 => "KR",
                _ => "CN"
            };

    }

    private class LodestoneItem : MenuItemBase
    {
        public override string Name       { get; protected set; } = GetLoc("ExpandPlayerMenuSearch-SearchLodestone");
        public override string Identifier { get; protected set; } = nameof(ExpandPlayerMenuSearch);


        private const string Url =
            "https://na.finalfantasyxiv.com/lodestone/character/?q={0}&worldname=_dc_{1}&classjob=&race_tribe=&blog_lang=ja&blog_lang=en&blog_lang=de&blog_lang=fr&order=";

        protected override void OnClicked(IMenuItemClickedArgs args)
        {
            if (TargetChara == null) return;

            var dcName = LuminaGetter.GetRow<World>(TargetChara.WorldID)?.DataCenter.ValueNullable?.Name.ExtractText() ?? "";
            Util.OpenLink(string.Format(Url, TargetChara.Name.Replace(' ', '+'), dcName));
        }
    }
    
    private class LalachievementsItem : MenuItemBase
    {
        public override string Name       { get ; protected set ; } = GetLoc("ExpandPlayerMenuSearch-SearchLalachievements");
        public override string Identifier { get;  protected set; }  = nameof(ExpandPlayerMenuSearch);


        private const string SearchAPI  = "https://www.lalachievements.com/api/charsearch/{0}/";
        private const string PlayerInfo = "https://www.lalachievements.com/char/{0}/";

        protected override void OnClicked(IMenuItemClickedArgs args) =>
            DService.Framework.RunOnTick(async () =>
            {
                if (TargetChara == null) return;

                var url      = string.Format(SearchAPI, TargetChara.Name);
                var response = await HttpClientHelper.Get().GetStringAsync(url);
                var result   = JsonConvert.DeserializeObject<JsonFileFormat.LLAPlayerSearchResult>(response);

                if (result.Data.Count == 0)
                {
                    NotificationError(GetLoc("ExpandPlayerMenuSearch-PlayerInfoNotFound"));
                    return;
                }

                foreach (var player in result.Data)
                {
                    if (player.CharacterName == TargetChara.Name && player.WorldID == TargetChara.WorldID)
                    {
                        Util.OpenLink(string.Format(PlayerInfo, player.CharacterID));
                        break;
                    }
                }
            }, TimeSpan.Zero, 0, CancelSource.Token);
    }
    
    private class TomestoneItem : MenuItemBase
    {
        public override string Name       { get ; protected set ; } = GetLoc("ExpandPlayerMenuSearch-SearchTomestone");
        public override string Identifier { get;  protected set; }  = nameof(ExpandPlayerMenuSearch);


        private const string SearchAPI  = "https://tomestone.gg/search/autocomplete?term={0}"; // 搜索词, 空格 %20

        protected override void OnClicked(IMenuItemClickedArgs args) =>
            DService.Framework.RunOnTick(async () =>
            {
                if (TargetChara == null) return;

                var url      = string.Format(SearchAPI, TargetChara.Name.Replace(" ", "%20"));
                var response = await HttpClientHelper.Get().GetStringAsync(url);
                
                dynamic? result   = JsonConvert.DeserializeObject(response);
                if (result == null) return;

                if (result.characters.Count == 0)
                {
                    NotificationError(GetLoc("ExpandPlayerMenuSearch-PlayerInfoNotFound"));
                    return;
                }

                foreach (var player in result.characters)
                {
                    string? refLink = player.href;
                    if (string.IsNullOrEmpty(refLink)) continue;
                    
                    var     info   = player.item;
                    string? name   = info.name;
                    string? server = info.serverName;

                    if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(server))
                        continue;
                    if (name != TargetChara.Name || !server.Contains(TargetChara.World, StringComparison.OrdinalIgnoreCase))
                        continue;
                    
                    Util.OpenLink($"https://tomestone.gg{refLink}");
                    break;
                }
            }, TimeSpan.Zero, 0, CancelSource.Token);
    }

    private class SuMemoItem : MenuItemBase
    {
        public override string Name       { get; protected set; } = GetLoc("ExpandPlayerMenuSearch-SearchSuMemo");
        public override string Identifier { get; protected set; } = nameof(ExpandPlayerMenuSearch);


        private const string Url = "https://fight.sumemo.dev/member/{0}@{1}";

        protected override void OnClicked(IMenuItemClickedArgs args)
        {
            if (TargetChara == null)
                return;
            Util.OpenLink(string.Format(Url, TargetChara.Name, TargetChara.World));
        }
    }
}
