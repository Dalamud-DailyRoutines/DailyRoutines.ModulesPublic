using DailyRoutines.Abstracts;
using DailyRoutines.Managers;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility.Raii;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Lumina.Excel.Sheets;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace DailyRoutines.Modules;

public unsafe class AutoLogin : DailyModuleBase
{
    public override ModuleInfo Info => new()
    {
        Title = GetLoc("AutoLoginTitle"),
        Description = GetLoc("AutoLoginDescription"),
        Category = ModuleCategories.General,
        ModulesRecommend = ["AutoSkipLogo"]
    };

    private static readonly Dictionary<BehaviourMode, string> BehaviourModeLoc = new()
    {
        { BehaviourMode.Once, Lang.Get("AutoLogin-Once") },
        { BehaviourMode.Repeat, Lang.Get("AutoLogin-Repeat") },
    };

    private const string Command = "/pdrlogin";

    private static Config ModuleConfig = null!;
    private static readonly Throttler<string> Throttler = new();

    private static World? SelectedWorld;
    private static string WorldSearchInput = string.Empty;
    private static int SelectedCharaIndex;
    private static int _dropIndex = -1;

    private static bool HasLoginOnce;
    private static int DefaultLoginIndex = -1;
    private static ushort ManualWorldID;
    private static int ManualCharaIndex = -1;

    public override void Init()
    {
        ModuleConfig = LoadConfig<Config>() ?? new();

        TaskHelper ??= new TaskHelper { TimeLimitMS = 60_000 };
        DService.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "_TitleMenu", OnTitleMenu);
        DService.AddonLifecycle.RegisterListener(AddonEvent.PostDraw, "Dialogue", OnDialogue);

        CommandManager.AddCommand(Command, new(OnCommand)
        {
            HelpMessage = Lang.Get("AutoLogin-CommandHelp"),
        });

