using System.Collections.Generic;
using System.Numerics;
using DailyRoutines.Abstracts;
using DailyRoutines.Managers;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Game.Gui.Dtr;
using Dalamud.Plugin.Services;
using Dalamud.Utility.Numerics;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Lumina.Excel.Sheets;
using TerritoryIntendedUse = FFXIVClientStructs.FFXIV.Client.Enums.TerritoryIntendedUse;

namespace DailyRoutines.ModulesPublic;

public unsafe class FastInstanceZoneChange : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title            = GetLoc("FastInstanceZoneChangeTitle"),
        Description      = GetLoc("FastInstanceZoneChangeDescription", Command),
        Category         = ModuleCategories.System,
        Author           = ["AtmoOmen", "KirisameVanilla"],
        ModulesRecommend = ["InstantTeleport"]
    };

    public override ModulePermission Permission { get; } = new() { AllDefaultEnabled = true };

    // 其他地方也没法换线
    private static readonly HashSet<TerritoryIntendedUse> ValidUses =
    [
        TerritoryIntendedUse.Overworld,
        TerritoryIntendedUse.Town
    ];

    private const string Command = "insc";

    private static Config        ModuleConfig = null!;
    private static IDtrBarEntry? Entry;

    protected override void Init()
    {
        TaskHelper ??= new() { TimeLimitMS = 30_000 };
        ModuleConfig = LoadConfig<Config>() ?? new();

        CommandManager.AddSubCommand(Command, new(OnCommand) { HelpMessage = GetLoc("FastInstanceZoneChange-CommandHelp") });

        Overlay ??= new(this);
        Overlay.WindowName = GetLoc("FastInstanceZoneChangeTitle");

        if (ModuleConfig.AddDtrEntry)
            HandleDtrEntry(true);

        FrameworkManager.Reg(OnUpdate, throttleMS: 5_000);
    }

    protected override void ConfigUI()
    {
        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), $"{GetLoc("Command")}:");
        using (ImRaii.PushIndent())
            ImGui.Text($"/pdr {Command} \u2192 {GetLoc("FastInstanceZoneChange-CommandHelp")}");

        ImGui.Spacing();

        if (ImGui.Checkbox(GetLoc("FastInstanceZoneChange-TeleportIfNotNearAetheryte"),
                           ref ModuleConfig.TeleportIfNotNearAetheryte))
            SaveConfig(ModuleConfig);

        if (ImGui.Checkbox(GetLoc("FastInstanceZoneChange-ConstantlyTry"), ref ModuleConfig.ConstantlyTry))
            SaveConfig(ModuleConfig);

        if (ImGui.Checkbox(GetLoc("FastInstanceZoneChange-MountAfterChange"), ref ModuleConfig.MountAfterChange))
            SaveConfig(ModuleConfig);

        if (ImGui.Checkbox(GetLoc("FastInstanceZoneChange-AddDtrEntry"), ref ModuleConfig.AddDtrEntry))
        {
            SaveConfig(ModuleConfig);
            HandleDtrEntry(ModuleConfig.AddDtrEntry);
        }

        if (ModuleConfig.AddDtrEntry)
        {
            if (ImGui.Checkbox(GetLoc("FastInstanceZoneChange-CloseAfterUsage"), ref ModuleConfig.CloseAfterUsage))
                SaveConfig(ModuleConfig);
        }
    }

    protected override void OverlayUI()
    {
        if (!InstancesManager.IsInstancedArea)
        {
            Overlay.IsOpen = false;
            return;
        }

        if (DService.KeyState[VirtualKey.ESCAPE])
        {
            Overlay.IsOpen = false;
            if (SystemMenu != null)
                SystemMenu->Close(true);
            return;
        }

        if (!ValidUses.Contains(GameState.TerritoryIntendedUse))
        {
            Overlay.IsOpen = false;
            return;
        }
        
        ImGui.SetWindowPos((ImGui.GetMainViewport().Size / 2) - new Vector2(0.5f));
        
        var count = InstancesManager.GetInstancesCount();
        for (uint i = 1; i <= count; i++)
        {
            if (i == InstancesManager.CurrentInstance) continue;
            if (ImGui.Button($"{GetLoc("FastInstanceZoneChange-SwitchInstance", i.ToSEChar())}") |
                DService.KeyState[(VirtualKey)(48 + i)])
            {
                if (TaskHelper.IsBusy || BetweenAreas || DService.Condition[ConditionFlag.Casting]) continue;
                ChatManager.SendMessage($"/pdr insc {i}");
                if (ModuleConfig.CloseAfterUsage) 
                    Overlay.IsOpen = false;
            }
        }
    }

    private static void OnUpdate(IFramework framework)
    {
        if (!ModuleConfig.AddDtrEntry || Entry == null || BetweenAreas) return;
        
        Entry.Shown = InstancesManager.IsInstancedArea;
    }

    private static void OnTerritoryChanged(ushort zone = 0)
    {
        try
        {
            if (!ModuleConfig.AddDtrEntry || Entry == null) return;
        
            Entry.Text = !InstancesManager.IsInstancedArea
                             ? string.Empty
                             : GetLoc("AutoMarksFinder-RelayInstanceDisplay", InstancesManager.CurrentInstance.ToSEChar());
            Entry.Shown = InstancesManager.IsInstancedArea;
        }
        catch
        {
            // ignored
        }
    }
    
    private void HandleDtrEntry(bool isAdd)
    {
        if (isAdd && Entry == null)
        {
            Entry         ??= DService.DtrBar.Get("DailyRoutines-FastInstanceZoneChange");
            Entry.OnClick +=  _ => Overlay.IsOpen ^= true;
            Entry.Shown   =   false;
            Entry.Tooltip =   GetLoc("FastInstanceZoneChange-DtrEntryTooltip");
            
            DService.ClientState.TerritoryChanged += OnTerritoryChanged;
            OnTerritoryChanged();
            FrameworkManager.Reg(OnUpdate, throttleMS: 5_000);
            return;
        }

        if (!isAdd && Entry != null)
        {
            Entry.Remove();
            Entry = null;
            
            FrameworkManager.Unreg(OnUpdate);
            DService.ClientState.TerritoryChanged -= OnTerritoryChanged;
        }
    }

    protected override void Uninit()
    {
        HandleDtrEntry(false);
        CommandManager.RemoveSubCommand(Command);
    }

    private void OnCommand(string command, string args)
    {
        args = args.Trim().ToLowerInvariant();

        var publicInstance = UIState.Instance()->PublicInstance;
        if (args.Length == 0)
        {
            if (publicInstance.IsInstancedArea())
                Overlay.IsOpen ^= true;
            else 
                NotificationError(GetLoc("FastInstanceZoneChange-Notice-NoInstanceZones"));
            return;
        }

        if (args == "abort")
        {
            TaskHelper.Abort();
            NotificationInfo(GetLoc("FastInstanceZoneChange-Notice-Aborted"));
            return;
        }

        if (!uint.TryParse(args, out var targetInstance))
        {
            NotificationError(GetLoc("FastInstanceZoneChange-Notice-InvalidArgs", args));
            return;
        }

        if (!publicInstance.IsInstancedArea())
        {
            NotificationError(GetLoc("FastInstanceZoneChange-Notice-NoInstanceZones"));
            return;
        }

        if (publicInstance.InstanceId == targetInstance)
        {
            NotificationError(GetLoc("FastInstanceZoneChange-Notice-CurrentlyInSameInstance", targetInstance));
            return;
        }

        TaskHelper.Abort();
        NotificationInfo(GetLoc("FastInstanceZoneChange-Notice-Change", targetInstance));

        var isAnyAetheryteNearby = IsAnyAetheryteNearby(out _);
        if (ModuleConfig.TeleportIfNotNearAetheryte && !isAnyAetheryteNearby)
        {
            TaskHelper.Enqueue(
                () => MovementManager.TeleportNearestAetheryte(default, DService.ClientState.TerritoryType, true),
                weight:  2);
            TaskHelper.DelayNext(500, string.Empty, false, 2);
            TaskHelper.Enqueue(() =>
            {
                if (!Throttler.Throttle("FastInstanceZoneChange-WaitTeleportFinish")) return false;
                return IsAnyAetheryteNearby(out _);
            }, weight:  2);
        }

        var currentMountID = 0U;
        if (DService.Condition[ConditionFlag.Mounted])
            currentMountID = DService.ObjectTable.LocalPlayer.CurrentMount?.RowId ?? 0;

        TaskHelper.Enqueue(() =>
        {
            if (!DService.Condition[ConditionFlag.Mounted]) return true;
            if (!Throttler.Throttle("FastInstanceZoneChange-WaitDismount", 100)) return false;

            ExecuteCommandManager.ExecuteCommand(ExecuteCommandFlag.Dismount);
            if (MovementManager.TryDetectGroundDownwards(DService.ObjectTable.LocalPlayer.Position.WithY(300),
                                                         out var hitInfo) ?? false)
            {
                MovementManager.TPMountAddress(hitInfo.Point with { Y = hitInfo.Point.Y - 0.5f });
                UseActionManager.UseAction(ActionType.GeneralAction, 9);
            }

            return !DService.Condition[ConditionFlag.Mounted];
        }, weight:  2);

        if (ModuleConfig.ConstantlyTry)
            TaskHelper.Enqueue(() => EnqueueInstanceChange(targetInstance, 0), weight:  2);
        else
            TaskHelper.Enqueue(() => ChangeInstanceZone(targetInstance), weight:  2);

        if (ModuleConfig.MountAfterChange)
        {
            TaskHelper.Enqueue(() =>
            {
                if (InstancesManager.CurrentInstance != targetInstance ||
                    BetweenAreas || !IsScreenReady()) return false;

                if (!LuminaGetter.TryGetRow<TerritoryType>(DService.ClientState.TerritoryType, out var zoneData) ||
                    !zoneData.Mount) return false;
                
                if (currentMountID != 0)
                    UseActionManager.UseAction(ActionType.Mount, currentMountID);
                else
                    UseActionManager.UseAction(ActionType.GeneralAction, 9);

                return true;
            });
        }
    }

    public void EnqueueInstanceChange(uint i, uint tryTimes)
    {
        // 等待上一次切换完成
        TaskHelper.Enqueue(() => IsAddonAndNodesReady(SelectString) || !DService.Condition[ConditionFlag.BetweenAreas], weight:  2);

        // 检测切换情况
        TaskHelper.Enqueue(() =>
        {
            if (!Throttler.Throttle("FastInstanceZoneChange-DetectInstances")) return false;
            if (DService.Condition[ConditionFlag.BetweenAreas])
            {
                ExecuteCommandManager.ExecuteCommand(ExecuteCommandFlag.TerritoryTransport);
                return false;
            }

            var publicInstance = UIState.Instance()->PublicInstance;
            if (!publicInstance.IsInstancedArea() || publicInstance.InstanceId == i)
                TaskHelper.RemoveAllTasks(2);

            return true;
        }, weight:  2);

        // 实际切换指令
        TaskHelper.Enqueue(() => ChangeInstanceZone(i), weight:  2);

        // 发送提示信息
        if (tryTimes > 0)
            TaskHelper.Enqueue(() => NotificationInfo(GetLoc("FastInstanceZoneChange-Notice-ChangeTimes", tryTimes)), weight:  2);

        // 延迟下一次检测
        TaskHelper.DelayNext(1_500, string.Empty, false, 2);
        TaskHelper.Enqueue(() => EnqueueInstanceChange(i, tryTimes++), weight:  2);
    }

    internal static void ChangeInstanceZone(uint i)
    {
        if (!UIState.Instance()->PublicInstance.IsInstancedArea() ||
            !IsAnyAetheryteNearby(out var eventID)) return;

        var packetStart = new EventStartPackt(DService.ObjectTable.LocalPlayer.GameObjectID, eventID);
        var packetFinish = new EventCompletePackt(eventID, 33554432, 7, i + 1);

        GamePacketManager.SendPackt(packetStart);
        GamePacketManager.SendPackt(packetFinish);
    }

    private static bool IsAnyAetheryteNearby(out uint eventID)
    {
        eventID = 0;

        foreach (var eve in EventFramework.Instance()->EventHandlerModule.EventHandlerMap)
        {
            if (eve.Item2.Value->Info.EventId.ContentId != EventHandlerContent.Aetheryte) continue;

            foreach (var obj in eve.Item2.Value->EventObjects)
            {
                if (obj.Value->NameString == LuminaGetter.GetRow<Aetheryte>(0)!.Value.Singular.ExtractText())
                {
                    eventID = eve.Item2.Value->Info.EventId;
                    return true;
                }
            }
        }

        return false;
    }

    private class Config : ModuleConfiguration
    {
        public bool TeleportIfNotNearAetheryte = true;
        public bool ConstantlyTry = true;
        public bool MountAfterChange = true;
        public bool AddDtrEntry = true;
        public bool CloseAfterUsage = true;
    }
}
