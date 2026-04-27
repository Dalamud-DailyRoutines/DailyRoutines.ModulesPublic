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

    private string selectedCharaName = string.Empty;
    private int dropIndex = -1;

    private bool              hasLoginOnce;
    private int               defaultLoginIndex = -1;
    private ushort            manualWorldID;
    private int               manualCharaIndex = -1;
    private string            manualCharaName = string.Empty;

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

                        // 角色名选择
                        ImGui.AlignTextToFramePadding();
                        ImGui.TextUnformatted($"{Lang.Get("AutoLogin-CharacterName")}:");

                        ImGui.SameLine();
                        ImGui.SetNextItemWidth(200f * GlobalUIScale);
                        ImGui.InputText("##AutoLogin-EnterCharaName", ref selectedCharaName, 32);

                        ImGui.SameLine();
                        if (ImGui.SmallButton(Lang.Get("AutoLogin-CurrentCharacter")) &&
                            DService.Instance().ObjectTable.LocalPlayer is { } localPlayer)
                            selectedCharaName = localPlayer.Name.ToString();
                    }

                    ImGui.SameLine();
                    ImGui.Dummy(new(12));

                    ImGui.SameLine();

                    if (ImGuiOm.ButtonIconWithTextVertical(FontAwesomeIcon.Plus, Lang.Get("Add")))
                    {
                        var charaName = selectedCharaName.Trim();
                        if (string.IsNullOrWhiteSpace(charaName) || worldSelectCombo.SelectedID == 0) return;
                        var info = new LoginInfo(worldSelectCombo.SelectedID, charaName);

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
                                $"{i + 1}. {GetLoginInfoDisplayText(world, info)}"
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
                                    GetLoginInfoDisplayText(world, info)
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

        var parts = args.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        switch (parts.Length)
        {
            case 1:
                if (string.IsNullOrWhiteSpace(parts[0])) return;

                if (!TrySetManualCharacter(parts[0])) return;
                manualWorldID = (ushort)GameState.HomeWorld;
                break;
            case 2:
                var world1 = Sheets.Worlds.Where(x => x.Value.Name.ToString().Contains(parts[0]))
                                   .OrderBy(x => x.Value.Name.ToString())
                                   .FirstOrDefault()
                                   .Key;
                if (world1 == 0) return;

                if (string.IsNullOrWhiteSpace(parts[1])) return;

                if (!TrySetManualCharacter(parts[1])) return;
                manualWorldID = (ushort)world1;
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

        if (manualWorldID != 0 && (manualCharaIndex != -1 || !string.IsNullOrWhiteSpace(manualCharaName)))
            TaskHelper.Enqueue
            (
                () => string.IsNullOrWhiteSpace(manualCharaName)
                          ? SelectCharacter(manualWorldID, manualCharaIndex)
                          : SelectCharacter(manualWorldID, manualCharaName),
                "SelectCharaManual"
            );
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
    }

    private void SelectCharacterDefault()
    {
        defaultLoginIndex = 0;
        EnqueueNextDefaultCharacter();
    }

    private bool TrySetManualCharacter(string chara)
    {
        if (int.TryParse(chara, out var charaIndex))
        {
            if (charaIndex is < 0 or > 8) return false;

            manualCharaIndex = charaIndex;
            manualCharaName  = string.Empty;
            return true;
        }

        manualCharaIndex = -1;
        manualCharaName  = chara;
        return true;
    }

    private void EnqueueNextDefaultCharacter()
    {
        if (config.LoginInfos.Count == 0) return;

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
            () => SelectCharacter(loginInfo),
            $"选择默认角色_{loginInfo.WorldID}_{loginInfo.DisplayName}"
        );
    }

    private bool SelectCharacter(LoginInfo loginInfo)
    {
        if (!string.IsNullOrWhiteSpace(loginInfo.CharaName))
            return SelectCharacter((ushort)loginInfo.WorldID, loginInfo.CharaName);

        if (loginInfo.CharaIndex is < 0 or > 8)
        {
            if (config.Mode == BehaviourMode.Repeat && IsDefaultLoginFlow)
                EnqueueNextDefaultCharacter();

            return true;
        }

        return SelectCharacter((ushort)loginInfo.WorldID, loginInfo.CharaIndex);
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
        EnqueueLoginAttemptResultWait($"WaitLoginAttemptResult_{worldID}_{charaIndex}");
        return true;
    }

    private bool SelectCharacter(ushort worldID, string charaName)
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
            TaskHelper.Enqueue(() => SelectWorld(worldID),                "重新选择世界", weight: 2);
            TaskHelper.Enqueue(() => SelectCharacter(worldID, charaName), "重新选择角色");
            return true;
        }

        if (!AgentLobbyEvent.SelectCharacter(entry => IsCharacterMatched(entry, worldID, charaName)))
        {
            if (config.Mode == BehaviourMode.Repeat && IsDefaultLoginFlow)
                EnqueueNextDefaultCharacter();

            return true;
        }

        EnqueueLoginAttemptResultWait($"WaitLoginAttemptResult_{worldID}_{charaName}");
        return true;
    }

    private void EnqueueLoginAttemptResultWait(string taskName)
    {
        if (config.Mode == BehaviourMode.Repeat && IsDefaultLoginFlow)
        {
            var startTicks = Environment.TickCount64;
            var hasLeftCharaSelect = new[] { false };
            TaskHelper.Enqueue
            (
                () => WaitForLoginAttemptResult(startTicks, hasLeftCharaSelect),
                taskName,
                timeoutAction: EnqueueNextDefaultCharacter
            );
        }
    }

    private bool WaitForLoginAttemptResult(long startTicks, bool[] hasLeftCharaSelect)
    {
        if (DService.Instance().ClientState.IsLoggedIn) return true;

        if (!CharaSelectListMenu->IsAddonAndNodesReady())
        {
            hasLeftCharaSelect[0] = true;
            return false;
        }

        if (hasLeftCharaSelect[0] ||
            Environment.TickCount64 - startTicks >= LoginAttemptNoProgressTimeoutMS)
        {
            EnqueueNextDefaultCharacter();
            return true;
        }

        return false;
    }

    private static bool IsCharacterMatched(CharaSelectCharacterEntry entry, ushort worldID, string charaName) =>
        (entry.CurrentWorldId == worldID || entry.HomeWorldId == worldID) &&
        string.Equals(entry.NameString, charaName.Trim(), StringComparison.OrdinalIgnoreCase);

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
        manualWorldID     = 0;
        manualCharaIndex  = -1;
        manualCharaName   = string.Empty;
    }

    private bool IsDefaultLoginFlow =>
        manualWorldID == 0 && manualCharaIndex == -1 && string.IsNullOrWhiteSpace(manualCharaName);

    private static string GetLoginInfoDisplayText(World world, LoginInfo info) =>
        string.IsNullOrWhiteSpace(info.CharaName)
            ? Lang.Get
              (
                  "AutoLogin-LoginInfoDisplayText",
                  world.Name.ToString(),
                  world.DataCenter.Value.Name.ToString(),
                  info.CharaIndex
              )
            : Lang.Get
              (
                  "AutoLogin-LoginInfoNameDisplayText",
                  world.Name.ToString(),
                  world.DataCenter.Value.Name.ToString(),
                  info.CharaName
              );

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

    private class LoginInfo : IEquatable<LoginInfo>
    {
        public uint   WorldID    { get; set; }
        public int    CharaIndex { get; set; } = -1;
        public string CharaName  { get; set; } = string.Empty;

        public LoginInfo()
        {
        }

        public LoginInfo(uint worldID, string charaName)
        {
            WorldID   = worldID;
            CharaName = charaName.Trim();
        }

        public LoginInfo(uint worldID, int charaIndex)
        {
            WorldID    = worldID;
            CharaIndex = charaIndex;
        }

        public string DisplayName =>
            string.IsNullOrWhiteSpace(CharaName) ? CharaIndex.ToString() : CharaName;

        public bool Equals(LoginInfo? other)
        {
            if (other is null || GetType() != other.GetType())
                return false;

            if (WorldID != other.WorldID) return false;

            return string.IsNullOrWhiteSpace(CharaName) && string.IsNullOrWhiteSpace(other.CharaName)
                       ? CharaIndex == other.CharaIndex
                       : string.Equals(CharaName, other.CharaName, StringComparison.OrdinalIgnoreCase);
        }

        public override bool Equals(object? obj) =>
            Equals(obj as LoginInfo);

        public override int GetHashCode() =>
            HashCode.Combine
            (
                WorldID,
                string.IsNullOrWhiteSpace(CharaName)
                    ? CharaIndex
                    : StringComparer.OrdinalIgnoreCase.GetHashCode(CharaName)
            );

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

    #region 常量

    private const string COMMAND = "/pdrlogin";
    private const int    LoginAttemptNoProgressTimeoutMS = 2_000;

    private static readonly Dictionary<BehaviourMode, string> BehaviourModeLoc = new()
    {
        [BehaviourMode.Once]   = Lang.Get("AutoLogin-Once"),
        [BehaviourMode.Repeat] = Lang.Get("AutoLogin-Repeat")
    };

    #endregion
}
