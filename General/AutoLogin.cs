using System.Diagnostics;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Interface.Colors;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using OmenTools.Dalamud.Attributes;
using OmenTools.ImGuiOm.Widgets.Combos;
using OmenTools.Info.Game.Data;
using OmenTools.Interop.Game.AgentEvent;
using OmenTools.Interop.Game.Lumina;
using OmenTools.Interop.Game.Models.Native;
using OmenTools.OmenService;
using OmenTools.Threading;

namespace DailyRoutines.ModulesPublic;

public unsafe class AutoLogin : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title               = Lang.Get("AutoLoginTitle"),
        Description         = Lang.Get("AutoLoginDescription"),
        Category            = ModuleCategory.General,
        ModulesRecommend    = ["AutoSkipLogo"],
        ModulesPrerequisite = ["InstantLogout"]
    };

    private static string CurrentProcessInfo
    {
        get
        {
            using var p = Process.GetCurrentProcess();
            return $"{Environment.ProcessId}-{p.StartTime.ToUniversalTime().Ticks}";
        }
    }

    private Config config = null!;

    private readonly WorldSelectCombo worldSelectCombo = new("World");

    // 界面
    private string selectedCharaName;
    private int    dropIndex = -1;

    private (string? ChracterName, int CharacterIndex, uint WorldID)? manualLoginInfo;

    // 外部控制
    private bool isNextAutoLoginHandled;

    protected override void Init()
    {
        config     =   Config.Load(this) ?? new();
        TaskHelper ??= new() { TimeoutMS = 180_000, ShowDebug = true };

        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "_TitleMenu", OnTitleMenu);
        OnTitleMenu(AddonEvent.PostSetup, null);

        CommandManager.Instance().AddCommand(COMMAND, new(OnCommand) { HelpMessage = Lang.Get("AutoLogin-CommandHelp") });
        GameState.Instance().Login += OnLogin;
    }

    protected override void Uninit()
    {
        GameState.Instance().Login -= OnLogin;
        CommandManager.Instance().RemoveCommand(COMMAND);

        DService.Instance().AddonLifecycle.UnregisterListener(OnTitleMenu);
    }

    protected override void ConfigUI()
    {
        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), $"{Lang.Get("Command")}");

        using (ImRaii.PushIndent())
            ImGui.TextWrapped($"{COMMAND} {Lang.Get("AutoLogin-CommandHelp")}");

        ImGui.NewLine();

        ImGuiOm.ConflictKeyText();

        ImGui.NewLine();

        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), $"{Lang.Get("AutoLogin-BehaviourMode")}");

        using (ImRaii.PushIndent())
        {
            ImGui.SetNextItemWidth(300f * GlobalUIScale);

            using (var combo = ImRaii.Combo("###BehaviourModeCombo", BehaviourModeLoc[config.Mode]))
            {
                if (combo)
                {
                    foreach (var mode in BehaviourModeLoc)
                    {
                        if (ImGui.Selectable(mode.Value, mode.Key == config.Mode))
                        {
                            config.Mode = mode.Key;
                            config.Save(this);
                        }
                    }
                }
            }
        }

        ImGui.NewLine();

        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), $"{Lang.Get("AutoLogin-LoginInfos")}");

        using (ImRaii.PushIndent())
        {
            using (ImRaii.Group())
            {
                // 服务器选择
                ImGui.SetNextItemWidth(200f * GlobalUIScale);
                worldSelectCombo.DrawRadio();

                ImGui.SameLine();
                ImGui.TextUnformatted(LuminaWrapper.GetAddonText(13634));

                // 选择当前服务器
                ImGui.SameLine();
                if (ImGui.SmallButton(Lang.Get("Current")))
                    worldSelectCombo.SelectedID = GameState.CurrentWorld;

                // 角色名
                ImGui.SetNextItemWidth(200f * GlobalUIScale);
                ImGui.InputText
                (
                    $"{LuminaWrapper.GetAddonText(14055)}##AutoLogin-EnterCharacterName",
                    ref selectedCharaName,
                    flags: ImGuiInputTextFlags.EnterReturnsTrue
                );
            }

            ImGui.SameLine(0, 16f * GlobalUIScale);

            if (ImGuiOm.ButtonIconWithTextVertical(FontAwesomeIcon.Plus, Lang.Get("Add")))
            {
                if (string.IsNullOrWhiteSpace(selectedCharaName) || worldSelectCombo.SelectedID == 0) return;
                var info = new LoginInfo(worldSelectCombo.SelectedID, selectedCharaName);

                if (!config.LoginData.Contains(info))
                {
                    config.LoginData.Add(info);
                    config.Save(this);
                }
            }

            ImGuiOm.TooltipHover(Lang.Get("AutoLogin-LoginInfoOrderHelp"));

            for (var i = 0; i < config.LoginData.Count; i++)
            {
                var info = config.LoginData[i];

                var text =
                    $"{i + 1}. {Lang.Get("AutoLogin-LoginInfoDisplayText", LuminaWrapper.GetWorldName(info.WorldID), LuminaWrapper.GetWorldDCName(info.WorldID), info.CharacterName)}";

                using (ImRaii.PushColor(ImGuiCol.Text, i % 2 == 0 ? ImGuiColors.TankBlue : ImGuiColors.DalamudWhite))
                    ImGui.Selectable(text);

                using (var source = ImRaii.DragDropSource())
                {
                    if (source)
                    {
                        if (ImGui.SetDragDropPayload("LoginInfoReorder", []))
                            dropIndex = i;

                        ImGui.TextColored(ImGuiColors.DalamudYellow, text);
                    }
                }

                using (var target = ImRaii.DragDropTarget())
                {
                    if (target)
                    {
                        if (ImGui.AcceptDragDropPayload("LoginInfoReorder").Handle != null)
                        {
                            Swap(dropIndex, i);
                            dropIndex = -1;
                        }
                    }
                }

                using (var context = ImRaii.ContextPopupItem($"ContextMenu_{i}"))
                {
                    if (context)
                    {
                        if (ImGui.Selectable(Lang.Get("Delete")))
                        {
                            config.LoginData.Remove(info);
                            config.Save(this);
                        }
                    }
                }

                if (i != config.LoginData.Count - 1)
                    ImGui.Separator();
            }
        }
    }

    #region 事件

    private void OnLogin()
    {
        TaskHelper.Abort();
        
        manualLoginInfo        = null;
        isNextAutoLoginHandled = false;

        config.CurrentProcessInfo = CurrentProcessInfo;
        config.Save(this);
    }

    private void OnCommand(string command, string args)
    {
        args = args.Trim();

        if (string.IsNullOrWhiteSpace(args)       ||
            !GameState.IsLoggedIn                 ||
            GameState.ContentFinderCondition != 0 ||
            TaskHelper.IsBusy)
            return;

        var parts = args.Split(' ');
        if (parts.Length is not (1 or 2))
            return;

        manualLoginInfo = null;
        
        string? characterName  = null;
        var     characterIndex = -1;
        var     worldID        = (uint)AgentLobby.Instance()->WorldId;

        var characterPart = parts[0];

        // 角色参数: 索引 (0-7) 或名称 (两段式用 + 连接)
        if (uint.TryParse(characterPart, out var idx) && idx < 8)
            characterIndex = (int)idx;
        else
            characterName = characterPart.Replace('+', ' ');

        // 可选服务器参数: ID 或名称
        if (parts.Length > 1)
        {
            var worldPart = parts[1];

            if (uint.TryParse(worldPart, out var id) && Sheets.Worlds.ContainsKey(id))
                worldID = id;
            else
            {
                var found = Sheets.Worlds.FirstOrDefault(x => x.Value.Name.ToString().Equals(worldPart, StringComparison.OrdinalIgnoreCase));
                if (found.Key != 0)
                    worldID = found.Key;
            }
        }

        manualLoginInfo = new(characterName, characterIndex, worldID);
        TaskHelper.Enqueue(() => ChatManager.Instance().SendMessage("/logout"));
    }

    private void OnTitleMenu(AddonEvent eventType, AddonArgs? args)
    {
        if (isNextAutoLoginHandled)
            return;
        
        if (config.LoginData.Count == 0 && manualLoginInfo == null)
            return;

        if (TaskHelper.AbortByConflictKey(this))
            return;

        // 超域旅行
        if (LobbyDKT->IsAddonAndNodesReady())
            return;

        // 真的可能吗?
        if (GameState.IsLoggedIn)
            return;

        // 登录过一次了 (手动登录不受此限制)
        if (manualLoginInfo           == null               &&
            config.Mode               == BehaviourMode.Once &&
            config.CurrentProcessInfo == CurrentProcessInfo)
            return;

        TaskHelper.Abort();
        
        TaskHelper.Enqueue
        (() =>
            {
                if (CharaSelectListMenu->IsAddonAndNodesReady()) return true;
                if (!TitleMenu->IsAddonAndNodesReady()) return false;

                AgentId.Lobby.SendEvent(0, 4);
                return true;
            }
        );

        TaskHelper.Enqueue
        (() =>
            {
                if (TaskHelper.AbortByConflictKey(this)) return true;
                if (!Throttler.Shared.Throttle("AutoLogin-SelectCharacter", 100)) return false;

                var agent = AgentLobby.Instance();
                if (agent == null) return false;

                if (!CharaSelectListMenu->IsAddonAndNodesReady()) return false;

                var client = (LobbyUIClientEX*)&agent->LobbyData.LobbyUIClient;

                // 手动登录优先
                if (manualLoginInfo is var (charName, charIndex, worldID))
                {
                    var target = charIndex >= 0
                                     ? client->CurrentDataCenterCharacters.FirstOrDefault(x => x.Index == charIndex && x.CurrentWorldID == worldID)
                                     : client->CurrentDataCenterCharacters.FirstOrDefault(x => x.Name  == charName  && x.CurrentWorldID == worldID);

                    // 角色不存在, 回退到配置列表
                    if (target.HomeWorldID == 0)
                        manualLoginInfo = null;
                    else
                    {
                        var contentID = target.ContentID;
                        TaskHelper.Enqueue(() => AgentLobbyEvent.SelectWorldByID(target.CurrentWorldID));
                        TaskHelper.Enqueue(() => agent->WorldId == target.CurrentWorldID);
                        TaskHelper.Enqueue(() => AgentLobbyEvent.SelectCharacter(x => x.ContentId == contentID));

                        manualLoginInfo = null;
                        return true;
                    }
                }

                var counter = config.LoginData.Count + 1;

                foreach (var loginInfo in config.LoginData)
                {
                    counter--;

                    var found = client->CurrentDataCenterCharacters.FirstOrDefault
                    (x => x.Name        == loginInfo.CharacterName &&
                          x.HomeWorldID == loginInfo.WorldID
                    );
                    if (found.HomeWorldID == 0)
                        continue;

                    TaskHelper.Enqueue(() => AgentLobbyEvent.SelectWorldByID(found.CurrentWorldID),                         weight: counter);
                    TaskHelper.Enqueue(() => agent->WorldId == found.CurrentWorldID,                                        weight: counter);
                    TaskHelper.Enqueue(() => AgentLobbyEvent.SelectCharacter(x => x.NameString == loginInfo.CharacterName), weight: counter);
                    break;
                }

                return true;
            }
        );
    }

    #endregion

    private void Swap(int index1, int index2)
    {
        if (index1 < 0                      ||
            index1 > config.LoginData.Count ||
            index2 < 0                      ||
            index2 > config.LoginData.Count) return;

        (config.LoginData[index1], config.LoginData[index2]) =
            (config.LoginData[index2], config.LoginData[index1]);

        TaskHelper.Abort();
        TaskHelper.DelayNext(500);
        TaskHelper.Enqueue(() => config.Save(this));
    }

    private class Config : ModuleConfig
    {
        public List<LoginInfo> LoginData = [];

        public BehaviourMode Mode = BehaviourMode.Once;

        public string CurrentProcessInfo = string.Empty;
    }

    private class LoginInfo
    (
        uint   worldID,
        string characterName
    ) : IEquatable<LoginInfo>
    {
        public uint   WorldID       { get; set; } = worldID;
        public string CharacterName { get; set; } = characterName;

        public bool Equals(LoginInfo? other)
        {
            if (other is null || GetType() != other.GetType())
                return false;

            return WorldID == other.WorldID && CharacterName == other.CharacterName;
        }

        public override bool Equals(object? obj) =>
            Equals(obj as LoginInfo);

        public override int GetHashCode() =>
            HashCode.Combine(WorldID, CharacterName);

        public static bool operator ==(LoginInfo? lhs, LoginInfo? rhs)
        {
            if (lhs is null) return rhs is null;
            return lhs.Equals(rhs);
        }

        public static bool operator !=(LoginInfo lhs, LoginInfo rhs) =>
            !(lhs == rhs);
    }

    private enum BehaviourMode
    {
        Once,
        Repeat
    }

    #region IPC

    [IPCProvider("AutoLogin.MarkNextAutoLoginHandled")]
    private void MarkNextAutoLoginHandled() => 
        isNextAutoLoginHandled = true;

    #endregion
    
    #region 常量

    private const string COMMAND = "/pdrlogin";

    private static readonly Dictionary<BehaviourMode, string> BehaviourModeLoc = new()
    {
        [BehaviourMode.Once]   = Lang.Get("AutoLogin-Once"),
        [BehaviourMode.Repeat] = Lang.Get("AutoLogin-Repeat")
    };

    #endregion
}
