using System.Collections.Frozen;
using System.Numerics;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Manager;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Client.UI;
using Lumina.Excel.Sheets;
using OmenTools.Interop.Game.AddonEvent;
using OmenTools.Interop.Game.Lumina;
using OmenTools.Interop.Game.Models.Packets.Upstream;
using OmenTools.OmenService;
using OmenTools.Threading;
using Action = System.Action;
using Control = FFXIVClientStructs.FFXIV.Client.Game.Control.Control;
using ModuleBase = DailyRoutines.Common.Module.Abstractions.ModuleBase;

namespace DailyRoutines.ModulesPublic;

public unsafe class FieldEntryCommand : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title               = Lang.Get("FieldEntryCommandTitle"),
        Description         = Lang.Get("FieldEntryCommandDescription", COMMAND),
        Category            = ModuleCategory.Assist,
        ModulesPrerequisite = ["AutoTalkSkip"]
    };

    public override ModulePermission Permission { get; } = new() { AllDefaultEnabled = true };
    
    private readonly FrozenDictionary<string, (Action EnqueueAction, uint Content)> commandArgs;
    
    private uint redirectTargetZoneInMoon;

    public FieldEntryCommand()
    {
        commandArgs = new Dictionary<string, (Action EnqueueAction, uint Content)>
        {
            ["bozja"]   = (EnqueueBozja, 735),
            ["zadnor"]  = (EnqueueZadonor, 778),
            ["anemos"]  = (EnqueueAnemos, 283),
            ["pagos"]   = (EnqueuePagos, 581),
            ["pyros"]   = (EnqueuePyros, 598),
            ["hydatos"] = (EnqueueHydatos, 639),
            ["diadem"]  = (EnqueueDiadem, 753),
            ["island"]  = (EnqueueIsland, 1),
            ["ardorum"] = (EnqueueArdorum, 2),
            ["phaenna"] = (EnqueuePhaenna, 3),
            ["oizys"]   = (EnqueueOizys, 4),
            ["ocs"]     = (EnqueueOccultCrescent, 1018)
        }.ToFrozenDictionary();
    }

    protected override void Init()
    {
        TaskHelper ??= new() { TimeoutMS = 30_000 };

        GamePacketManager.Instance().RegPreSendPacket(OnPreSendPacket);

        CommandManager.Instance().AddCommand(COMMAND, new(OnCommand) { HelpMessage = Lang.Get("FieldEntryCommand-CommandHelp") });
    }
    
    protected override void Uninit()
    {
        GamePacketManager.Instance().Unreg(OnPreSendPacket);

        CommandManager.Instance().RemoveCommand(COMMAND);

        redirectTargetZoneInMoon = 0;
    }

    protected override void ConfigUI()
    {
        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), $"{Lang.Get("Command")}");

        using var indent = ImRaii.PushIndent();

        ImGui.TextUnformatted($"{COMMAND} → {Lang.Get("FieldEntryCommand-CommandHelp")}");

        ImGui.Spacing();

        using var table = ImRaii.Table
        (
            "ArgsTable",
            2,
            ImGuiTableFlags.Borders,
            (ImGui.GetContentRegionAvail() / 2) with { Y = 0 }
        );
        if (!table) return;

        ImGui.TableSetupColumn(Lang.Get("Argument"),              ImGuiTableColumnFlags.WidthStretch, 10);
        ImGui.TableSetupColumn(LuminaWrapper.GetAddonText(14098), ImGuiTableColumnFlags.WidthStretch, 20);

        ImGui.TableHeadersRow();

        foreach (var command in commandArgs)
        {
            if (!LuminaGetter.TryGetRow<ContentFinderCondition>(command.Value.Content, out var data)) continue;

            ImGui.TableNextRow();

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(command.Key);

            if (ImGui.IsItemHovered())
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);

            if (ImGui.IsItemClicked())
            {
                ImGui.SetClipboardText($"{command.Key}");
                NotifyHelper.Instance().NotificationSuccess($"{Lang.Get("CopiedToClipboard")}: {command.Key}");
            }

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(ContentToPlaceName.TryGetValue(command.Value.Content, out var placeName) ? placeName : data.Name.ToString());
        }
    }

    private void OnCommand(string command, string args)
    {
        if (DService.Instance().Condition.IsBoundByDuty) return;
        TaskHelper.Abort();

        args = args.Trim().ToLowerInvariant();

        foreach (var commandPair in commandArgs)
        {
            if (!LuminaGetter.TryGetRow<ContentFinderCondition>(commandPair.Value.Content, out var data)) continue;

            var contentName = ContentToPlaceName.TryGetValue(commandPair.Value.Content, out var placeName) ? placeName : data.Name.ToString();

            if (args == commandPair.Key || contentName.Contains(args, StringComparison.OrdinalIgnoreCase))
            {
                commandPair.Value.EnqueueAction();
                NotifyHelper.Instance().NotificationInfo(Lang.Get("FieldEntryCommand-Notification", contentName));
                return;
            }
        }
    }

    public void OnPreSendPacket(ref bool isPrevented, int opcode, ref nint packet, ref bool isPrioritize)
    {
        if (redirectTargetZoneInMoon == 0 || GameState.TerritoryType != 959) return;

        if (opcode == UpstreamOpcode.EventCompleteOpcode)
        {
            var data = (EventCompletePackt*)packet;
            if (data->EventID != 0x500AF) return;

            if (data->Category == 0x2000000)
                data->Param1 = redirectTargetZoneInMoon;

            if (data->Category == 0x1000064)
            {
                data->Param0             = redirectTargetZoneInMoon;
                redirectTargetZoneInMoon = 0;
            }
        }
    }

    // 开拓无人岛
    private void EnqueueIsland()
    {
        // 已在无人岛
        if (GameState.TerritoryType == 1055) return;

        // 不在拉诺西亚低地 → 先去拉诺西亚低地
        if (GameState.TerritoryType != 135)
        {
            TaskHelper.Enqueue(() => MovementManager.Instance().TeleportZone(135));
            TaskHelper.Enqueue
            (() => GameState.TerritoryType == 135                        &&
                   UIModule.IsScreenReady()                              &&
                   !DService.Instance().Condition[ConditionFlag.Jumping] &&
                   !MovementManager.Instance().IsManagerBusy
            );
        }

        TaskHelper.Enqueue
        (() =>
            {
                TaskHelper.Enqueue
                (() =>
                    {
                        if (!EventFramework.Instance()->IsEventIDNearby(721694))
                        {
                            TaskHelper.Enqueue(() => MovementManager.Instance().TPSmart_InZone(LowerLaNosceaDefaultPosition), weight: 2);
                            TaskHelper.Enqueue
                            (
                                () => GameState.TerritoryType == 135                        &&
                                      UIModule.IsScreenReady()                              &&
                                      !DService.Instance().Condition[ConditionFlag.Jumping] &&
                                      !MovementManager.Instance().IsManagerBusy,
                                weight: 2
                            );
                        }

                        return true;
                    }
                );

                TaskHelper.Enqueue
                (() =>
                    {
                        if (DService.Instance().ObjectTable.LocalPlayer is null) return false;

                        new EventStartPackt(LocalPlayerState.EntityID, 721694).Send();
                        return true;
                    }
                );

                // 第一次
                TaskHelper.Enqueue(() => AddonSelectStringEvent.Select(0));
                // 第二次
                TaskHelper.Enqueue(() => AddonSelectYesnoEvent.ClickYes());

                // 等待进入无人岛
                TaskHelper.Enqueue(() => GameState.TerritoryType == 1055 && DService.Instance().ObjectTable.LocalPlayer != null);
                TaskHelper.Enqueue(() => MovementManager.Instance().TPSmart_InZone(IslandDefaultPosition));
            }
        );
    }

    // 云冠群岛
    private void EnqueueDiadem()
    {
        MovementManager.Instance().TeleportFirmament();
        TaskHelper.Enqueue(() => GameState.TerritoryType == 886 && !MovementManager.Instance().IsManagerBusy);

        TaskHelper.Enqueue
        (() =>
            {
                TaskHelper.Enqueue(() => GameState.TerritoryType == 886 && Control.GetLocalPlayer() != null);
                TaskHelper.Enqueue(() => MovementManager.Instance().TPSmart_InZone(DiademDefaultPosition));

                TaskHelper.Enqueue
                (() =>
                    {
                        if (!Throttler.Shared.Throttle("FieldEntryCommand-Diadem")) return false;
                        if (!UIModule.IsScreenReady()) return false;

                        new EventStartPackt(LocalPlayerState.EntityID, 721532).Send();
                        return DService.Instance().Condition.IsOccupiedInEvent;
                    }
                );

                // 第一次
                TaskHelper.Enqueue(() => AddonSelectStringEvent.Select(LuminaWrapper.GetContentName(753)));
                // 第二次
                TaskHelper.Enqueue(() => AddonSelectYesnoEvent.ClickYes());
            }
        );
    }

    // 常风之地
    private void EnqueueAnemos() =>
        EnqueueKugane(LuminaWrapper.GetContentName(283));

    // 恒冰之地
    private void EnqueuePagos() =>
        EnqueueKugane(LuminaWrapper.GetContentName(581));

    // 涌火之地
    private void EnqueuePyros() =>
        EnqueueKugane(LuminaWrapper.GetContentName(598));

    // 丰水之地
    private void EnqueueHydatos() =>
        EnqueueKugane(LuminaWrapper.GetContentName(639));

    private void EnqueueKugane(string dutyName)
    {
        // 不在黄金港 → 先去黄金港
        if (GameState.TerritoryType != 628)
        {
            TaskHelper.Enqueue(() => MovementManager.Instance().TPSmart_BetweenZone(628, KuganeDefaultPosition, false, true));
            TaskHelper.Enqueue
            (() => GameState.TerritoryType == 628                        &&
                   UIModule.IsScreenReady()                              &&
                   !DService.Instance().Condition[ConditionFlag.Jumping] &&
                   !MovementManager.Instance().IsManagerBusy
            );
        }

        TaskHelper.Enqueue
        (() =>
            {
                TaskHelper.Enqueue
                (() =>
                    {
                        if (!EventFramework.Instance()->IsEventIDNearby(721355))
                        {
                            TaskHelper.Enqueue(() => MovementManager.Instance().TPSmart_InZone(KuganeDefaultPosition), weight: 2);
                            TaskHelper.Enqueue
                            (
                                () => GameState.TerritoryType == 628                        &&
                                      UIModule.IsScreenReady()                              &&
                                      !DService.Instance().Condition[ConditionFlag.Jumping] &&
                                      !MovementManager.Instance().IsManagerBusy,
                                weight: 2
                            );
                        }

                        return true;
                    }
                );

                TaskHelper.Enqueue
                (() =>
                    {
                        if (DService.Instance().ObjectTable.LocalPlayer is not { } localPlayer) return false;

                        GamePacketManager.Instance().SendPackt(new EventStartPackt(localPlayer.GameObjectID, 721355));
                        return true;
                    }
                );

                // 第一次
                TaskHelper.Enqueue(() => AddonSelectStringEvent.Select(dutyName));
                // 第二次
                TaskHelper.Enqueue(() => AddonSelectYesnoEvent.ClickYes());
            }
        );
    }

    // 博兹雅
    private void EnqueueBozja() =>
        EnqueueGangos(LuminaWrapper.GetContentName(735));

    // 扎杜诺尔
    private void EnqueueZadonor() =>
        EnqueueGangos(LuminaWrapper.GetContentName(778));

    private void EnqueueGangos(string dutyName)
    {
        // 不在甘戈斯 → 先去甘戈斯
        if (GameState.TerritoryType != 915)
        {
            TaskHelper.Enqueue(() => MovementManager.Instance().TeleportZone(915, false, true));
            TaskHelper.Enqueue
            (() => GameState.TerritoryType == 915                        &&
                   UIModule.IsScreenReady()                              &&
                   !DService.Instance().Condition[ConditionFlag.Jumping] &&
                   !MovementManager.Instance().IsManagerBusy
            );
        }

        TaskHelper.Enqueue
        (() =>
            {
                TaskHelper.Enqueue
                (() =>
                    {
                        if (!EventFramework.Instance()->IsEventIDNearby(721601))
                        {
                            TaskHelper.Enqueue(() => MovementManager.Instance().TPSmart_InZone(GangosDefaultPosition), weight: 2);
                            TaskHelper.Enqueue
                            (
                                () => GameState.TerritoryType == 915                        &&
                                      UIModule.IsScreenReady()                              &&
                                      !DService.Instance().Condition[ConditionFlag.Jumping] &&
                                      !MovementManager.Instance().IsManagerBusy,
                                weight: 2
                            );
                        }

                        return true;
                    }
                );

                TaskHelper.Enqueue
                (() =>
                    {
                        if (DService.Instance().ObjectTable.LocalPlayer is not { } localPlayer) return false;

                        GamePacketManager.Instance().SendPackt(new EventStartPackt(localPlayer.GameObjectID, 721601));
                        return true;
                    }
                );

                // 第一次
                TaskHelper.Enqueue(() => AddonSelectStringEvent.Select(dutyName));
                // 第二次
                TaskHelper.Enqueue(() => AddonSelectStringEvent.Select(dutyName));
            }
        );
    }

    private void EnqueueArdorum() =>
        EnqueueCosmic(1237);

    private void EnqueuePhaenna() =>
        EnqueueCosmic(1291);

    private void EnqueueOizys() =>
        EnqueueCosmic(1310);

    private void EnqueueCosmic(uint targetZone)
    {
        if (GameState.TerritoryType == targetZone) return;

        if (GameState.TerritoryType != 959)
        {
            TaskHelper.Enqueue(() => MovementManager.Instance().TPSmart_BetweenZone(959, CosmicDefaultPosition, false, true));
            TaskHelper.Enqueue(() => GameState.TerritoryType == 959 && UIModule.IsScreenReady() && !MovementManager.Instance().IsManagerBusy);
        }

        TaskHelper.Enqueue
        (() =>
            {
                TaskHelper.Enqueue
                (() =>
                    {
                        if (!EventFramework.Instance()->IsEventIDNearby(327855))
                        {
                            TaskHelper.Enqueue(() => MovementManager.Instance().TPSmart_InZone(CosmicDefaultPosition), weight: 2);
                            TaskHelper.Enqueue
                            (
                                () => GameState.TerritoryType == 959                        &&
                                      UIModule.IsScreenReady()                              &&
                                      !DService.Instance().Condition[ConditionFlag.Jumping] &&
                                      !MovementManager.Instance().IsManagerBusy,
                                weight: 2
                            );
                        }

                        return true;
                    }
                );

                TaskHelper.Enqueue(() => redirectTargetZoneInMoon = targetZone);
                TaskHelper.Enqueue(() => new EventStartPackt(LocalPlayerState.EntityID, 327855).Send());
                TaskHelper.Enqueue(() => AddonSelectStringEvent.Select(3));
            }
        );
    }

    private void EnqueueOccultCrescent()
    {
        if (GameState.TerritoryType == 1278) //已经在幻象村了
        {
            TaskHelper.Enqueue(() => GameState.TerritoryType == 1278 && !DService.Instance().Condition[ConditionFlag.Jumping] && !MovementManager.Instance().IsManagerBusy);
            TaskHelper.Enqueue
            (() =>
                {
                    if (!EventFramework.Instance()->IsEventIDNearby(721825))
                    {
                        TaskHelper.Enqueue(() => MovementManager.Instance().TPSmart_InZone(PhantomVillagePosition), weight: 2);
                        TaskHelper.Enqueue
                        (
                            () => GameState.TerritoryType == 1278 && !DService.Instance().Condition[ConditionFlag.Jumping] && !MovementManager.Instance().IsManagerBusy,
                            weight: 2
                        );
                    }

                    return true;
                }
            );

            TaskHelper.Enqueue(() => UIModule.IsScreenReady());
            TaskHelper.Enqueue(() => new EventStartPackt(LocalPlayerState.EntityID, 721825).Send());
            TaskHelper.Enqueue(() => AddonSelectStringEvent.Select(0));
            TaskHelper.Enqueue(() => AddonSelectStringEvent.Select(0));
            return;
        }


        if (GameState.TerritoryType != 1278)
            TaskHelper.Enqueue(() => MovementManager.Instance().TeleportZone(1278));

        TaskHelper.Enqueue(() => GameState.TerritoryType == 1278 && LocalPlayerState.Object != null);
        TaskHelper.Enqueue
        (() =>
            {
                if (!EventFramework.Instance()->IsEventIDNearby(721825))
                {
                    TaskHelper.Enqueue(() => MovementManager.Instance().TPSmart_InZone(PhantomVillagePosition), weight: 2);
                    TaskHelper.Enqueue
                    (
                        () => GameState.TerritoryType == 1278 && !DService.Instance().Condition[ConditionFlag.Jumping] && !MovementManager.Instance().IsManagerBusy,
                        weight: 2
                    );
                }

                return true;
            }
        );

        TaskHelper.Enqueue(() => UIModule.IsScreenReady());
        TaskHelper.Enqueue(() => new EventStartPackt(LocalPlayerState.EntityID, 721825).Send());
        TaskHelper.Enqueue(() => AddonSelectStringEvent.Select(0));
        TaskHelper.Enqueue(() => AddonSelectStringEvent.Select(0));
    }
    
    #region 常量

    private const string COMMAND = "/pdrfe";
    
    private static readonly FrozenDictionary<uint, string> ContentToPlaceName = new Dictionary<uint, string>()
    {
        // 开拓无人岛
        [1] = LuminaWrapper.GetPlaceName(2566),
        // 憧憬湾
        [2] = LuminaWrapper.GetPlaceName(5219),
        // 法恩娜
        [3] = LuminaWrapper.GetPlaceName(5301),
        // 俄匊斯
        [4] = LuminaWrapper.GetPlaceName(5406)
    }.ToFrozenDictionary();

    private static readonly Vector3 GangosDefaultPosition        = new(-33f, 0.15f, -41f);
    private static readonly Vector3 KuganeDefaultPosition        = new(-114.3f, -5f, 150f);
    private static readonly Vector3 DiademDefaultPosition        = new(-19.6f, -16f, 143f);
    private static readonly Vector3 LowerLaNosceaDefaultPosition = new(172, 12, 642);
    private static readonly Vector3 IslandDefaultPosition        = new(-269, 40, 228);
    private static readonly Vector3 CosmicDefaultPosition        = new(-5.3f, -131.1f, -504.0f);
    private static readonly Vector3 PhantomVillagePosition       = new(-71.93f, 5f, -16.02f);

    #endregion
}
