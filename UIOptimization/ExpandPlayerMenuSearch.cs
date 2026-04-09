using System.Reflection;
using DailyRoutines.Common.Info.Abstractions;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
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
using MenuItem = Dalamud.Game.Gui.ContextMenu.MenuItem;
using NotifyHelper = OmenTools.OmenService.NotifyHelper;

namespace DailyRoutines.ModulesPublic;

public class ExpandPlayerMenuSearch : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("ExpandPlayerMenuSearchTitle"),
        Description = Lang.Get("ExpandPlayerMenuSearchDescription"),
        Category    = ModuleCategory.UIOptimization
    };

    public override ModulePermission Permission { get; } = new() { AllDefaultEnabled = true };

    private SearchMenuItemBase[] SearchMenuItems
    {
        get
        {
            if (field is { Length: > 0 }) return field;

            return field = typeof(ExpandPlayerMenuSearch)
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
                            )!
                           )
                           .OrderBy(searchMenuItem => searchMenuItem.Order)
                           .ThenBy(searchMenuItem => searchMenuItem.ConfigKey, StringComparer.Ordinal)
                           .ToArray();
        }
    }

    private          Config                  config       = null!;
    private readonly CancellationTokenSource cancelSource = new();
    private          CharacterSearchInfo?    targetChara;

    private readonly UpperContainerItem menu;
    private readonly ClickAllItem       clickAllMenu;

    public ExpandPlayerMenuSearch()
    {
        menu         = new(this);
        clickAllMenu = new(this);
    }

    protected override void Init()
    {
        config = Config.Load(this) ?? new();

        DService.Instance().ContextMenu.OnMenuOpened += OnMenuOpened;
    }

    protected override void Uninit()
    {
        DService.Instance().ContextMenu.OnMenuOpened -= OnMenuOpened;

        cancelSource.Cancel();
        cancelSource.Dispose();

        targetChara = null;
    }

    protected override void ConfigUI()
    {
        foreach (var searchMenuItem in SearchMenuItems)
        {
            var value = config.SearchMenuEnabledStates
                              .GetValueOrDefault(searchMenuItem.ConfigKey, searchMenuItem.DefaultEnabled);
            if (!ImGui.Checkbox(Lang.Get(searchMenuItem.LocKey), ref value)) continue;

            config.SearchMenuEnabledStates[searchMenuItem.ConfigKey] = value;
            SaveConfig();
        }
    }

    private void SaveConfig() =>
        config.Save(this);

    private void OnMenuOpened(IMenuOpenedArgs args)
    {
        targetChara = null;

        if (args.MenuType != ContextMenuType.Default) return;
        if (!TryResolveTargetChara(args, out targetChara)) return;

        var shouldAddMenu = SearchMenuItems.Any
        (searchMenuItem => config.SearchMenuEnabledStates
                                 .GetValueOrDefault(searchMenuItem.ConfigKey, searchMenuItem.DefaultEnabled)
        );

        if (shouldAddMenu)
            args.AddMenuItem(menu.Get());
    }

    private static unsafe bool TryResolveTargetChara(IMenuArgs args, out CharacterSearchInfo? resolvedTarget)
    {
        resolvedTarget = null;

        if (args.Target is MenuTargetInventory) return false;
        if (args.Target is not MenuTargetDefault menuTarget) return false;

        var agent = DService.Instance().GameGUI.FindAgentInterface("ChatLog");
        if (agent != nint.Zero && *(uint*)(agent + 0x948 + 8) == 3) return false;

        var hasTargetCharacter = menuTarget.TargetCharacter != null;
        var hasTargetNameAndWorld = !string.IsNullOrWhiteSpace(menuTarget.TargetName) &&
                                    menuTarget.TargetHomeWorld.ValueNullable != null  &&
                                    menuTarget.TargetHomeWorld.Value.RowId   != 0;
        var hasTargetObjectCharacter = menuTarget.TargetObject != null                                   &&
                                       IGameObject.Create(menuTarget.TargetObject.Address) is ICharacter &&
                                       hasTargetNameAndWorld;

        switch (args.AddonName)
        {
            default:
                return false;
            case "BlackList":
                var agentBlackList = AgentBlacklist.Instance();

                if ((nint)agentBlackList == nint.Zero || !agentBlackList->AgentInterface.IsAgentActive())
                    return false;

                var playerName       = agentBlackList->SelectedPlayerName.ToString();
                var selectedFullName = agentBlackList->SelectedPlayerFullName.ToString();
                var serverName = selectedFullName.StartsWith(playerName, StringComparison.Ordinal)
                                     ? selectedFullName[playerName.Length..]
                                     : string.Empty;

                resolvedTarget = new()
                {
                    Name  = playerName,
                    World = serverName,
                    WorldID = LuminaGetter.Get<World>()
                                          .FirstOrDefault(world => world.Name.ToString().Contains(serverName, StringComparison.OrdinalIgnoreCase))
                                          .RowId
                };
                return true;
            case "FreeCompany":
                if (menuTarget.TargetContentId == 0) return false;

                resolvedTarget = new()
                {
                    Name    = menuTarget.TargetName,
                    World   = menuTarget.TargetHomeWorld.ValueNullable?.Name.ToString() ?? string.Empty,
                    WorldID = menuTarget.TargetHomeWorld.RowId
                };
                return true;
            case "LinkShell":
            case "CrossWorldLinkshell":
                return menuTarget.TargetContentId != 0 &&
                       TryResolveGeneralTarget(menuTarget, hasTargetCharacter, hasTargetObjectCharacter, hasTargetNameAndWorld, out resolvedTarget);
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
                return TryResolveGeneralTarget(menuTarget, hasTargetCharacter, hasTargetObjectCharacter, hasTargetNameAndWorld, out resolvedTarget);
        }
    }

    private static unsafe bool TryResolveGeneralTarget
    (
        MenuTargetDefault        menuTarget,
        bool                     hasTargetCharacter,
        bool                     hasTargetObjectCharacter,
        bool                     hasTargetNameAndWorld,
        out CharacterSearchInfo? resolvedTarget
    )
    {
        resolvedTarget = null;

        if (hasTargetCharacter)
        {
            resolvedTarget = new()
            {
                Name    = menuTarget.TargetCharacter!.Name,
                World   = menuTarget.TargetCharacter.HomeWorld.ValueNullable?.Name.ToString() ?? string.Empty,
                WorldID = menuTarget.TargetCharacter.HomeWorld.RowId
            };
        }
        else if (menuTarget.TargetObject != null                                         &&
                 IGameObject.Create(menuTarget.TargetObject.Address) is ICharacter chara &&
                 hasTargetNameAndWorld)
        {
            resolvedTarget = new()
            {
                Name    = chara.Name.ToString(),
                World   = LuminaGetter.GetRow<World>(((Character*)chara.Address)->HomeWorld)?.Name.ToString() ?? string.Empty,
                WorldID = ((Character*)chara.Address)->HomeWorld
            };
        }
        else if (hasTargetNameAndWorld)
        {
            resolvedTarget = new()
            {
                Name    = menuTarget.TargetName,
                World   = menuTarget.TargetHomeWorld.ValueNullable?.Name.ToString() ?? string.Empty,
                WorldID = menuTarget.TargetHomeWorld.RowId
            };
        }

        return hasTargetCharacter || hasTargetObjectCharacter || hasTargetNameAndWorld;
    }

    private sealed class CharacterSearchInfo
    {
        public string Name    { get; init; } = string.Empty;
        public string World   { get; init; } = string.Empty;
        public uint   WorldID { get; init; }
    }

    private sealed class Config : ModuleConfig
    {
        public Dictionary<string, bool> SearchMenuEnabledStates = [];
    }

    private abstract class SearchMenuItemBase : MenuItemBase
    {
        protected readonly ExpandPlayerMenuSearch module;

        protected SearchMenuItemBase(ExpandPlayerMenuSearch module)
        {
            this.module = module;
            Name        = Lang.Get(LocKey);
        }

        public sealed override string Name       { get; protected set; }
        public sealed override string Identifier { get; protected set; } = nameof(ExpandPlayerMenuSearch);

        public abstract string LocKey         { get; }
        public abstract string ConfigKey      { get; }
        public virtual  bool   DefaultEnabled => false;
        public virtual  int    Order          => 0;

        protected CharacterSearchInfo? TargetChara => module.targetChara;

        protected static void NotifyPlayerNotFound() =>
            NotifyHelper.Instance().NotificationError(Lang.Get("ExpandPlayerMenuSearch-PlayerInfoNotFound"));

        protected void RunOnTick(Func<CharacterSearchInfo, Task> action)
        {
            var targetChara = TargetChara;
            if (targetChara == null) return;

            DService.Instance().Framework.RunOnTick
            (
                () => action(targetChara),
                cancellationToken: module.cancelSource.Token
            );
        }

        protected void RunOnTickImmediately(Func<CharacterSearchInfo, Task> action)
        {
            var targetChara = TargetChara;
            if (targetChara == null) return;

            DService.Instance().Framework.RunOnTick
            (
                () => action(targetChara),
                TimeSpan.Zero,
                0,
                module.cancelSource.Token
            );
        }
    }

    private sealed class UpperContainerItem
    (
        ExpandPlayerMenuSearch module
    ) : MenuItemBase
    {
        public override string Name       { get; protected set; } = Lang.Get("ExpandPlayerMenuSearch-SearchTitle");
        public override string Identifier { get; protected set; } = nameof(ExpandPlayerMenuSearch);

        protected override bool WithDRPrefix { get; set; } = true;
        protected override bool IsSubmenu    { get; set; } = true;

        protected override void OnClicked(IMenuItemClickedArgs args) =>
            args.OpenSubmenu(Name, ProcessMenuItems());

        private List<MenuItem> ProcessMenuItems()
        {
            var list = new List<MenuItem> { module.clickAllMenu.Get() };

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

    private sealed class ClickAllItem
    (
        ExpandPlayerMenuSearch module
    ) : MenuItemBase
    {
        public override string Name       { get; protected set; } = Lang.Get("ExpandPlayerMenuSearch-SearchInAllPlatforms");
        public override string Identifier { get; protected set; } = nameof(ExpandPlayerMenuSearch);

        protected override void OnClicked(IMenuItemClickedArgs args)
        {
            foreach (var searchMenuItem in module.SearchMenuItems)
            {
                if (!module.config.SearchMenuEnabledStates
                           .GetValueOrDefault(searchMenuItem.ConfigKey, searchMenuItem.DefaultEnabled))
                    continue;

                searchMenuItem.Click(args);
            }
        }
    }

    private sealed class RisingStoneItem
    (
        ExpandPlayerMenuSearch module
    ) : SearchMenuItemBase(module)
    {
        private const string SearchAPI =
            "https://apiff14risingstones.web.sdo.com/api/common/search?type=6&keywords={0}&page={1}&limit=50";

        private const string PlayerInfoURL = "https://ff14risingstones.web.sdo.com/pc/index.html#/me/info?uuid={0}";

        public override string LocKey         => "ExpandPlayerMenuSearch-SearchRisingStone";
        public override string ConfigKey      => nameof(RisingStoneItem);
        public override bool   DefaultEnabled => GameState.IsCN;
        public override int    Order          => 10;

        protected override void OnClicked(IMenuItemClickedArgs args) =>
            RunOnTick
            (async targetChara =>
                {
                    var page    = 1;
                    var isFound = false;

                    while (!isFound)
                    {
                        var url      = string.Format(SearchAPI, targetChara.Name, page);
                        var response = await HTTPClientHelper.Instance().Get().GetStringAsync(url);
                        var result   = JsonConvert.DeserializeObject<RSPlayerSearchResult>(response);

                        if (result?.Data == null || result.Data.Count == 0)
                        {
                            NotifyPlayerNotFound();
                            break;
                        }

                        foreach (var player in result.Data)
                        {
                            if (player.CharacterName != targetChara.Name || player.GroupName != targetChara.World)
                                continue;

                            Util.OpenLink(string.Format(PlayerInfoURL, player.UUID));
                            isFound = true;
                            break;
                        }

                        if (isFound) break;

                        await Task.Delay(1000, module.cancelSource.Token);
                        page++;
                    }
                }
            );
    }

    private sealed class TiebaItem
    (
        ExpandPlayerMenuSearch module
    ) : SearchMenuItemBase(module)
    {
        private const string URL = "https://tieba.baidu.com/f/search/res?ie=utf-8&kw=ff14&qw={0}";

        public override string LocKey         => "ExpandPlayerMenuSearch-SearchTieba";
        public override string ConfigKey      => nameof(TiebaItem);
        public override bool   DefaultEnabled => GameState.IsCN;
        public override int    Order          => 20;

        protected override void OnClicked(IMenuItemClickedArgs args)
        {
            var targetChara = TargetChara;
            if (targetChara == null) return;

            Util.OpenLink(string.Format(URL, $"{targetChara.Name}@{targetChara.World}"));
        }
    }

    private sealed class FFLogsItem
    (
        ExpandPlayerMenuSearch module
    ) : SearchMenuItemBase(module)
    {
        private const string URL = "https://cn.fflogs.com/character/{0}/{1}/{2}";

        public override string LocKey         => "ExpandPlayerMenuSearch-SearchFFLogs";
        public override string ConfigKey      => nameof(FFLogsItem);
        public override bool   DefaultEnabled => true;
        public override int    Order          => 30;

        protected override void OnClicked(IMenuItemClickedArgs args)
        {
            var targetChara = TargetChara;
            if (targetChara == null) return;

            var region = LuminaGetter.GetRow<World>(targetChara.WorldID)?.DataCenter.ValueNullable?.Region ?? 0;
            Util.OpenLink(string.Format(URL, RegionToFFLogsAbbvr(region), targetChara.World, targetChara.Name));
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

    private sealed class LodestoneItem
    (
        ExpandPlayerMenuSearch module
    ) : SearchMenuItemBase(module)
    {
        private const string URL =
            "https://na.finalfantasyxiv.com/lodestone/character/?q={0}&worldname=_dc_{1}&classjob=&race_tribe=&blog_lang=ja&blog_lang=en&blog_lang=de&blog_lang=fr&order=";

        public override string LocKey         => "ExpandPlayerMenuSearch-SearchLodestone";
        public override string ConfigKey      => nameof(LodestoneItem);
        public override bool   DefaultEnabled => GameState.IsGL;
        public override int    Order          => 40;

        protected override void OnClicked(IMenuItemClickedArgs args)
        {
            var targetChara = TargetChara;
            if (targetChara == null) return;

            var dcName = LuminaGetter.GetRow<World>(targetChara.WorldID)?.DataCenter.ValueNullable?.Name.ToString() ?? string.Empty;
            Util.OpenLink(string.Format(URL, targetChara.Name.Replace(' ', '+'), dcName));
        }
    }

    private sealed class LalachievementsItem
    (
        ExpandPlayerMenuSearch module
    ) : SearchMenuItemBase(module)
    {
        private const string SEARCH_API      = "https://www.lalachievements.com/api/charsearch/{0}/";
        private const string PLAYER_INFO_URL = "https://www.lalachievements.com/char/{0}/";

        public override string LocKey         => "ExpandPlayerMenuSearch-SearchLalachievements";
        public override string ConfigKey      => nameof(LalachievementsItem);
        public override bool   DefaultEnabled => GameState.IsGL;
        public override int    Order          => 50;

        protected override void OnClicked(IMenuItemClickedArgs args) =>
            RunOnTickImmediately
            (async targetChara =>
                {
                    var url      = string.Format(SEARCH_API, targetChara.Name);
                    var response = await HTTPClientHelper.Instance().Get().GetStringAsync(url);
                    var result   = JsonConvert.DeserializeObject<LLAPlayerSearchResult>(response);

                    if (result?.Data == null || result.Data.Count == 0)
                    {
                        NotifyPlayerNotFound();
                        return;
                    }

                    foreach (var player in result.Data)
                    {
                        if (player.CharacterName != targetChara.Name || player.WorldID != targetChara.WorldID)
                            continue;

                        Util.OpenLink(string.Format(PLAYER_INFO_URL, player.CharacterID));
                        break;
                    }
                }
            );
    }

    private sealed class TomestoneItem
    (
        ExpandPlayerMenuSearch module
    ) : SearchMenuItemBase(module)
    {
        private const string SEARCH_API = "https://tomestone.gg/search/autocomplete?term={0}";

        public override string LocKey         => "ExpandPlayerMenuSearch-SearchTomestone";
        public override string ConfigKey      => nameof(TomestoneItem);
        public override bool   DefaultEnabled => GameState.IsGL;
        public override int    Order          => 60;

        protected override void OnClicked(IMenuItemClickedArgs args) =>
            RunOnTickImmediately
            (async targetChara =>
                {
                    var      url      = string.Format(SEARCH_API, targetChara.Name.Replace(" ", "%20"));
                    var      response = await HTTPClientHelper.Instance().Get().GetStringAsync(url);
                    dynamic? result   = JsonConvert.DeserializeObject(response);
                    if (result?.characters == null) return;

                    if (result.characters.Count == 0)
                    {
                        NotifyPlayerNotFound();
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
                        if (name != targetChara.Name || !server.Contains(targetChara.World, StringComparison.OrdinalIgnoreCase))
                            continue;

                        Util.OpenLink($"https://tomestone.gg{refLink}");
                        break;
                    }
                }
            );
    }

    private sealed class SuMemoItem
    (
        ExpandPlayerMenuSearch module
    ) : SearchMenuItemBase(module)
    {
        private const string URL = "https://sumemo.dev/member/{0}@{1}";

        public override string LocKey         => "ExpandPlayerMenuSearch-SearchSuMemo";
        public override string ConfigKey      => nameof(SuMemoItem);
        public override bool   DefaultEnabled => true;
        public override int    Order          => 70;

        protected override void OnClicked(IMenuItemClickedArgs args)
        {
            var targetChara = TargetChara;
            if (targetChara == null) return;

            Util.OpenLink(string.Format(URL, targetChara.Name, targetChara.World));
        }
    }
}
