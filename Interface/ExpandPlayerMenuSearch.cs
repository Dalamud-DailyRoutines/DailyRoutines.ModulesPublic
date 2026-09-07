using System.Reflection;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using Dalamud.Game.Text;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Lumina.Data;
using Lumina.Excel.Sheets;
using Newtonsoft.Json;
using OmenTools.Info.DTOs.Lalachievements;
using OmenTools.Info.DTOs.RisingStone;
using OmenTools.Interop.Game.Lumina;
using OmenTools.OmenService;
using NotifyHelper = OmenTools.OmenService.NotifyHelper;

namespace DailyRoutines.ModulesPublic.Interface;

public class ExpandPlayerMenuSearch : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("ExpandPlayerMenuSearchTitle"),
        Description = Lang.Get("ExpandPlayerMenuSearchDescription"),
        Category    = ModuleCategory.Interface
    };

    public override ModulePermission Permission { get; } = new() { AllDefaultEnabled = true };

    private SearchMenuItemBase[] SearchMenuItems
    {
        get
        {
            if (field is { Length: > 0 }) return field;

            return field =
            [
                .. typeof(ExpandPlayerMenuSearch)
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

        ContextMenuManager.Instance().Reg(menu);
    }

    protected override void Uninit()
    {
        ContextMenuManager.Instance().Unreg(menu);

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
            if (!ImGui.Checkbox(searchMenuItem.PlatformName, ref value)) continue;

            config.SearchMenuEnabledStates[searchMenuItem.ConfigKey] = value;
            config.Save(this);
        }
    }

    private static unsafe bool TryResolveTargetChara
    (
        ContextMenuOpenedArgs    args,
        out CharacterSearchInfo? resolvedTarget
    )
    {
        resolvedTarget = null;

        if (args.InventoryAgentContext != null) return false;

        var agent = IGameGui.Instance().FindAgentInterface("ChatLog");
        if (agent != nint.Zero && *(uint*)(agent + 0x948 + 0x8) == 3) return false;

        var hasTargetCharacter = args.TargetCharacter != null;
        var hasTargetNameAndWorld = !string.IsNullOrWhiteSpace(args.TargetName) &&
                                    args.TargetHomeWorldID > 0;
        var hasTargetObjectCharacter = args.TargetObjectID != 0                                              &&
                                       IObjectTable.Instance().SearchByID(args.TargetObjectID) is ICharacter &&
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
                var serverName = selectedFullName.StartsWith(playerName, StringComparison.Ordinal) ?
                                     selectedFullName[playerName.Length..] :
                                     string.Empty;

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
                if (args.TargetContentID == 0) return false;

                resolvedTarget = new()
                {
                    Name    = args.TargetName                                                           ?? string.Empty,
                    World   = LuminaGetter.GetRow<World>((uint)args.TargetHomeWorldID)?.Name.ToString() ?? string.Empty,
                    WorldID = (uint)args.TargetHomeWorldID
                };
                return true;
            case "LinkShell":
            case "CrossWorldLinkshell":
                return args.TargetContentID != 0 &&
                       TryResolveGeneralTarget(args, hasTargetCharacter, hasTargetObjectCharacter, hasTargetNameAndWorld, out resolvedTarget);
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
                return TryResolveGeneralTarget(args, hasTargetCharacter, hasTargetObjectCharacter, hasTargetNameAndWorld, out resolvedTarget);
        }
    }

    private static unsafe bool TryResolveGeneralTarget
    (
        ContextMenuOpenedArgs    args,
        bool                     hasTargetCharacter,
        bool                     hasTargetObjectCharacter,
        bool                     hasTargetNameAndWorld,
        out CharacterSearchInfo? resolvedTarget
    )
    {
        resolvedTarget = null;

        if (hasTargetCharacter)
        {
            var targetCharacter = args.TargetCharacter!;
            resolvedTarget = new()
            {
                Name    = targetCharacter->NameString,
                World   = LuminaGetter.GetRow<World>(targetCharacter->HomeWorld)?.Name.ToString() ?? string.Empty,
                WorldID = targetCharacter->HomeWorld
            };
        }
        else if (IObjectTable.Instance().SearchByID(args.TargetObjectID) is ICharacter chara &&
                 hasTargetNameAndWorld)
        {
            resolvedTarget = new()
            {
                Name    = chara.Name,
                World   = LuminaGetter.GetRow<World>(((Character*)chara.Address)->HomeWorld)?.Name.ToString() ?? string.Empty,
                WorldID = ((Character*)chara.Address)->HomeWorld
            };
        }
        else if (hasTargetNameAndWorld)
        {
            resolvedTarget = new()
            {
                Name    = args.TargetName                                                           ?? string.Empty,
                World   = LuminaGetter.GetRow<World>((uint)args.TargetHomeWorldID)?.Name.ToString() ?? string.Empty,
                WorldID = (uint)args.TargetHomeWorldID
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

    private abstract class SearchMenuItemBase
    (
        ExpandPlayerMenuSearch module
    ) : ContextMenuEntry
    {
        protected readonly ExpandPlayerMenuSearch module = module;

        public override string Identifier => nameof(ExpandPlayerMenuSearch);

        public override bool OmitPrefix => true;

        public abstract string PlatformName   { get; }
        public abstract string ConfigKey      { get; }
        public virtual  bool   DefaultEnabled => false;

        protected CharacterSearchInfo? TargetChara => module.targetChara;

        protected static void NotifyPlayerNotFound()
        {
            var message = Lang.Get("ExpandPlayerMenuSearch-Notification-PlayerInfoNotFound");
            NotifyHelper.ToastError(message);
            NotifyHelper.Chat
            (
                new XivChatEntry
                {
                    Type    = XivChatType.ErrorMessage,
                    Message = message
                }
            );
        }

        protected void RunOnTick
        (
            Func<CharacterSearchInfo, Task> action
        )
        {
            var targetChara = TargetChara;
            if (targetChara == null) return;

            IFramework.Instance().RunOnTick
            (
                () => action(targetChara),
                cancellationToken: module.cancelSource.Token
            );
        }

        protected void RunOnTickImmediately
        (
            Func<CharacterSearchInfo, Task> action
        )
        {
            var targetChara = TargetChara;
            if (targetChara == null) return;

            IFramework.Instance().RunOnTick
            (
                () => action(targetChara),
                TimeSpan.Zero,
                0,
                module.cancelSource.Token
            );
        }

        public abstract void OnClicked();

        public override ContextMenuItem Create
        (
            ContextMenuOpenedArgs args
        ) =>
            new()
            {
                Name      = PlatformName,
                OnClicked = _ => OnClicked(),
            };
    }

    private sealed class UpperContainerItem
    (
        ExpandPlayerMenuSearch module
    ) : ContextMenuEntry
    {
        public override string Identifier => nameof(ExpandPlayerMenuSearch);
        
        public override ContextMenuItem? Create
        (
            ContextMenuOpenedArgs args
        )
        {
            module.targetChara = null;

            if (!TryResolveTargetChara(args, out var targetChara)) return null;

            var searchItems = module.SearchMenuItems
                                    .Where
                                    (searchMenuItem => module.config.SearchMenuEnabledStates
                                                             .GetValueOrDefault(searchMenuItem.ConfigKey, searchMenuItem.DefaultEnabled)
                                    )
                                    .ToArray();
            if (searchItems.Length == 0) return null;

            module.targetChara = targetChara;

            return new()
            {
                Name = Lang.Get("ExpandPlayerMenuSearch-ContextMenu-Name"),
                Submenu = new()
                {
                    Title   = Lang.Get("ExpandPlayerMenuSearch-ContextMenu-SubTitle"),
                    Entries = [module.clickAllMenu, .. searchItems]
                }
            };
        }
    }

    private sealed class ClickAllItem
    (
        ExpandPlayerMenuSearch module
    ) : ContextMenuEntry
    {
        public override string Identifier => nameof(ExpandPlayerMenuSearch);

        public override int? Priority => 1000;

        public override bool OmitPrefix => true;

        public override ContextMenuItem Create
        (
            ContextMenuOpenedArgs args
        ) =>
            new()
            {
                Name = Lang.Get("ExpandPlayerMenuSearch-ContextMenu-SearchAll"),
                OnClicked = _ =>
                {
                    foreach (var searchMenuItem in module.SearchMenuItems)
                    {
                        if (!module.config.SearchMenuEnabledStates
                                   .GetValueOrDefault(searchMenuItem.ConfigKey, searchMenuItem.DefaultEnabled))
                            continue;

                        searchMenuItem.OnClicked();
                    }
                }
            };
    }

    private sealed class RisingStoneItem
    (
        ExpandPlayerMenuSearch module
    ) : SearchMenuItemBase(module)
    {
        public override string PlatformName   => "石之家";
        public override string ConfigKey      => nameof(RisingStoneItem);
        public override bool   DefaultEnabled => GameState.IsCN;

        public override void OnClicked() =>
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

        #region 常量

        private const string SearchAPI =
            "https://apiff14risingstones.web.sdo.com/api/common/search?type=6&keywords={0}&page={1}&limit=50";

        private const string PlayerInfoURL = 
            "https://ff14risingstones.web.sdo.com/pc/index.html#/me/info?uuid={0}";

        #endregion
    }

    private sealed class TiebaItem
    (
        ExpandPlayerMenuSearch module
    ) : SearchMenuItemBase(module)
    {
        public override string PlatformName   => "百度贴吧";
        public override string ConfigKey      => nameof(TiebaItem);
        public override bool   DefaultEnabled => GameState.IsCN;

        public override void OnClicked()
        {
            var targetChara = TargetChara;
            if (targetChara == null) return;

            Util.OpenLink(string.Format(URL, $"{targetChara.Name}@{targetChara.World}"));
        }

        #region 常量

        private const string URL = 
            "https://tieba.baidu.com/f/search/res?ie=utf-8&kw=ff14&qw={0}";

        #endregion
    }

    private sealed class FFLogsItem
    (
        ExpandPlayerMenuSearch module
    ) : SearchMenuItemBase(module)
    {
        public override string PlatformName   => "FF Logs";
        public override string ConfigKey      => nameof(FFLogsItem);
        public override bool   DefaultEnabled => true;

        public override void OnClicked()
        {
            var targetChara = TargetChara;
            if (targetChara == null) return;

            var region = LuminaGetter.GetRowOrDefault<World>(targetChara.WorldID).DataCenter.Value.Region.RowId;
            Util.OpenLink(string.Format(URL, RegionToFFLogsAbbvr(region), targetChara.World, targetChara.Name, GetPrefix()));
        }

        private static string RegionToFFLogsAbbvr
        (
            uint region
        ) =>
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

        private static string GetPrefix() =>
            GameState.ClientLanguge switch
            {
                Language.Japanese           => "ja",
                Language.French             => "fr",
                Language.German             => "de",
                Language.Korean             => "ko",
                Language.ChineseSimplified  => "cn",
                Language.ChineseTraditional => "cn",
                _                           => "www",
            };

        #region 常量

        private const string URL = 
            "https://{3}.fflogs.com/character/{0}/{1}/{2}";

        #endregion
    }

    private sealed class LodestoneItem
    (
        ExpandPlayerMenuSearch module
    ) : SearchMenuItemBase(module)
    {
        public override string PlatformName   => "Lodestone";
        public override string ConfigKey      => nameof(LodestoneItem);
        public override bool   DefaultEnabled => GameState.IsGL;

        public override void OnClicked()
        {
            if (TargetChara == null) return;

            var dcName = LuminaWrapper.GetWorldDCName(TargetChara.WorldID);
            Util.OpenLink(string.Format(URL, TargetChara.Name.Replace(' ', '+'), dcName, GetPrefix()));
        }

        private static string GetPrefix() =>
            GameState.ClientLanguge switch
            {
                Language.Japanese => "jp",
                Language.French   => "fr",
                Language.German   => "de",
                _                 => "na",
            };

        #region 常量

        private const string URL =
            "https://{2}.finalfantasyxiv.com/lodestone/character/?q={0}&worldname=_dc_{1}&classjob=&race_tribe=&blog_lang=ja&blog_lang=en&blog_lang=de&blog_lang=fr&order=";

        #endregion
    }

    private sealed class LalachievementsItem
    (
        ExpandPlayerMenuSearch module
    ) : SearchMenuItemBase(module)
    {
        public override string PlatformName   => "Lalachievements";
        public override string ConfigKey      => nameof(LalachievementsItem);
        public override bool   DefaultEnabled => GameState.IsGL;
        
        public override void OnClicked() =>
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

                        Util.OpenLink(string.Format(PLAYER_INFO_URL, player.CharacterID, GetVariant()));
                        break;
                    }
                }
            );
        
        private static string GetVariant() =>
            GameState.ClientLanguge switch
            {
                Language.Japanese => "/ja",
                Language.French   => "/fr",
                Language.German   => "/de",
                _                 => string.Empty,
            };

        #region 常量

        // TODO：有 Cloudflare Turnstile 验证
        private const string SEARCH_API      = "https://www.lalachievements.com/api/charsearch/{0}/";
        private const string PLAYER_INFO_URL = "https://www.lalachievements.com{1}/char/{0}/";

        #endregion
    }

    private sealed class TomestoneItem
    (
        ExpandPlayerMenuSearch module
    ) : SearchMenuItemBase(module)
    {
        public override string PlatformName   => "Tomestone";
        public override string ConfigKey      => nameof(TomestoneItem);
        public override bool   DefaultEnabled => GameState.IsGL;

        public override void OnClicked() =>
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

        #region 常量

        private const string SEARCH_API = "https://tomestone.gg/search/autocomplete?term={0}";

        #endregion
    }
}
