using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using DailyRoutines.Manager;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Fate;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using OmenTools.Info.Game.Enums;
using OmenTools.Interop.Game;
using OmenTools.Interop.Game.Models;
using OmenTools.OmenService;
using OmenTools.Threading.TaskHelper;

namespace DailyRoutines.ModulesPublic;

public unsafe class QueueCombatTeleport : ModuleBase
{
    private static readonly CompSig CanUseTeleportSig =
        new("84 C0 0F 84 ?? ?? ?? ?? 48 8D 0D ?? ?? ?? ?? E8 ?? ?? ?? ?? 84 C0 0F 84 ?? ?? ?? ?? 48 89 5C 24");

    // test al, al → mov al, 1
    private static readonly MemoryPatch CanUseTeleportPatch = new(CanUseTeleportSig.Get(), [0xB0, 0x01]);

    private static readonly CompSig CanUseTeleportMapSig =
        new("84 C0 0F 44 CA 8B C1 48 83 C4 ?? C3 CC CC CC CC CC CC CC CC CC CC CC CC CC CC CC CC CC");

    // test → or, cmovz → nop
    private static readonly MemoryPatch CanUseTeleportMapPatch =
        new(CanUseTeleportMapSig.Get(), [0x08, 0xC0, 0x90, 0x90, 0x90]);

    private static (uint ID, uint SubID)? QueuedTeleport;