        if (IsAddonAndNodesReady(TitleMenu)) OnTitleMenu(AddonEvent.PostSetup, null);
    }

    public override void ConfigUI()
    {
        ConflictKeyText();

        ImGui.Spacing();

        ImGui.AlignTextToFramePadding();
        ImGui.Text($"{Lang.Get("AutoLogin-LoginInfos")}:");

        ImGui.SameLine();
        ImGui.SetNextItemWidth(200f * GlobalFontScale);
        using (var combo = ImRaii.Combo("###LoginInfosCombo",
                                        Lang.Get("AutoLogin-SavedLoginInfosAmount", ModuleConfig.LoginInfos.Count),
                                        ImGuiComboFlags.HeightLarge))
        {
            if (combo)
            {
                using (ImRaii.Group())
                {
                    // 服务器选择
                    ImGui.AlignTextToFramePadding();
                    ImGui.Text($"{Lang.Get("AutoLogin-ServerName")}:");

                    ImGui.SameLine();
                    ImGui.SetNextItemWidth(120f * GlobalFontScale);
                    WorldSelectCombo(ref SelectedWorld, ref WorldSearchInput);

                    // 选择当前服务器
                    ImGui.SameLine();
                    if (ImGui.SmallButton(Lang.Get("AutoLogin-CurrentWorld")))
                    {
                        if (PresetSheet.Worlds.TryGetValue(AgentLobby.Instance()->LobbyData.CurrentWorldId, out var world))
                            SelectedWorld = world;
                    }

                    // 角色登录索引选择
                    ImGui.Text($"{Lang.Get("AutoLogin-CharacterIndex")}:");

                    ImGui.SameLine();
                    ImGui.SetNextItemWidth(120f * GlobalFontScale);
                    if (ImGui.InputInt("##AutoLogin-EnterCharaIndex", ref SelectedCharaIndex, 0, 0,
                                       ImGuiInputTextFlags.EnterReturnsTrue))
                        SelectedCharaIndex = Math.Clamp(SelectedCharaIndex, 0, 8);

                    ImGuiOm.TooltipHover(Lang.Get("AutoLogin-CharaIndexInputTooltip"));
                }

                ImGui.SameLine();
                ImGui.Dummy(new(12));

                ImGui.SameLine();
                if (ImGuiOm.ButtonIconWithTextVertical(FontAwesomeIcon.Plus, Lang.Get("Add")))
                {
                    if (SelectedCharaIndex is < 0 or > 7 || SelectedWorld == null) return;
                    var info = new LoginInfo(SelectedWorld.Value.RowId, SelectedCharaIndex);
                    if (!ModuleConfig.LoginInfos.Contains(info))
                    {
                        ModuleConfig.LoginInfos.Add(info);
                        SaveConfig(ModuleConfig);
                    }
                }

                ImGuiOm.TooltipHover(Lang.Get("AutoLogin-LoginInfoOrderHelp"));

                ImGui.Separator();
                ImGui.Separator();

                for (var i = 0; i < ModuleConfig.LoginInfos.Count; i++)
                {
                    var info  = ModuleConfig.LoginInfos[i];
                    var worldNullable = LuminaGetter.GetRow<World>(info.WorldID);
                    if (worldNullable == null) continue;
                    var world = worldNullable.Value;
                    using (ImRaii.PushColor(ImGuiCol.Text, i % 2 == 0 ? ImGuiColors.TankBlue : ImGuiColors.DalamudWhite))
                    {
                        ImGui.Selectable(
                            $"{i + 1}. {Lang.Get("AutoLogin-LoginInfoDisplayText", world.Name.ExtractText(), world.DataCenter.Value.Name.ExtractText(), info.CharaIndex)}");
                    }

                    using (var source = ImRaii.DragDropSource())
                    {
                        if (source)
                        {
                            if (ImGui.SetDragDropPayload("LoginInfoReorder", nint.Zero, 0)) _dropIndex = i;
                            ImGui.TextColored(ImGuiColors.DalamudYellow,
                                              Lang.Get("AutoLogin-LoginInfoDisplayText",
                                                                   world.Name.ExtractText(),
                                                                   world.DataCenter.Value.Name.ExtractText(),
                                                                   info.CharaIndex));
                        }
                    }

                    using (var target = ImRaii.DragDropTarget())
                    {
                        if (target)
                        {
                            if (ImGui.AcceptDragDropPayload("LoginInfoReorder").NativePtr != null)
                            {
                                Swap(_dropIndex, i);
                                _dropIndex = -1;
                            }
                        }
                    }

                    using (var context = ImRaii.ContextPopupItem($"ContextMenu_{i}"))
                    {
                        if (context)
                        {
                            if (ImGui.Selectable(Lang.Get("Delete")))
                            {
                                ModuleConfig.LoginInfos.Remove(info);
                                SaveConfig(ModuleConfig);
                            }
                        }
                    }

                    if (i != ModuleConfig.LoginInfos.Count - 1) ImGui.Separator();
                }
            }
        }

        ImGui.SameLine();
        ImGui.Text($"{Lang.Get("AutoLogin-BehaviourMode")}:");

        ImGui.SameLine();
        ImGui.SetNextItemWidth(100f * GlobalFontScale);
        using (var combo = ImRaii.Combo("###BehaviourModeCombo", BehaviourModeLoc[ModuleConfig.Mode]))
        {
            if (combo)
            {
                foreach (var mode in BehaviourModeLoc)
                    if (ImGui.Selectable(mode.Value, mode.Key == ModuleConfig.Mode))
                    {
                        ModuleConfig.Mode = mode.Key;
                        SaveConfig(ModuleConfig);
                    }
            }
        }

        if (ModuleConfig.Mode == BehaviourMode.Once)
        {
            ImGui.SameLine();
            ImGui.Text($"{Lang.Get("AutoLogin-LoginState")}:");

            ImGui.SameLine();
            ImGui.TextColored(HasLoginOnce ? ImGuiColors.HealerGreen : ImGuiColors.DPSRed,
                              HasLoginOnce
                                  ? Lang.Get("AutoLogin-LoginOnce")
                                  : Lang.Get("AutoLogin-HaveNotLogin"));

            ImGui.SameLine();
            if (ImGui.SmallButton(Lang.Get("Refresh"))) HasLoginOnce = false;
        }

        ImGui.Spacing();

        ImGui.TextColored(LightSkyBlue, $"{GetLoc("Command")}:");

        ImGui.SameLine();
        ImGui.Text(GetLoc("AutoLogin-AddCommandHelp", Command, Command));
    }

    private void OnCommand(string command, string args)
    {
        args = args.Trim();
        if (string.IsNullOrWhiteSpace(args)) return;
        if (!DService.ClientState.IsLoggedIn || DService.ClientState.LocalPlayer == null || 
            BoundByDuty) return;

        var parts = args.Split(' ');
        switch (parts.Length)
        {
            case 1:
                if (!int.TryParse(args, out var charaIndex0) || charaIndex0 < 0 || charaIndex0 > 8) return;

                ManualWorldID = (ushort)DService.ClientState.LocalPlayer.HomeWorld.RowId;
                ManualCharaIndex = charaIndex0;
                break;
            case 2:
                var world1 = PresetSheet.Worlds.FirstOrDefault(x => x.Value.Name.ExtractText().Contains(parts[0])).Key;
                if (world1 == 0) return;
                if (!int.TryParse(parts[1], out var charaIndex1) || charaIndex1 < 0 || charaIndex1 > 8) return;

                ManualWorldID = (ushort)world1;
                ManualCharaIndex = charaIndex1;
                break;
            default:
                return;
        }

        TaskHelper.Abort();
        TaskHelper.Enqueue(Logout);
    }

    private static bool? Logout()
    {
        if (!Throttler.Throttle("Logout")) return false;
        if (!DService.ClientState.IsLoggedIn) return true;

        if (SelectYesno == null)
        {
            ChatHelper.Instance.SendMessage("/logout");
            return false;
        }

        var title = Marshal.PtrToStringUTF8((nint)SelectYesno->AtkValues[0].String.Value);
        if (!title.Contains(LuminaGetter.GetRow<Addon>(115)!.Value.Text.ExtractText()))
        {
            ClickSelectYesnoNo();
            return false;
        }

        ClickSelectYesnoYes();
        return true;
    }

    private void OnTitleMenu(AddonEvent eventType, AddonArgs? args)
    {
        if (ModuleConfig.LoginInfos.Count <= 0) return;
        if (ModuleConfig.Mode == BehaviourMode.Once && HasLoginOnce) return;
        if (InterruptByConflictKey(TaskHelper, this)) return;
        if (LobbyDKT != null && IsAddonAndNodesReady(LobbyDKT)) return;

        SendEvent(AgentId.Lobby, 0, 4);

        TaskHelper.Abort();
        if (ManualWorldID != 0 && ManualCharaIndex != -1)
            TaskHelper.Enqueue(() => SelectCharacter(ManualWorldID, ManualCharaIndex), "SelectCharaManual");
        else
            TaskHelper.Enqueue(SelectCharacterDefault, "SelectCharaDefault0");
    }

    private void OnDialogue(AddonEvent type, AddonArgs args)
    {
        if (!Throttler.Throttle("AutoLogin-OnDialogue", 5000)) return;
        if (InterruptByConflictKey(TaskHelper, this)) return;

        TaskHelper.Abort();
        TaskHelper.Enqueue(() =>
        {
            var addon = Dialogue;
            if (addon == null || !IsAddonAndNodesReady(addon)) return false;

            var buttonNode = addon->GetButtonNodeById(4);
            if (buttonNode == null) return false;

            buttonNode->ClickAddonButton(addon);
            return true;
        }, null, null, null, 1);
    }

    private void SelectCharacterDefault()
    {
        foreach (var loginInfo in ModuleConfig.LoginInfos)
        {
            DefaultLoginIndex = 0;
            TaskHelper.Enqueue(() => SelectCharacter((ushort)loginInfo.WorldID, loginInfo.CharaIndex), $"SelectCharaDefault_{loginInfo.WorldID}_{loginInfo.CharaIndex}");
            break;
        }
    }

    private bool? SelectCharacter(ushort worldID, int charaIndex)
    {
        if (InterruptByConflictKey(TaskHelper, this)) return true;
        if (!Throttler.Throttle("SelectCharacter", 100)) return false;
        
        var agent = AgentLobby.Instance();
        if (agent == null) return false;

        var addon = CharaSelectListMenu;
        if (addon == null || !IsAddonAndNodesReady(addon)) return false;

        if (agent->WorldId != worldID)
        {
            TaskHelper.Enqueue(() => SelectWorld(worldID), "SelectWorld", null, null, 2);
            TaskHelper.Enqueue(() => SelectCharacter(worldID, charaIndex));
            return true;
        }

        Callback(addon, true, 21, charaIndex);
        Callback(addon, true, 29, 0, charaIndex);
        Callback(addon, true, 21, charaIndex);

        TaskHelper.Enqueue(() => ClickSelectYesnoYes());
        TaskHelper.Enqueue(ResetStates);
        return true;
    }

    private bool? SelectWorld(ushort worldID)
    {
        if (InterruptByConflictKey(TaskHelper, this)) return true;
        if (!Throttler.Throttle("SelectWorld", 100)) return false;

        var agent = AgentLobby.Instance();
        if (agent == null) return false;

        var addon = CharaSelectWorldServer;
        if (addon == null) return false;

        for (var i = 0; i < 16; i++)
        {
            Callback(addon, true, 24, 0, i);

            if (agent->WorldId == worldID)
            {
                Callback(addon, true, 25, 0, i);
                return true;
            }
        }

        TaskHelper.Abort();
        if (DefaultLoginIndex != -1 && DefaultLoginIndex < ModuleConfig.LoginInfos.Count)
        {
            var loginInfo = ModuleConfig.LoginInfos[DefaultLoginIndex];
            DefaultLoginIndex++;
            TaskHelper.Enqueue(() => SelectCharacter((ushort)loginInfo.WorldID, loginInfo.CharaIndex), $"SelectCharaDefault_{loginInfo.WorldID}_{loginInfo.CharaIndex}");
        }
        return true;
    }

    private static void ResetStates()
    {
        HasLoginOnce = true;
        DefaultLoginIndex = -1;
        ManualWorldID = 0;
        ManualCharaIndex = -1;
    }

    private void Swap(int index1, int index2)
    {
        if (index1 < 0 || index1 > ModuleConfig.LoginInfos.Count || 
            index2 < 0 || index2 > ModuleConfig.LoginInfos.Count) return;
        (ModuleConfig.LoginInfos[index1], ModuleConfig.LoginInfos[index2]) = (ModuleConfig.LoginInfos[index2], ModuleConfig.LoginInfos[index1]);

        TaskHelper.Abort();

        TaskHelper.DelayNext(500);
        TaskHelper.Enqueue(() => SaveConfig(ModuleConfig));
    }

    public override void Uninit()
    {
        DService.AddonLifecycle.UnregisterListener(OnTitleMenu);
        DService.AddonLifecycle.UnregisterListener(OnDialogue);
        CommandManager.RemoveCommand(Command);
        ResetStates();
        HasLoginOnce = false;

        base.Uninit();
    }

    private class Config : ModuleConfiguration
    {
        public List<LoginInfo> LoginInfos = [];
        public BehaviourMode Mode = BehaviourMode.Once;
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

        public override bool Equals(object? obj) { return Equals(obj as LoginInfo); }

        public override int GetHashCode() { return HashCode.Combine(WorldID, CharaIndex); }

        public static bool operator ==(LoginInfo? lhs, LoginInfo? rhs)
        {
            if (lhs is null) return rhs is null;
            return lhs.Equals(rhs);
        }

        public static bool operator !=(LoginInfo lhs, LoginInfo rhs) { return !(lhs == rhs); }
    }

    private enum BehaviourMode
    {
        Once,
        Repeat,
    }
}
