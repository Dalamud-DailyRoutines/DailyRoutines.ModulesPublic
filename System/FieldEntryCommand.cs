using System;
using System.Collections.Generic;
using System.Numerics;
using DailyRoutines.Abstracts;
using DailyRoutines.Helpers;
using DailyRoutines.Infos;
using DailyRoutines.Managers;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using Lumina.Excel.Sheets;
using Action = System.Action;

namespace DailyRoutines.ModulesPublic;

public class FieldEntryCommand : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title               = GetLoc("FieldEntryCommandTitle"),
        Description         = GetLoc("FieldEntryCommandDescription", Command),
        Category            = ModuleCategories.System,
        ModulesPrerequisite = ["AutoTalkSkip"]
    };

    public override ModulePermission Permission { get; } = new() { NeedAuth = true };

    private static TaskHelper? TPHelper;

    private const string Command = "/pdrfe";

    private static readonly Dictionary<string, (Action EnqueueAction, uint Content)> CommandArgs = new()
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
        ["ocs"]     = (EnqueueOccultCrescent, 1018),
    };

    private static readonly Dictionary<uint, string> ContentToPlaceName = new()
    {
        // 开拓无人岛
        [1] = LuminaWrapper.GetPlaceName(2566),
        // 宇宙探索 1
        [2] = LuminaWrapper.GetPlaceName(5219),
    };

    private static readonly Vector3 GangosDefaultPosition        = new(-33f, 0.15f, -41f);
    private static readonly Vector3 KuganeDefaultPosition        = new(-114.3f, -5f, 150f);
    private static readonly Vector3 DiademDefaultPosition        = new(-19.6f, -16f, 143f);
    private static readonly Vector3 LowerLaNosceaDefaultPosition = new(172, 12, 642);
    private static readonly Vector3 IslandDefaultPosition        = new(-269, 40, 228);
    private static readonly Vector3 CosmicDefaultPosition        = new(0f, -187.42f, -412f);
    private static readonly Vector3 PhantomVillagePosition1      = new(207.15f, -17.96f, 60.74f);
    private static readonly Vector3 PhantomVillagePosition2      = new(-71.93f, 5f, -16.02f);

    protected override void Init()
    {
        TPHelper ??= new() { TimeLimitMS = 30_000 };

        CommandManager.AddCommand(Command, new(OnCommand) { HelpMessage = GetLoc("FieldEntryCommand-CommandHelp")} );
    }

    protected override void ConfigUI()
    {
        ImGui.TextColored(LightSkyBlue, $"{GetLoc("Command")}:");
        
        using var indent = ImRaii.PushIndent();
        using var table = ImRaii.Table("ArgsTable", 2, ImGuiTableFlags.Borders,
                                       (ImGui.GetContentRegionAvail() / 2) with { Y = 0 });
        if (!table) return;

        ImGui.TableSetupColumn(GetLoc("Argument"),                ImGuiTableColumnFlags.WidthStretch, 10);
        ImGui.TableSetupColumn(LuminaWrapper.GetAddonText(14098), ImGuiTableColumnFlags.WidthStretch, 20);

        ImGui.TableHeadersRow();

        foreach (var command in CommandArgs)
        {
            if (!LuminaGetter.TryGetRow<ContentFinderCondition>(command.Value.Content, out var data)) continue;

            ImGui.TableNextRow();

            ImGui.TableNextColumn();
            ImGui.Text(command.Key);
            
            if (ImGui.IsItemHovered())
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            if (ImGui.IsItemClicked())
            {
                ImGui.SetClipboardText($"{command.Key}");
                NotificationSuccess($"{GetLoc("CopiedToClipboard")}: {command.Key}");
            }

            ImGui.TableNextColumn();
            ImGui.Text(ContentToPlaceName.TryGetValue(command.Value.Content, out var placeName) ? placeName : data.Name.ExtractText());
        }
    }

    private static void OnCommand(string command, string args)
    {
        if (BoundByDuty) return;
        TPHelper.Abort();

        args = args.Trim().ToLowerInvariant();

        foreach (var commandPair in CommandArgs)
        {
            if (!LuminaGetter.TryGetRow<ContentFinderCondition>(commandPair.Value.Content, out var data)) continue;
            
            var contentName = ContentToPlaceName.TryGetValue(commandPair.Value.Content, out var placeName) ? placeName : data.Name.ExtractText();
            if (args == commandPair.Key || contentName.Contains(args, StringComparison.OrdinalIgnoreCase))
            {
                commandPair.Value.EnqueueAction();
                NotificationInfo(GetLoc("FieldEntryCommand-Notification", contentName));
                return;
            }
        }
    }

    // 开拓无人岛
    private static void EnqueueIsland()
    {
        // 已在无人岛
        if (DService.ClientState.TerritoryType == 1055) return;

        // 不在拉诺西亚低地 → 先去拉诺西亚低地
        if (DService.ClientState.TerritoryType != 135)
        {
            TPHelper.Enqueue(() => MovementManager.TeleportZone(135));
            TPHelper.Enqueue(() => DService.ClientState.TerritoryType == 135  && IsScreenReady() &&
                                   !DService.Condition[ConditionFlag.Jumping] && !MovementManager.IsManagerBusy);
        }

        TPHelper.Enqueue(() =>
        {
            TPHelper.Enqueue(() =>
            {
                if (!EventFrameworkHelper.IsEventIDNearby(721694))
                {
                    TPHelper.Enqueue(() => MovementManager.TPSmart_InZone(LowerLaNosceaDefaultPosition), weight: 2);
                    TPHelper.Enqueue(() => DService.ClientState.TerritoryType == 135  && IsScreenReady() &&
                                           !DService.Condition[ConditionFlag.Jumping] && !MovementManager.IsManagerBusy, weight: 2);
                }
                return true;
            });

            TPHelper.Enqueue(() =>
            {
                if (DService.ObjectTable.LocalPlayer is not { } localPlayer) return false;

                GamePacketManager.SendPackt(new EventStartPackt(localPlayer.GameObjectId, 721694));
                return true;
            });

            // 第一次
            TPHelper.Enqueue(() => ClickSelectString(0));
            // 第二次
            TPHelper.Enqueue(() => ClickSelectYesnoYes());

            // 等待进入无人岛
            TPHelper.Enqueue(() => DService.ClientState.TerritoryType == 1055 && DService.ObjectTable.LocalPlayer != null);
            TPHelper.Enqueue(() => MovementManager.TPSmart_InZone(IslandDefaultPosition));
        });
    }

    // 云冠群岛
    private static unsafe void EnqueueDiadem()
    {
        MovementManager.TeleportFirmament();
        TPHelper.Enqueue(() => DService.ClientState.TerritoryType == 886 && !MovementManager.IsManagerBusy);

        TPHelper.Enqueue(() =>
        {
            TPHelper.Enqueue(() => DService.ClientState.TerritoryType == 886 && Control.GetLocalPlayer() != null);
            TPHelper.Enqueue(() => MovementManager.TPSmart_InZone(DiademDefaultPosition));

            TPHelper.Enqueue(() =>
            {
                if (!Throttler.Throttle("FieldEntryCommand-Diadem")) return false;
                if (!IsScreenReady()) return false;

                new EventStartPackt(LocalPlayerState.EntityID, 721532).Send();
                return OccupiedInEvent;
            });

            // 第一次
            TPHelper.Enqueue(() => ClickSelectString(LuminaWrapper.GetContentName(753)));
            // 第二次
            TPHelper.Enqueue(() => ClickSelectYesnoYes());
        });
    }

    // 常风之地
    private static void EnqueueAnemos()
        => EnqueueKugane(LuminaWrapper.GetContentName(283));

    // 恒冰之地
    private static void EnqueuePagos()
        => EnqueueKugane(LuminaWrapper.GetContentName(581));

    // 涌火之地
    private static void EnqueuePyros()
        => EnqueueKugane(LuminaWrapper.GetContentName(598));

    // 丰水之地
    private static void EnqueueHydatos()
        => EnqueueKugane(LuminaWrapper.GetContentName(639));
    
    private static void EnqueueKugane(string dutyName)
    {
        // 不在黄金港 → 先去黄金港
        if (DService.ClientState.TerritoryType != 628)
        {
            TPHelper.Enqueue(() => MovementManager.TPSmart_BetweenZone(628, KuganeDefaultPosition, false, true));
            TPHelper.Enqueue(() => DService.ClientState.TerritoryType == 628  && IsScreenReady() &&
                                   !DService.Condition[ConditionFlag.Jumping] && !MovementManager.IsManagerBusy);
        }

        TPHelper.Enqueue(() =>
        {
            TPHelper.Enqueue(() =>
            {
                if (!EventFrameworkHelper.IsEventIDNearby(721355))
                {
                    TPHelper.Enqueue(() => MovementManager.TPSmart_InZone(KuganeDefaultPosition), weight: 2);
                    TPHelper.Enqueue(() => DService.ClientState.TerritoryType == 628  && IsScreenReady() &&
                                           !DService.Condition[ConditionFlag.Jumping] && !MovementManager.IsManagerBusy, weight: 2);
                }
                
                return true;
            });

            TPHelper.Enqueue(() =>
            {
                if (DService.ObjectTable.LocalPlayer is not { } localPlayer) return false;

                GamePacketManager.SendPackt(new EventStartPackt(localPlayer.GameObjectId, 721355));
                return true;
            });

            // 第一次
            TPHelper.Enqueue(() => ClickSelectString(dutyName));
            // 第二次
            TPHelper.Enqueue(() => ClickSelectYesnoYes());
        });
    }

    // 博兹雅
    private static void EnqueueBozja()
        => EnqueueGangos(LuminaWrapper.GetContentName(735));

    // 扎杜诺尔
    private static void EnqueueZadonor()
        => EnqueueGangos(LuminaWrapper.GetContentName(778));
    
    private static void EnqueueGangos(string dutyName)
    {
        // 不在甘戈斯 → 先去甘戈斯
        if (DService.ClientState.TerritoryType != 915)
        {
            TPHelper.Enqueue(() => MovementManager.TeleportZone(915, false, true));
            TPHelper.Enqueue(() => DService.ClientState.TerritoryType == 915  && IsScreenReady() &&
                                   !DService.Condition[ConditionFlag.Jumping] && !MovementManager.IsManagerBusy);
        }

        TPHelper.Enqueue(() =>
        {
            TPHelper.Enqueue(() =>
            {
                if (!EventFrameworkHelper.IsEventIDNearby(721601))
                {
                    TPHelper.Enqueue(() => MovementManager.TPSmart_InZone(GangosDefaultPosition), weight: 2);
                    TPHelper.Enqueue(() => DService.ClientState.TerritoryType == 915  && IsScreenReady() &&
                                           !DService.Condition[ConditionFlag.Jumping] && !MovementManager.IsManagerBusy, weight: 2);
                }
                return true;
            });

            TPHelper.Enqueue(() =>
            {
                if (DService.ObjectTable.LocalPlayer is not { } localPlayer) return false;

                GamePacketManager.SendPackt(new EventStartPackt(localPlayer.GameObjectId, 721601));
                return true;
            });

            // 第一次
            TPHelper.Enqueue(() => ClickSelectString(dutyName));
            // 第二次
            TPHelper.Enqueue(() => ClickSelectString(dutyName));
        });
    }
    
    private static void EnqueueArdorum()
        => EnqueueCosmic(LuminaWrapper.GetPlaceName(5219));
    
    private static void EnqueueCosmic(string dutyName)
    {
        if (GameState.TerritoryIntendedUse == 60) return;

        if (DService.ClientState.TerritoryType != 959)
        {
            TPHelper.Enqueue(() => MovementManager.TPSmart_BetweenZone(959, CosmicDefaultPosition, false, true));
            TPHelper.Enqueue(() => DService.ClientState.TerritoryType == 959 && IsScreenReady() && !MovementManager.IsManagerBusy);
        }

        TPHelper.Enqueue(() =>
        {
            TPHelper.Enqueue(() =>
            {
                if (!EventFrameworkHelper.IsEventIDNearby(721817))
                {
                    TPHelper.Enqueue(() => MovementManager.TPSmart_InZone(CosmicDefaultPosition), weight: 2);
                    TPHelper.Enqueue(() => DService.ClientState.TerritoryType == 959  && IsScreenReady() &&
                                           !DService.Condition[ConditionFlag.Jumping] && !MovementManager.IsManagerBusy, weight: 2);
                }
                
                return true;
            });

            TPHelper.Enqueue(() => new EventStartPackt(LocalPlayerState.EntityID, 721817).Send());
            TPHelper.Enqueue(() => ClickSelectString(dutyName));
            TPHelper.Enqueue(() => ClickSelectString(0));
        });
    }
    
    private static void EnqueueOccultCrescent()
    {
        if (DService.ClientState.TerritoryType == 1278) //已经在幻象村了
        {
            TPHelper.Enqueue(() => DService.ClientState.TerritoryType == 1278 &&
                                   !DService.Condition[ConditionFlag.Jumping] && 
                                   !MovementManager.IsManagerBusy);
            
            TPHelper.Enqueue(() =>
            {
                if (!EventFrameworkHelper.IsEventIDNearby(721825))
                {
                    TPHelper.Enqueue(() => MovementManager.TPSmart_InZone(PhantomVillagePosition2), weight: 2);
                    TPHelper.Enqueue(() => DService.ClientState.TerritoryType == 1278 &&
                                           !DService.Condition[ConditionFlag.Jumping] &&
                                           !MovementManager.IsManagerBusy,
                                     weight: 2);
                }
                
                return true;
            });
            
            TPHelper.Enqueue(() => IsScreenReady());
            TPHelper.Enqueue(() => new EventStartPackt(LocalPlayerState.EntityID, 721825).Send());
            TPHelper.Enqueue(() => ClickSelectString(0));
            TPHelper.Enqueue(() => ClickSelectString(0));
            return;
        }
        
        
        if (DService.ClientState.TerritoryType != 1185)
        {
            TPHelper.Enqueue(() => MovementManager.TeleportZone(1185));
            TPHelper.Enqueue(() => DService.ClientState.TerritoryType == 1185 &&
                                   !DService.Condition[ConditionFlag.Jumping] &&
                                   !MovementManager.IsManagerBusy);
        }
        
        TPHelper.Enqueue(() =>
        {
            if (!EventFrameworkHelper.IsEventIDNearby(131592))
            {
                TPHelper.Enqueue(() => MovementManager.TPSmart_InZone(PhantomVillagePosition1), weight: 2);
                TPHelper.Enqueue(() => DService.ClientState.TerritoryType == 1185 &&
                                       !DService.Condition[ConditionFlag.Jumping] &&
                                       !MovementManager.IsManagerBusy, weight: 2);
            }
                
            return true;
        });
        
        TPHelper.Enqueue(() => IsScreenReady());
        TPHelper.Enqueue(() => new EventStartPackt(LocalPlayerState.EntityID, 131592).Send());
        TPHelper.Enqueue(() => ClickSelectYesnoYes());

        TPHelper.Enqueue(() => DService.ClientState.TerritoryType == 1278 &&
                               !DService.Condition[ConditionFlag.Jumping] &&
                               IsScreenReady()                            &&
                               !MovementManager.IsManagerBusy);
        
        TPHelper.Enqueue(() =>
        {
            if (!EventFrameworkHelper.IsEventIDNearby(721825))
            {
                TPHelper.Enqueue(() => MovementManager.TPSmart_InZone(PhantomVillagePosition2), weight: 2);
                TPHelper.Enqueue(() => DService.ClientState.TerritoryType == 1278 &&
                                       !DService.Condition[ConditionFlag.Jumping] &&
                                       !MovementManager.IsManagerBusy, weight: 2);
            }
                
            return true;
        });
        
        TPHelper.Enqueue(() => IsScreenReady());
        TPHelper.Enqueue(() => new EventStartPackt(LocalPlayerState.EntityID, 721825).Send());
        TPHelper.Enqueue(() => ClickSelectString(0));
        TPHelper.Enqueue(() => ClickSelectString(0));
    }

    protected override void Uninit()
    {
        CommandManager.RemoveCommand(Command);

        TPHelper?.Abort();
        TPHelper = null;
    }
}
