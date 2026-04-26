using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using DailyRoutines.Manager;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Interface.Colors;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Lumina.Excel.Sheets;
using OmenTools.ImGuiOm.Widgets.Combos;
using OmenTools.Info.Game.Data;
using OmenTools.Interop.Game.AgentEvent;
using OmenTools.Interop.Game.Lumina;
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

    private Config config = null!;

    private readonly WorldSelectCombo worldSelectCombo = new("World");

    private int selectedCharaIndex;
    private int dropIndex = -1;

    private bool              hasLoginOnce;
    private int               defaultLoginIndex = -1;
    private LoginAttemptState loginAttemptState;
    private long              loginAttemptStartTicks;
    private ushort            manualWorldID;
    private int               manualCharaIndex = -1;

    protected override void Init()
    {
        config =   Config.Load(this) ?? new();
        TaskHelper   ??= new() { TimeoutMS = 180_000, ShowDebug = true };

        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "_TitleMenu", OnTitleMenu);
        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PostDraw,  "Dialogue",   OnDialogue);
        OnTitleMenu(AddonEvent.PostSetup, null);

        CommandManager.Instance().AddCommand(COMMAND, new(OnCommand) { HelpMessage = Lang.Get("AutoLogin-CommandHelp") });
        DService.Instance().ClientState.Login += OnLogin;
    }
    
    protected override void Uninit()
    {
        DService.Instance().ClientState.Login -= OnLogin;
        CommandManager.Instance().RemoveCommand(COMMAND);

        DService.Instance().AddonLifecycle.UnregisterListener(OnTitleMenu);
        DService.Instance().AddonLifecycle.UnregisterListener(OnDialogue);

        ResetStates();
        hasLoginOnce = false;
    }

    protected override void ConfigUI()
    {
        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), $"{Lang.Get("Command")}");

        using (ImRaii.PushIndent())
            ImGui.TextUnformatted(Lang.Get("AutoLogin-AddCommandHelp", COMMAND, COMMAND));

        ImGui.NewLine();

        ImGuiOm.ConflictKeyText();

        ImGui.NewLine();

        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), $"{Lang.Get("AutoLogin-LoginInfos")}");

        using (ImRaii.PushIndent())
        {
            ImGui.SetNextItemWidth(300f * GlobalUIScale);

            using (var combo = ImRaii.Combo
                   (
                       "###LoginInfosCombo",
                       Lang.Get("AutoLogin-SavedLoginInfosAmount", config.LoginInfos.Count),
                       ImGuiComboFlags.HeightLarge
                   ))
            {
                if (combo)
                {
                    using (ImRaii.Group())
                    {
                        // 服务器选择
                        ImGui.AlignTextToFramePadding();
                        ImGui.TextUnformatted($"{LuminaWrapper.GetAddonText(15834)}:");

                        ImGui.SameLine();
                        ImGui.SetNextItemWidth(200f * GlobalUIScale);
                        worldSelectCombo.DrawRadio();

                        // 选择当前服务器
                        ImGui.SameLine();

                        if (ImGui.SmallButton(Lang.Get("AutoLogin-CurrentWorld")))
                        {
                            if (Sheets.Worlds.TryGetValue(GameState.CurrentWorld, out var world))
                                worldSelectCombo.SelectedID = world.RowId;
                        }

                        // 角色登录索引选择
                        ImGui.AlignTextToFramePadding();
                        ImGui.TextUnformatted($"{Lang.Get("AutoLogin-CharacterIndex")}:");

                        ImGui.SameLine();
                        ImGui.SetNextItemWidth(200f * GlobalUIScale);
                        ImGui.InputInt("##AutoLogin-EnterCharaIndex", ref selectedCharaIndex);
                        selectedCharaIndex = Math.Clamp(selectedCharaIndex, 0, 7);
                        ImGuiOm.TooltipHover(Lang.Get("AutoLogin-CharaIndexInputTooltip"));
                    }

                    ImGui.SameLine();
                    ImGui.Dummy(new(12));

                    ImGui.SameLine();

                    if (ImGuiOm.ButtonIconWithTextVertical(FontAwesomeIcon.Plus, Lang.Get("Add")))
                    {
                        if (selectedCharaIndex is < 0 or > 7 || worldSelectCombo.SelectedID == 0) return;
                        var info = new LoginInfo(worldSelectCombo.SelectedID, selectedCharaIndex);

                        if (!config.LoginInfos.Contains(info))
                        {
                            config.LoginInfos.Add(info);
                            config.Save(this);
                        }
                    }

                    ImGuiOm.TooltipHover(Lang.Get("AutoLogin-LoginInfoOrderHelp"));

                    ImGui.Separator();
                    ImGui.Separator();

                    for (var i = 0; i < config.LoginInfos.Count; i++)
                    {
                        var info          = config.LoginInfos[i];
                        var worldNullable = LuminaGetter.GetRow<World>(info.WorldID);
                        if (worldNullable == null) continue;
                        var world = worldNullable.Value;

                        using (ImRaii.PushColor(ImGuiCol.Text, i % 2 == 0 ? ImGuiColors.TankBlue : ImGuiColors.DalamudWhite))
                        {
                            ImGui.Selectable
                            (
                                $"{i + 1}. {Lang.Get("AutoLogin-LoginInfoDisplayText", world.Name.ToString(), world.DataCenter.Value.Name.ToString(), info.CharaIndex)}"
                            );
                        }

                        using (var source = ImRaii.DragDropSource())
                        {
                            if (source)
                            {
                                if (ImGui.SetDragDropPayload("LoginInfoReorder", []))
                                    dropIndex = i;

                                ImGui.TextColored
                                (
                                    ImGuiColors.DalamudYellow,
                                    Lang.Get
                                    (
                                        "AutoLogin-LoginInfoDisplayText",
                                        world.Name.ToString(),
                                        world.DataCenter.Value.Name.ToString(),
                                        info.CharaIndex
                                    )
                                );
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
                                    config.LoginInfos.Remove(info);
                                    config.Save(this);
                                }
                            }
                        }

                        if (i != config.LoginInfos.Count - 1)
                            ImGui.Separator();
                    }
                }
            }
        }

        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), $"{Lang.Get("AutoLogin-BehaviourMode")}");

        ImGui.SetNextItemWidth(300f * GlobalUIScale);

        using (ImRaii.PushIndent())
        {
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

            if (config.Mode == BehaviourMode.Once)
            {
                ImGui.Spacing();

                ImGui.TextUnformatted($"{Lang.Get("State")}:");

                ImGui.SameLine();
                ImGui.TextColored
                (
                    hasLoginOnce ? KnownColor.LawnGreen.ToVector4() : KnownColor.OrangeRed.ToVector4(),
                    hasLoginOnce
                        ? Lang.Get("AutoLogin-LoginOnce")
                        : Lang.Get("AutoLogin-HaveNotLogin")
                );

                ImGui.SameLine(0, 8f * GlobalUIScale);
                if (ImGui.SmallButton(Lang.Get("Clear")))
                    hasLoginOnce = false;
            }
        }
    }

    private void OnLogin()
    {
        ResetStates();
        TaskHelper.Abort();
    }

    private void OnCommand(string command, string args)
    {
        args = args.Trim();
        if (string.IsNullOrWhiteSpace(args) || !DService.Instance().ClientState.IsLoggedIn || DService.Instance().Condition.IsBoundByDuty)
            return;

        var parts = args.Split(' ');

        switch (parts.Length)
        {
            case 1:
                if (!int.TryParse(args, out var charaIndex0) || charaIndex0 < 0 || charaIndex0 > 8) return;

                manualWorldID    = (ushort)GameState.HomeWorld;
                manualCharaIndex = charaIndex0;
                break;
            case 2:
                var world1 = Sheets.Worlds.Where(x => x.Value.Name.ToString().Contains(parts[0]))
                                   .OrderBy(x => x.Value.Name.ToString())
                                   .FirstOrDefault()
                                   .Key;
                if (world1 == 0) return;

                if (!int.TryParse(parts[1], out var charaIndex1) || charaIndex1 < 0 || charaIndex1 > 8) return;

                manualWorldID    = (ushort)world1;
                manualCharaIndex = charaIndex1;
                break;
            default:
                return;
        }

        TaskHelper.Abort();
        TaskHelper.Enqueue(() => ChatManager.Instance().SendMessage("/logout"));
    }

    private void OnTitleMenu(AddonEvent eventType, AddonArgs? args)
    {
        if (config.LoginInfos.Count <= 0                      ||
            config.Mode == BehaviourMode.Once && hasLoginOnce ||
            TaskHelper.AbortByConflictKey(this)                     ||
            LobbyDKT->IsAddonAndNodesReady()                        ||
            DService.Instance().ClientState.IsLoggedIn)
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

        if (manualWorldID != 0 && manualCharaIndex != -1)
            TaskHelper.Enqueue(() => SelectCharacter(manualWorldID, manualCharaIndex), "SelectCharaManual");
        else
            TaskHelper.Enqueue(SelectCharacterDefault, "SelectCharaDefault0");
    }

    private void OnDialogue(AddonEvent type, AddonArgs args)
    {
        var addon = Dialogue;
        if (!addon->IsAddonAndNodesReady()) return;

        var buttonNode = addon->GetComponentButtonById(4);
        if (buttonNode == null) return;

        buttonNode->Click();

        if (IsDefaultLoginFlow && loginAttemptState != LoginAttemptState.None)
            loginAttemptState = LoginAttemptState.Failed;
    }

    private void SelectCharacterDefault()
    {
        defaultLoginIndex = 0;
        EnqueueNextDefaultCharacter();
    }

    private void EnqueueNextDefaultCharacter()
    {
        if (config.LoginInfos.Count == 0) return;

        loginAttemptState = LoginAttemptState.None;

        if (defaultLoginIndex < 0) return;

        if (defaultLoginIndex >= config.LoginInfos.Count)
        {
            if (config.Mode != BehaviourMode.Repeat) return;
            defaultLoginIndex = 0;
        }

        var loginInfo = config.LoginInfos[defaultLoginIndex];
        defaultLoginIndex++;
        TaskHelper.Enqueue
        (
            () => SelectCharacter((ushort)loginInfo.WorldID, loginInfo.CharaIndex),
            $"选择默认角色_{loginInfo.WorldID}_{loginInfo.CharaIndex}"
        );
    }

    private bool SelectCharacter(ushort worldID, int charaIndex)
    {
        if (TaskHelper.AbortByConflictKey(this)) return true;
        if (!Throttler.Shared.Throttle("AutoLogin-SelectCharacter", 100)) return false;

        var agent = AgentLobby.Instance();
        if (agent == null) return false;

        var addon = CharaSelectListMenu;
        if (!addon->IsAddonAndNodesReady()) return false;

        // 不对应, 重新选
        if (agent->WorldId != worldID)
        {
            TaskHelper.Enqueue(() => SelectWorld(worldID),                 "重新选择世界", weight: 2);
            TaskHelper.Enqueue(() => SelectCharacter(worldID, charaIndex), "重新选择角色");
            return true;
        }

        AgentLobbyEvent.SelectCharacterByIndex((uint)charaIndex);
        if (config.Mode == BehaviourMode.Repeat && IsDefaultLoginFlow)
        {
            loginAttemptState      = LoginAttemptState.Waiting;
            loginAttemptStartTicks = Environment.TickCount64;
            TaskHelper.Enqueue
            (
                WaitForLoginAttemptResult,
                $"WaitLoginAttemptResult_{worldID}_{charaIndex}",
                timeoutAction: EnqueueNextDefaultCharacter
            );
        }

        return true;
    }

    private bool WaitForLoginAttemptResult()
    {
        if (DService.Instance().ClientState.IsLoggedIn) return true;

        if (loginAttemptState == LoginAttemptState.Failed)
        {
            EnqueueNextDefaultCharacter();
            return true;
        }

        if (!CharaSelectListMenu->IsAddonAndNodesReady())
        {
            loginAttemptState = LoginAttemptState.CharaSelectLeft;
            return false;
        }

        if (loginAttemptState == LoginAttemptState.Waiting &&
            Environment.TickCount64 - loginAttemptStartTicks >= LoginAttemptNoProgressTimeoutMS)
        {
            EnqueueNextDefaultCharacter();
            return true;
        }

        if (loginAttemptState != LoginAttemptState.CharaSelectLeft) return false;

        EnqueueNextDefaultCharacter();
        return true;
    }

    private bool SelectWorld(ushort worldID)
    {
        if (TaskHelper.AbortByConflictKey(this)) return true;
        if (!Throttler.Shared.Throttle("AutoLogin-SelectWorld", 100)) return false;

        var agent = AgentLobby.Instance();
        if (agent == null) return false;

        if (!CharaSelectListMenu->IsAddonAndNodesReady()) return false;

        if (!AgentLobbyEvent.SelectWorldByID(worldID))
        {
            // 没找到
            EnqueueNextDefaultCharacter();
        }

        return true;
    }

    private void ResetStates()
    {
        hasLoginOnce      = true;
        defaultLoginIndex = -1;
        loginAttemptState = LoginAttemptState.None;
        manualWorldID     = 0;
        manualCharaIndex  = -1;
    }

    private bool IsDefaultLoginFlow =>
        manualWorldID == 0 && manualCharaIndex == -1;

    private void Swap(int index1, int index2)
    {
        if (index1 < 0                             ||
            index1 > config.LoginInfos.Count ||
            index2 < 0                             ||
            index2 > config.LoginInfos.Count) return;

        (config.LoginInfos[index1], config.LoginInfos[index2]) =
            (config.LoginInfos[index2], config.LoginInfos[index1]);

        TaskHelper.Abort();
        TaskHelper.DelayNext(500);
        TaskHelper.Enqueue(() => config.Save(this));
    }
    
    private class Config : ModuleConfig
    {
        public List<LoginInfo> LoginInfos = [];
        public BehaviourMode   Mode       = BehaviourMode.Once;
    }

    private class LoginInfo
    (
        uint worldID,
        int  index
    ) : IEquatable<LoginInfo>
    {
        public uint WorldID    { get; set; } = worldID;
        public int  CharaIndex { get; set; } = index;

        public bool Equals(LoginInfo? other)
        {
            if (other is null || GetType() != other.GetType())
                return false;

            return WorldID == other.WorldID && CharaIndex == other.CharaIndex;
        }

        public override bool Equals(object? obj) =>
            Equals(obj as LoginInfo);

        public override int GetHashCode() =>
            HashCode.Combine(WorldID, CharaIndex);

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

    private enum LoginAttemptState
    {
        None,
        Waiting,
        CharaSelectLeft,
        Failed
    }
    
    #region 常量

    private const string COMMAND = "/pdrlogin";
    private const int    LoginAttemptNoProgressTimeoutMS = 5_000;

    private static readonly Dictionary<BehaviourMode, string> BehaviourModeLoc = new()
    {
        [BehaviourMode.Once]   = Lang.Get("AutoLogin-Once"),
        [BehaviourMode.Repeat] = Lang.Get("AutoLogin-Repeat")
    };

    #endregion
}