    private static Config?     ModuleConfig;
    private static TaskHelper? TeleportHelper;

    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("QueueCombatTeleportTitle"),
        Description = Lang.Get("QueueCombatTeleportDescription"),
        Category    = ModuleCategory.System
    };

    public override ModulePermission Permission { get; } = new() { NeedAuth = true };

    protected override void Init()
    {
        ModuleConfig   =   Config.Load(this) ?? new();
        TeleportHelper ??= new() { TimeoutMS = 60_000 };

        CanUseTeleportPatch.Enable();
        CanUseTeleportMapPatch.Enable();

        UseActionManager.Instance().RegPreUseAction(OnPreUseAction);
        ExecuteCommandManager.Instance().RegPre(OnPreUseCommand);
        DService.Instance().Condition.ConditionChange += OnConditionChanged;
    }

    protected override void ConfigUI()
    {
        if (ImGui.Checkbox(Lang.Get("SendNotification"), ref ModuleConfig.SendNotification))
            ModuleConfig.Save(this);

        ImGui.SameLine();
        if (ImGui.Checkbox(Lang.Get("SendChat"), ref ModuleConfig.SendChat))
            ModuleConfig.Save(this);

        ImGui.SetNextItemWidth(100f * GlobalUIScale);
        if (ImGui.InputInt(Lang.Get("Delay"), ref ModuleConfig.Delay))
            ModuleConfig.Delay = Math.Max(0, ModuleConfig.Delay);
        if (ImGui.IsItemDeactivatedAfterEdit())
            ModuleConfig.Save(this);
    }

    // 直接导向传送界面
    private static void OnPreUseAction
    (
        ref bool                        isPrevented,
        ref ActionType                  actionType,
        ref uint                        actionID,
        ref ulong                       targetID,
        ref uint                        extraParam,
        ref ActionManager.UseActionMode queueState,
        ref uint                        comboRouteID
    )
    {
        if (actionType != ActionType.GeneralAction || actionID != 7) return;
        if (GameMain.Instance()->CurrentContentFinderConditionId != 0) return;
        if (!DService.Instance().Condition[ConditionFlag.InCombat]) return;
        if (ModuleManager.Instance().IsModuleEnabled("BetterTeleport") ?? false) return;

        var agent = AgentTeleport.Instance();
        if (agent == null) return;

        isPrevented = true;
        agent->Show();
    }

    // 实际存储
    private static void OnPreUseCommand
    (
        ref bool               isPrevented,
        ref ExecuteCommandFlag command,
        ref uint               param1,
        ref uint               param2,
        ref uint               param3,
        ref uint               param4
    )
    {
        if (command != ExecuteCommandFlag.Teleport || isPrevented || !DService.Instance().Condition[ConditionFlag.InCombat]) return;
        isPrevented    = true;
        QueuedTeleport = new(param1, param3);
        Notify(QueueTeleportNotifyType.Save);
    }

    // 实际执行传送
    private static void OnConditionChanged(ConditionFlag flag, bool value)
    {
        if (flag != ConditionFlag.InCombat || value || QueuedTeleport == null) return;
        var currentFate = FateManager.Instance()->CurrentFate;
        if (currentFate != null && currentFate->Progress < 80) return;

        if (currentFate != null)
            TeleportHelper.Enqueue(() => FateManager.Instance()->CurrentFate == null);
        TeleportHelper.Enqueue(() => !DService.Instance().Condition[ConditionFlag.InCombat]);

        if (ModuleConfig.Delay > 0)
        {
            TeleportHelper.Enqueue(() => NotifyHelper.Instance().NotificationInfo(Lang.Get("QueueCombatTeleport-Notice-Waiting", ModuleConfig.Delay)));
            TeleportHelper.DelayNext(ModuleConfig.Delay);
        }

        TeleportHelper.Enqueue
        (() =>
            {
                Telepo.Instance()->Teleport(QueuedTeleport.Value.ID, (byte)QueuedTeleport.Value.SubID);
                Notify(QueueTeleportNotifyType.Execute);
                QueuedTeleport = null;

                return true;
            }
        );
    }

    private static void Notify(QueueTeleportNotifyType type)
    {
        if (!ModuleConfig.SendChat && !ModuleConfig.SendNotification) return;

        var message = string.Empty;

        switch (type)
        {
            case QueueTeleportNotifyType.Save when QueuedTeleport != null:
                var qualifiedAetheryteSaved =
                    DService.Instance().AetheryteList.FirstOrDefault
                    (x => x.AetheryteID == QueuedTeleport.Value.ID &&
                          x.SubIndex    == QueuedTeleport.Value.SubID
                    );
                if (qualifiedAetheryteSaved == null) return;
                message = Lang.Get
                (
                    "QueueCombatTeleport-Notice-Saved",
                    qualifiedAetheryteSaved.AetheryteData.Value.PlaceName.Value.Name.ToString()
                );
                break;
            case QueueTeleportNotifyType.Execute when QueuedTeleport != null:
                var qualifiedAetheryteExecuted =
                    DService.Instance().AetheryteList.FirstOrDefault
                    (x => x.AetheryteID == QueuedTeleport.Value.ID &&
                          x.SubIndex    == QueuedTeleport.Value.SubID
                    );
                if (qualifiedAetheryteExecuted == null) return;
                message = Lang.Get
                (
                    "QueueCombatTeleport-Notice-Executed",
                    qualifiedAetheryteExecuted.AetheryteData.Value.PlaceName.Value.Name.ToString()
                );
                break;
            case QueueTeleportNotifyType.Clear:
                message = Lang.Get("QueueCombatTeleport-Notice-Cleared");
                break;
        }

        if (string.IsNullOrWhiteSpace(message)) return;
        if (ModuleConfig.SendChat)
            NotifyHelper.Instance().Chat(message);
        if (ModuleConfig.SendNotification)
            NotifyHelper.Instance().NotificationInfo(message);
    }

    protected override void Uninit()
    {
        CanUseTeleportPatch.Disable();
        CanUseTeleportMapPatch.Disable();

        DService.Instance().Condition.ConditionChange -= OnConditionChanged;
        ExecuteCommandManager.Instance().Unreg(OnPreUseCommand);
        UseActionManager.Instance().Unreg(OnPreUseAction);

        TeleportHelper?.Dispose();
        TeleportHelper = null;

        QueuedTeleport = null;
    }

    public class Config : ModuleConfig
    {
        public int  Delay    = 500;
        public bool SendChat = true;

        public bool SendNotification = true;
    }

    private enum QueueTeleportNotifyType
    {
        Save,
        Execute,
        Clear
    }
}
