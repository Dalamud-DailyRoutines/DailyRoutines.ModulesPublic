using System;
using System.Collections.Generic;
using System.Linq;
using DailyRoutines.Abstracts;
using DailyRoutines.Managers;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Interface.Colors;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Lumina.Excel.Sheets;

namespace DailyRoutines.ModulesPublic;

public unsafe class AutoLogin : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title               = GetLoc("AutoLoginTitle"),
        Description         = GetLoc("AutoLoginDescription"),
        Category            = ModuleCategories.General,
        ModulesRecommend    = ["AutoSkipLogo"],
        ModulesPrerequisite = ["InstantLogout"]
    };

    private static readonly Dictionary<BehaviourMode, string> BehaviourModeLoc = new()
    {
        [BehaviourMode.Once]   = GetLoc("AutoLogin-Once"),
        [BehaviourMode.Repeat] = GetLoc("AutoLogin-Repeat")
    };

    private const string COMMAND = "/pdrlogin";

    private static Config ModuleConfig = null!;

    private static readonly WorldSelectCombo WorldSelectCombo = new("World");
    
    private static int SelectedCharaIndex;
    private static int DropIndex = -1;

    private static bool   HasLoginOnce;
    private static int    DefaultLoginIndex = -1;
    private static ushort ManualWorldID;
    private static int    ManualCharaIndex = -1;

    protected override void Init()
    {
        ModuleConfig =   LoadConfig<Config>() ?? new();
        TaskHelper   ??= new() { TimeoutMS = 180_000, ShowDebug = true };

        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "_TitleMenu", OnTitleMenu);
        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PostDraw,  "Dialogue",   OnDialogue);
        OnTitleMenu(AddonEvent.PostSetup, null);

        CommandManager.AddCommand(COMMAND, new(OnCommand) { HelpMessage = GetLoc("AutoLogin-CommandHelp") });
        DService.Instance().ClientState.Login += OnLogin;
    }

    protected override void ConfigUI()
    {
        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), $"{GetLoc("Command")}");

        using (ImRaii.PushIndent())
            ImGui.TextUnformatted(GetLoc("AutoLogin-AddCommandHelp", COMMAND, COMMAND));
        
        ImGui.NewLine();
        
        ConflictKeyText();

        ImGui.NewLine();

        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), $"{GetLoc("AutoLogin-LoginInfos")}");

        using (ImRaii.PushIndent())
        {
            ImGui.SetNextItemWidth(300f * GlobalFontScale);
            using (var combo = ImRaii.Combo("###LoginInfosCombo",
                                            GetLoc("AutoLogin-SavedLoginInfosAmount", ModuleConfig.LoginInfos.Count),
                                            ImGuiComboFlags.HeightLarge))
            {
                if (combo)
                {
                    using (ImRaii.Group())
                    {
                        // 服务器选择
                        ImGui.AlignTextToFramePadding();
                        ImGui.TextUnformatted($"{LuminaWrapper.GetAddonText(15834)}:");

                        ImGui.SameLine();
                        ImGui.SetNextItemWidth(200f * GlobalFontScale);
                        WorldSelectCombo.DrawRadio();

                        // 选择当前服务器
                        ImGui.SameLine();
                        if (ImGui.SmallButton(GetLoc("AutoLogin-CurrentWorld")))
                        {
                            if (PresetSheet.Worlds.TryGetValue(GameState.CurrentWorld, out var world))
                                WorldSelectCombo.SelectedID = world.RowId;
                        }

                        // 角色登录索引选择
                        ImGui.AlignTextToFramePadding();
                        ImGui.TextUnformatted($"{GetLoc("AutoLogin-CharacterIndex")}:");

                        ImGui.SameLine();
                        ImGui.SetNextItemWidth(200f * GlobalFontScale);
                        if (ImGui.InputInt("##AutoLogin-EnterCharaIndex", ref SelectedCharaIndex, flags: ImGuiInputTextFlags.EnterReturnsTrue))
                            SelectedCharaIndex = Math.Clamp(SelectedCharaIndex, 0, 8);
                        ImGuiOm.TooltipHover(GetLoc("AutoLogin-CharaIndexInputTooltip"));
                    }

                    ImGui.SameLine();
                    ImGui.Dummy(new(12));

                    ImGui.SameLine();
                    if (ImGuiOm.ButtonIconWithTextVertical(FontAwesomeIcon.Plus, GetLoc("Add")))
                    {
                        if (SelectedCharaIndex is < 0 or > 7 || WorldSelectCombo.SelectedID == 0) return;
                        var info = new LoginInfo(WorldSelectCombo.SelectedID, SelectedCharaIndex);
                        if (!ModuleConfig.LoginInfos.Contains(info))
                        {
                            ModuleConfig.LoginInfos.Add(info);
                            SaveConfig(ModuleConfig);
                        }
                    }

                    ImGuiOm.TooltipHover(GetLoc("AutoLogin-LoginInfoOrderHelp"));

                    ImGui.Separator();
                    ImGui.Separator();

                    for (var i = 0; i < ModuleConfig.LoginInfos.Count; i++)
                    {
                        var info          = ModuleConfig.LoginInfos[i];
                        var worldNullable = LuminaGetter.GetRow<World>(info.WorldID);
                        if (worldNullable == null) continue;
                        var world = worldNullable.Value;
                        using (ImRaii.PushColor(ImGuiCol.Text, i % 2 == 0 ? ImGuiColors.TankBlue : ImGuiColors.DalamudWhite))
                            ImGui.Selectable(
                                $"{i + 1}. {GetLoc("AutoLogin-LoginInfoDisplayText", world.Name.ToString(), world.DataCenter.Value.Name.ToString(), info.CharaIndex)}");

                        using (var source = ImRaii.DragDropSource())
                        {
                            if (source)
                            {
                                if (ImGui.SetDragDropPayload("LoginInfoReorder", []))
                                    DropIndex = i;

                                ImGui.TextColored(ImGuiColors.DalamudYellow,
                                                  GetLoc("AutoLogin-LoginInfoDisplayText",
                                                         world.Name.ToString(),
                                                         world.DataCenter.Value.Name.ToString(),
                                                         info.CharaIndex));
                            }
                        }

                        using (var target = ImRaii.DragDropTarget())
                        {
                            if (target)
                            {
                                if (ImGui.AcceptDragDropPayload("LoginInfoReorder").Handle != null)
                                {
                                    Swap(DropIndex, i);
                                    DropIndex = -1;
                                }
                            }
                        }

                        using (var context = ImRaii.ContextPopupItem($"ContextMenu_{i}"))
                        {
                            if (context)
                            {
                                if (ImGui.Selectable(GetLoc("Delete")))
                                {
                                    ModuleConfig.LoginInfos.Remove(info);
                                    SaveConfig(ModuleConfig);
                                }
                            }
                        }

                        if (i != ModuleConfig.LoginInfos.Count - 1)
                            ImGui.Separator();
                    }
                }
            }
        }

        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), $"{GetLoc("AutoLogin-BehaviourMode")}");

        ImGui.SetNextItemWidth(300f * GlobalFontScale);
        using (ImRaii.PushIndent())
        {
            using (var combo = ImRaii.Combo("###BehaviourModeCombo", BehaviourModeLoc[ModuleConfig.Mode]))
            {
                if (combo)
                {
                    foreach (var mode in BehaviourModeLoc)
                    {
                        if (ImGui.Selectable(mode.Value, mode.Key == ModuleConfig.Mode))
                        {
                            ModuleConfig.Mode = mode.Key;
                            SaveConfig(ModuleConfig);
                        }
                    }
                }
            }

            if (ModuleConfig.Mode == BehaviourMode.Once)
            {
                ImGui.Spacing();
                
                ImGui.TextUnformatted($"{GetLoc("State")}:");

                ImGui.SameLine();
                ImGui.TextColored(HasLoginOnce ? KnownColor.LawnGreen.ToVector4() : KnownColor.OrangeRed.ToVector4(),
                                  HasLoginOnce
                                      ? GetLoc("AutoLogin-LoginOnce")
                                      : GetLoc("AutoLogin-HaveNotLogin"));

                ImGui.SameLine(0, 8f * GlobalFontScale);
                if (ImGui.SmallButton(GetLoc("Clear")))
                    HasLoginOnce = false;
            }
        }
    }
    
    private void OnLogin() => 
        TaskHelper.Abort();

    private void OnCommand(string command, string args)
    {
        args = args.Trim();
        if (string.IsNullOrWhiteSpace(args) || !DService.Instance().ClientState.IsLoggedIn || BoundByDuty)
            return;

        var parts = args.Split(' ');
        switch (parts.Length)
        {
            case 1:
                if (!int.TryParse(args, out var charaIndex0) || charaIndex0 < 0 || charaIndex0 > 8) return;

                ManualWorldID    = (ushort)GameState.HomeWorld;
                ManualCharaIndex = charaIndex0;
                break;
            case 2:
                var world1 = PresetSheet.Worlds.Where(x => x.Value.Name.ToString().Contains(parts[0]))
                                        .OrderBy(x => x.Value.Name.ToString())
                                        .FirstOrDefault()
                                        .Key;
                if (world1 == 0) return;

                if (!int.TryParse(parts[1], out var charaIndex1) || charaIndex1 < 0 || charaIndex1 > 8) return;

                ManualWorldID    = (ushort)world1;
                ManualCharaIndex = charaIndex1;
                break;
            default:
                return;
        }

        TaskHelper.Abort();
        TaskHelper.Enqueue(() => ChatManager.Instance().SendMessage("/logout"));
    }

    private void OnTitleMenu(AddonEvent eventType, AddonArgs? args)
    {
        if (ModuleConfig.LoginInfos.Count <= 0                        ||
            (ModuleConfig.Mode == BehaviourMode.Once && HasLoginOnce) ||
            InterruptByConflictKey(TaskHelper, this)                  ||
            LobbyDKT->IsAddonAndNodesReady()                            ||
            DService.Instance().ClientState.IsLoggedIn)
            return;
        
        TaskHelper.Abort();
        TaskHelper.Enqueue(() =>
        {
            if (CharaSelectListMenu->IsAddonAndNodesReady()) return true;
            if (!TitleMenu->IsAddonAndNodesReady()) return false;

            AgentId.Lobby.SendEvent(0, 4);
            return true;
        });
        
        if (ManualWorldID != 0 && ManualCharaIndex != -1)
            TaskHelper.Enqueue(() => SelectCharacter(ManualWorldID, ManualCharaIndex), "SelectCharaManual");
        else
            TaskHelper.Enqueue(SelectCharacterDefault, "SelectCharaDefault0");
    }

    private static void OnDialogue(AddonEvent type, AddonArgs args)
    {
        var addon = Dialogue;
        if (!addon->IsAddonAndNodesReady()) return;

        var buttonNode = addon->GetComponentButtonById(4);
        if (buttonNode == null) return;

        buttonNode->Click();
    }

    private void SelectCharacterDefault()
    {
        if (ModuleConfig.LoginInfos.Count == 0) return;

        var loginInfo = ModuleConfig.LoginInfos[0];
        DefaultLoginIndex = 0;
        TaskHelper.Enqueue(() => SelectCharacter((ushort)loginInfo.WorldID, loginInfo.CharaIndex),
                           $"选择默认角色_{loginInfo.WorldID}_{loginInfo.CharaIndex}");
    }

    private bool SelectCharacter(ushort worldID, int charaIndex)
    {
        if (InterruptByConflictKey(TaskHelper, this)) return true;
        if (!Throttler.Throttle("AutoLogin-SelectCharacter", 100)) return false;

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
        return true;
    }

    private bool SelectWorld(ushort worldID)
    {
        if (InterruptByConflictKey(TaskHelper, this)) return true;
        if (!Throttler.Throttle("AutoLogin-SelectWorld", 100)) return false;

        var agent = AgentLobby.Instance();
        if (agent == null) return false;

        if (!CharaSelectListMenu->IsAddonAndNodesReady()) return false;

        if (!AgentLobbyEvent.SelectWorldByID(worldID))
        {
            // 没找到
            TaskHelper.Abort();
            if (DefaultLoginIndex != -1 && DefaultLoginIndex < ModuleConfig.LoginInfos.Count)
            {
                var loginInfo = ModuleConfig.LoginInfos[DefaultLoginIndex];
                DefaultLoginIndex++;
                TaskHelper.Enqueue(() => SelectCharacter((ushort)loginInfo.WorldID, loginInfo.CharaIndex),
                                   $"SelectCharaDefault_{loginInfo.WorldID}_{loginInfo.CharaIndex}");
            }
        }

        return true;
    }

    private static void ResetStates()
    {
        HasLoginOnce      = true;
        DefaultLoginIndex = -1;
        ManualWorldID     = 0;
        ManualCharaIndex  = -1;
    }

    private void Swap(int index1, int index2)
    {
        if (index1 < 0 || index1 > ModuleConfig.LoginInfos.Count ||
            index2 < 0 || index2 > ModuleConfig.LoginInfos.Count) return;

        (ModuleConfig.LoginInfos[index1], ModuleConfig.LoginInfos[index2]) =
            (ModuleConfig.LoginInfos[index2], ModuleConfig.LoginInfos[index1]);

        TaskHelper.Abort();
        TaskHelper.DelayNext(500);
        TaskHelper.Enqueue(() => SaveConfig(ModuleConfig));
    }

    protected override void Uninit()
    {
        DService.Instance().ClientState.Login -= OnLogin;
        CommandManager.RemoveCommand(COMMAND);
        
        DService.Instance().AddonLifecycle.UnregisterListener(OnTitleMenu);
        DService.Instance().AddonLifecycle.UnregisterListener(OnDialogue);
        
        ResetStates();
        HasLoginOnce = false;
    }

    private class Config : ModuleConfiguration
    {
        public List<LoginInfo> LoginInfos = [];
        public BehaviourMode   Mode       = BehaviourMode.Once;
    }

    private class LoginInfo(uint worldID, int index) : IEquatable<LoginInfo>
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
}
