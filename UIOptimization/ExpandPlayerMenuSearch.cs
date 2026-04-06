using DailyRoutines.Common.Info.Abstractions;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using DailyRoutines.Manager;
using Dalamud.Game.Gui.ContextMenu;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Lumina.Excel.Sheets;
using Newtonsoft.Json;
using OmenTools.Info.DTOs.Lalachievements;
using OmenTools.Info.DTOs.RisingStone;
using OmenTools.Interop.Game.Lumina;
using OmenTools.OmenService;
using Action = System.Action;
using MenuItem = Dalamud.Game.Gui.ContextMenu.MenuItem;
using NotifyHelper = OmenTools.OmenService.NotifyHelper;

namespace DailyRoutines.ModulesPublic;

public class ExpandPlayerMenuSearch : ModuleBase
{
    private static Config ModuleConfig = null!;

    private static readonly UpperContainerItem UpperContainerMenu = new();

    private static CancellationTokenSource? CancelSource;

    private static CharacterSearchInfo? TargetChara;

    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("ExpandPlayerMenuSearchTitle"),
        Description = Lang.Get("ExpandPlayerMenuSearchDescription"),
        Category    = ModuleCategory.UIOptimization
    };

    public override ModulePermission Permission { get; } = new() { AllDefaultEnabled = true };

    protected override void Init()
    {
        ModuleConfig = Config.Load(this) ?? new();

        CancelSource ??= new();

        DService.Instance().ContextMenu.OnMenuOpened += OnMenuOpen;
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
        var agent      = DService.Instance().GameGUI.FindAgentInterface("ChatLog");
        if (agent != nint.Zero && *(uint*)(agent + 0x948 + 8) == 3) return false;

        var judgeCriteria0 = menuTarget.TargetCharacter != null;
        var judgeCriteria1 = !string.IsNullOrWhiteSpace(menuTarget.TargetName) &&
                             menuTarget.TargetHomeWorld.ValueNullable != null  &&
                             menuTarget.TargetHomeWorld.Value.RowId   != 0;

        var judgeCriteria2 = menuTarget.TargetObject != null && IGameObject.Create(menuTarget.TargetObject.Address) is ICharacter && judgeCriteria1;

        switch (args.AddonName)
        {
            default:
                return false;
            case "BlackList":
                var agentBlackList = AgentBlacklist.Instance();

                if ((nint)agentBlackList != nint.Zero && agentBlackList->AgentInterface.IsAgentActive())
                {
                    var playerName = agentBlackList->SelectedPlayerName.ToString();
                    var serverName = agentBlackList->SelectedPlayerFullName.ToString()
                                                                           .TrimStart(playerName.ToCharArray());

                    TargetChara = new()
                    {
                        Name    = playerName,
                        World   = serverName,
                        WorldID = LuminaGetter.Get<World>().FirstOrDefault(x => x.Name.ToString().Contains(serverName, StringComparison.OrdinalIgnoreCase)).RowId
                    };
                    return true;
                }

                return false;
            case "FreeCompany":
                if (menuTarget.TargetContentId == 0) return false;

                TargetChara = new()
                {
                    Name    = menuTarget.TargetName,
                    World   = menuTarget.TargetHomeWorld.ValueNullable?.Name.ToString() ?? string.Empty,
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
                TargetChara = new CharacterSearchInfo
                {
                    Name    = menuTarget.TargetCharacter.Name,
                    World   = menuTarget.TargetCharacter.HomeWorld.ValueNullable?.Name.ToString(),
                    WorldID = menuTarget.TargetCharacter.HomeWorld.RowId
                };
            }
            else if (menuTarget.TargetObject != null && IGameObject.Create(menuTarget.TargetObject.Address) is ICharacter chara && judgeCriteria1)
            {
                TargetChara = new CharacterSearchInfo
                {
                    Name    = chara.Name.ToString(),
                    World   = LuminaGetter.GetRow<World>(((Character*)chara.Address)->HomeWorld)?.Name.ToString() ?? string.Empty,
                    WorldID = ((Character*)chara.Address)->HomeWorld
                };
            }
            else if (judgeCriteria1)
            {
                TargetChara = new()
                {
                    Name    = menuTarget.TargetName,
                    World   = menuTarget.TargetHomeWorld.ValueNullable?.Name.ToString() ?? string.Empty,
                    WorldID = menuTarget.TargetHomeWorld.RowId
                };
            }

            return judgeCriteria0 || judgeCriteria2 || judgeCriteria1;
        }
    }

    protected override void Uninit()
    {
        DService.Instance().ContextMenu.OnMenuOpened -= OnMenuOpen;

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

    private class Config : ModuleConfig
    {
        public bool FFLogsEnabled          = true;
        public bool LalachievementsEnabled = GameState.IsGL;
        public bool LodestoneEnabled       = GameState.IsGL;
        public bool RisingStoneEnabled     = GameState.IsCN;
        public bool SuMemoEnabled          = true;
        public bool TiebaEnabled           = GameState.IsCN;
        public bool TomestoneEnabled       = GameState.IsGL;
    }

    private class UpperContainerItem : MenuItemBase
    {
        private static readonly List<(Func<bool> Config, Action SetConfig, string LocKey, MenuItemBase Item)> MenuItems =
        [
            new
            (
                () => ModuleConfig.RisingStoneEnabled,
                () => ModuleConfig.RisingStoneEnabled ^= true,
                "ExpandPlayerMenuSearch-SearchRisingStone",
                new RisingStoneItem()
            ),
            new
            (
                () => ModuleConfig.TiebaEnabled,
                () => ModuleConfig.TiebaEnabled ^= true,
                "ExpandPlayerMenuSearch-SearchTieba",
                new TiebaItem()
            ),
            new
            (
                () => ModuleConfig.FFLogsEnabled,
                () => ModuleConfig.FFLogsEnabled ^= true,
                "ExpandPlayerMenuSearch-SearchFFLogs",
                new FFLogsItem()
            ),
            new
            (
                () => ModuleConfig.LodestoneEnabled,
                () => ModuleConfig.LodestoneEnabled ^= true,
                "ExpandPlayerMenuSearch-SearchLodestone",
                new LodestoneItem()
            ),
            new
            (
                () => ModuleConfig.LalachievementsEnabled,
                () => ModuleConfig.LalachievementsEnabled ^= true,
                "ExpandPlayerMenuSearch-SearchLalachievements",
                new LalachievementsItem()
            ),
            new
            (
                () => ModuleConfig.TomestoneEnabled,
                () => ModuleConfig.TomestoneEnabled ^= true,
                "ExpandPlayerMenuSearch-SearchTomestone",
                new TomestoneItem()
            ),
            new
            (
                () => ModuleConfig.SuMemoEnabled,
                () => ModuleConfig.SuMemoEnabled ^= true,
                "ExpandPlayerMenuSearch-SearchSuMemo",
                new SuMemoItem()
            )
        ];

        private static readonly ClickAllItem ClickAllMenu = new();
        public override         string       Name       { get; protected set; } = Lang.Get("ExpandPlayerMenuSearch-SearchTitle");
        public override         string       Identifier { get; protected set; } = nameof(ExpandPlayerMenuSearch);


        protected override bool WithDRPrefix { get; set; } = true;
        protected override bool IsSubmenu    { get; set; } = true;

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
                item.Item.Click(args);
            }
        }

        internal static void Draw()
        {
            foreach (var menuItem in MenuItems)
            {
                var value = menuItem.Config();

                if (ImGui.Checkbox(Lang.Get(menuItem.LocKey), ref value))
                {
                    menuItem.SetConfig();
                    ModuleConfig.Save(ModuleManager.Instance().GetModule<ExpandPlayerMenuSearch>());
                }
            }
        }
    }

    private class ClickAllItem : MenuItemBase
    {
        public override string Name       { get; protected set; } = Lang.Get("ExpandPlayerMenuSearch-SearchInAllPlatforms");
        public override string Identifier { get; protected set; } = nameof(ExpandPlayerMenuSearch);


        protected override void OnClicked(IMenuItemClickedArgs args) =>
            UpperContainerItem.ClickAll(args);
    }

    private class RisingStoneItem : MenuItemBase
    {
        private const string SearchAPI =
            "https://apiff14risingstones.web.sdo.com/api/common/search?type=6&keywords={0}&page={1}&limit=50";

        private const   string PlayerInfo = "https://ff14risingstones.web.sdo.com/pc/index.html#/me/info?uuid={0}";
        public override string Name       { get; protected set; } = Lang.Get("ExpandPlayerMenuSearch-SearchRisingStone");
        public override string Identifier { get; protected set; } = nameof(ExpandPlayerMenuSearch);

        protected override void OnClicked(IMenuItemClickedArgs args)
        {
            DService.Instance().Framework.RunOnTick
            (
                async () =>
                {
                    if (TargetChara == null) return;

                    var       page                 = 1;
                    var       isFound              = false;
                    const int delayBetweenRequests = 1000;

                    while (!isFound)
                    {
                        var url      = string.Format(SearchAPI, TargetChara.Name, page);
                        var response = await HTTPClientHelper.Instance().Get().GetStringAsync(url);
                        var result   = JsonConvert.DeserializeObject<RSPlayerSearchResult>(response);

                        if (result.Data.Count == 0)
                        {
                            NotifyHelper.Instance().NotificationError(Lang.Get("ExpandPlayerMenuSearch-PlayerInfoNotFound"));
                            break;
                        }

                        foreach (var player in result.Data)
                        {
                            if (player.CharacterName == TargetChara.Name && player.GroupName == TargetChara.World)
                            {
                                var uuid = player.UUID;
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
                },
                cancellationToken: CancelSource.Token
            );
        }
    }

    private class TiebaItem : MenuItemBase
    {
        private const   string URL = "https://tieba.baidu.com/f/search/res?ie=utf-8&kw=ff14&qw={0}";
        public override string Name       { get; protected set; } = Lang.Get("ExpandPlayerMenuSearch-SearchTieba");
        public override string Identifier { get; protected set; } = nameof(ExpandPlayerMenuSearch);

        protected override void OnClicked(IMenuItemClickedArgs args)
        {
            if (TargetChara == null) return;
            Util.OpenLink(string.Format(URL, $"{TargetChara.Name}@{TargetChara.World}"));
        }
    }

    private class FFLogsItem : MenuItemBase
    {
        private const   string URL = "https://cn.fflogs.com/character/{0}/{1}/{2}";
        public override string Name       { get; protected set; } = Lang.Get("ExpandPlayerMenuSearch-SearchFFLogs");
        public override string Identifier { get; protected set; } = nameof(ExpandPlayerMenuSearch);

        protected override void OnClicked(IMenuItemClickedArgs args)
        {
            if (TargetChara == null) return;
            var abbvr = RegionToFFLogsAbbvr(LuminaGetter.GetRow<World>(TargetChara.WorldID)?.DataCenter.ValueNullable?.Region ?? 0);
            Util.OpenLink(string.Format(URL, abbvr, TargetChara.World, TargetChara.Name));
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
        private const string URL =
            "https://na.finalfantasyxiv.com/lodestone/character/?q={0}&worldname=_dc_{1}&classjob=&race_tribe=&blog_lang=ja&blog_lang=en&blog_lang=de&blog_lang=fr&order=";

        public override string Name       { get; protected set; } = Lang.Get("ExpandPlayerMenuSearch-SearchLodestone");
        public override string Identifier { get; protected set; } = nameof(ExpandPlayerMenuSearch);

        protected override void OnClicked(IMenuItemClickedArgs args)
        {
            if (TargetChara == null) return;

            var dcName = LuminaGetter.GetRow<World>(TargetChara.WorldID)?.DataCenter.ValueNullable?.Name.ToString() ?? "";
            Util.OpenLink(string.Format(URL, TargetChara.Name.Replace(' ', '+'), dcName));
        }
    }

    private class LalachievementsItem : MenuItemBase
    {
        private const   string SEARCH_API  = "https://www.lalachievements.com/api/charsearch/{0}/";
        private const   string PLAYER_INFO = "https://www.lalachievements.com/char/{0}/";
        public override string Name       { get; protected set; } = Lang.Get("ExpandPlayerMenuSearch-SearchLalachievements");
        public override string Identifier { get; protected set; } = nameof(ExpandPlayerMenuSearch);

        protected override void OnClicked(IMenuItemClickedArgs args) =>
            DService.Instance().Framework.RunOnTick
            (
                async () =>
                {
                    if (TargetChara == null) return;

                    var url      = string.Format(SEARCH_API, TargetChara.Name);
                    var response = await HTTPClientHelper.Instance().Get().GetStringAsync(url);
                    var result   = JsonConvert.DeserializeObject<LLAPlayerSearchResult>(response);

                    if (result.Data.Count == 0)
                    {
                        NotifyHelper.Instance().NotificationError(Lang.Get("ExpandPlayerMenuSearch-PlayerInfoNotFound"));
                        return;
                    }

                    foreach (var player in result.Data)
                    {
                        if (player.CharacterName == TargetChara.Name && player.WorldID == TargetChara.WorldID)
                        {
                            Util.OpenLink(string.Format(PLAYER_INFO, player.CharacterID));
                            break;
                        }
                    }
                },
                TimeSpan.Zero,
                0,
                CancelSource.Token
            );
    }

    private class TomestoneItem : MenuItemBase
    {
        private const   string SEARCH_API = "https://tomestone.gg/search/autocomplete?term={0}"; // 搜索词, 空格 %20
        public override string Name       { get; protected set; } = Lang.Get("ExpandPlayerMenuSearch-SearchTomestone");
        public override string Identifier { get; protected set; } = nameof(ExpandPlayerMenuSearch);

        protected override void OnClicked(IMenuItemClickedArgs args) =>
            DService.Instance().Framework.RunOnTick
            (
                async () =>
                {
                    if (TargetChara == null) return;

                    var url      = string.Format(SEARCH_API, TargetChara.Name.Replace(" ", "%20"));
                    var response = await HTTPClientHelper.Instance().Get().GetStringAsync(url);

                    dynamic? result = JsonConvert.DeserializeObject(response);
                    if (result == null) return;

                    if (result.characters.Count == 0)
                    {
                        NotifyHelper.Instance().NotificationError(Lang.Get("ExpandPlayerMenuSearch-PlayerInfoNotFound"));
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
                },
                TimeSpan.Zero,
                0,
                CancelSource.Token
            );
    }

    private class SuMemoItem : MenuItemBase
    {
        private const   string URL = "https://sumemo.dev/member/{0}@{1}";
        public override string Name       { get; protected set; } = Lang.Get("ExpandPlayerMenuSearch-SearchSuMemo");
        public override string Identifier { get; protected set; } = nameof(ExpandPlayerMenuSearch);

        protected override void OnClicked(IMenuItemClickedArgs args)
        {
            if (TargetChara == null)
                return;
            Util.OpenLink(string.Format(URL, TargetChara.Name, TargetChara.World));
        }
    }
}
