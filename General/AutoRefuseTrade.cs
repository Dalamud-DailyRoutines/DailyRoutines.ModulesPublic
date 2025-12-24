using System;
using DailyRoutines.Abstracts;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;

namespace DailyRoutines.ModulesPublic;

public unsafe class AutoRefuseTrade : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title = GetLoc("AutoRefuseTradeTitle"),
        Description = GetLoc("AutoRefuseTradeDescription"),
        Category = ModuleCategories.General
    };

    public override ModulePermission Permission { get; } = new() { NeedAuth = true };
    
    private static Hook<AgentShowDelegate>? AgentTradeShowHook;

    private static readonly CompSig                     TradeRequestSig = new("48 89 6C 24 ?? 56 57 41 56 48 83 EC ?? 48 8B E9 44 8B F2");
    private delegate        int                         TradeRequestDelegate(InventoryManager* instance, uint entityID);
    private static          Hook<TradeRequestDelegate>? TradeRequestHook;

    private static Config ModuleConfig = null!;

    protected override void Init()
    {
        ModuleConfig = LoadConfig<Config>() ?? new();
        
        AgentTradeShowHook ??= DService.Hook.HookFromAddress<AgentShowDelegate>(
            GetVFuncByName(AgentModule.Instance()->GetAgentByInternalId(AgentId.Trade)->VirtualTable, "Show"),
            AgentTradeShowDetour);
        AgentTradeShowHook.Enable();

        TradeRequestHook ??= DService.Hook.HookFromAddress<TradeRequestDelegate>(
            GetMemberFuncByName(typeof(InventoryManager.MemberFunctionPointers), "SendTradeRequest"),
            TradeRequestDetour);
        TradeRequestHook.Enable();
    }

    protected override void ConfigUI()
    {
        if (ImGui.Checkbox(GetLoc("SendChat"), ref ModuleConfig.SendChat))
            SaveConfig(ModuleConfig);

        ImGui.SameLine();
        if (ImGui.Checkbox(GetLoc("SendNotification"), ref ModuleConfig.SendNotification))
            SaveConfig(ModuleConfig);

        ImGui.Text(GetLoc("AutoRefuseTrade-ExtraCommands"));
        ImGui.InputTextMultiline("###ExtraCommandsInput", ref ModuleConfig.ExtraCommands, 1024, ScaledVector2(300f, 120f));
        ImGuiOm.TooltipHover(ModuleConfig.ExtraCommands);

        if (ImGui.IsItemDeactivatedAfterEdit())
            SaveConfig(ModuleConfig);
    }

    private static int TradeRequestDetour(InventoryManager* instance, uint entityID)
    {
        Throttler.Throttle("AutoRefuseTrade-Show", 3_000, true);
        return TradeRequestHook.Original(instance, entityID);
    }

    private static void AgentTradeShowDetour(AgentInterface* agent)
    {
        // 没有 Block => 五秒内没有发起交易的请求
        if (Throttler.Check("AutoRefuseTrade-Show"))
        {
            InventoryManager.Instance()->RefuseTrade();
            NotifyTradeCancel();
            return;
        }

        AgentTradeShowHook.Original(agent);
    }

    private static void NotifyTradeCancel()
    {
        var message = GetLoc("AutoRefuseTrade-Notification");

        if (ModuleConfig.SendNotification)
        {
            NotificationInfo(message);
            Speak(message);
        }

        if (ModuleConfig.SendChat)
            Chat($"{message}\n    ({GetLoc("Time")}: {DateTime.Now.ToShortTimeString()})");

        if (!string.IsNullOrWhiteSpace(ModuleConfig.ExtraCommands))
        {
            foreach (var command in ModuleConfig.ExtraCommands.Split('\n'))
                ChatManager.SendMessage(command);
        }
    }

    private class Config : ModuleConfiguration
    {
        public bool SendNotification = true;
        public bool SendChat = true;
        public string ExtraCommands = string.Empty;
    }
}
