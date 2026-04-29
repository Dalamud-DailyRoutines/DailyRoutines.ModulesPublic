using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using OmenTools.Dalamud.Helpers;
using OmenTools.Interop.Game.Models;
using OmenTools.OmenService;
using OmenTools.Threading;
using AgentShowDelegate = OmenTools.Interop.Game.Models.Native.AgentShowDelegate;

namespace DailyRoutines.ModulesPublic;

public unsafe class AutoRefuseTrade : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoRefuseTradeTitle"),
        Description = Lang.Get("AutoRefuseTradeDescription"),
        Category    = ModuleCategory.General
    };

    public override ModulePermission Permission { get; } = new() { NeedAuth = true };
    
    private Hook<AgentShowDelegate>? AgentTradeShowHook;

    private Hook<InventoryManager.Delegates.SendTradeRequest>? SendTradeRequestHook;

    private Config config = null!;

    protected override void Init()
    {
        config = Config.Load(this) ?? new();


        AgentTradeShowHook = AgentModule.Instance()->GetAgentByInternalId(AgentId.Trade)->VirtualTable->HookVFuncFromName
        (
            "Show",
            (AgentShowDelegate)AgentTradeShowDetour
        );
        AgentTradeShowHook.Enable();

        SendTradeRequestHook = DService.Instance().Hook.HookFromMemberFunction
        (
            typeof(InventoryManager.MemberFunctionPointers),
            "SendTradeRequest",
            (InventoryManager.Delegates.SendTradeRequest)SendTradeRequestDetour
        );
        SendTradeRequestHook.Enable();
    }

    protected override void ConfigUI()
    {
        if (ImGui.Checkbox(Lang.Get("SendChat"), ref config.SendChat))
            config.Save(this);

        ImGui.SameLine();
        if (ImGui.Checkbox(Lang.Get("SendNotification"), ref config.SendNotification))
            config.Save(this);

        ImGui.TextUnformatted(Lang.Get("AutoRefuseTrade-ExtraCommands"));
        ImGui.InputTextMultiline("###ExtraCommandsInput", ref config.ExtraCommands, 1024, ScaledVector2(300f, 120f));
        ImGuiOm.TooltipHover(config.ExtraCommands);

        if (ImGui.IsItemDeactivatedAfterEdit())
            config.Save(this);
    }

    private void SendTradeRequestDetour(InventoryManager* instance, uint entityID)
    {
        Throttler.Shared.Throttle("AutoRefuseTrade-Show", 3_000, true);
        SendTradeRequestHook.Original(instance, entityID);
    }

    private void AgentTradeShowDetour(AgentInterface* agent)
    {
        // 没有 Block => 五秒内没有发起交易的请求
        if (Throttler.Shared.Check("AutoRefuseTrade-Show"))
        {
            InventoryManager.Instance()->RefuseTrade();
            NotifyTradeCancel();
            return;
        }

        AgentTradeShowHook.Original(agent);
    }

    private void NotifyTradeCancel()
    {
        var message = Lang.Get("AutoRefuseTrade-Notification");

        if (config.SendNotification)
        {
            NotifyHelper.Instance().NotificationInfo(message);
            NotifyHelper.Speak(message);
        }

        if (config.SendChat)
            NotifyHelper.Instance().Chat($"{message}\n    ({Lang.Get("Time")}: {StandardTimeManager.Instance().Now.ToShortTimeString()})");

        if (!string.IsNullOrWhiteSpace(config.ExtraCommands))
        {
            foreach (var command in config.ExtraCommands.Split('\n'))
                ChatManager.Instance().SendMessage(command);
        }
    }
    
    private class Config : ModuleConfig
    {
        public string ExtraCommands    = string.Empty;
        public bool   SendChat         = true;
        public bool   SendNotification = true;
    }
}
