using System.Text.RegularExpressions;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using OmenTools.OmenService;
using OmenTools.Threading;
using AgentShowDelegate = OmenTools.Interop.Game.Models.Native.AgentShowDelegate;

namespace DailyRoutines.ModulesPublic;

public unsafe partial class AutoRefuseTrade : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoRefuseTradeTitle"),
        Description = Lang.Get("AutoRefuseTradeDescription"),
        Category    = ModuleCategory.System
    };

    public override ModulePermission Permission { get; } = new() { NeedAuth = true };

    private Hook<AgentShowDelegate>? AgentTradeShowHook;

    private Hook<InventoryManager.Delegates.SendTradeRequest>? SendTradeRequestHook;

    private Config config = null!;

    protected override void Init()
    {
        config = Config.Load(this) ?? new();

        TaskHelper = new();

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
        if (ImGui.InputUInt($"{Lang.Get("Delay")} (ms)", ref config.DelayMS))
            config.DelayMS = Math.Max(0, config.DelayMS);
        if (ImGui.IsItemDeactivatedAfterEdit())
            config.Save(this);

        ImGui.NewLine();

        if (ImGui.Checkbox(Lang.Get("SendChat"), ref config.SendChat))
            config.Save(this);

        if (ImGui.Checkbox(Lang.Get("SendNotification"), ref config.SendNotification))
            config.Save(this);

        ImGui.TextUnformatted(Lang.Get("AutoRefuseTrade-ExtraCommands"));
        ImGui.InputTextMultiline("###ExtraCommandsInput", ref config.ExtraCommands, 1024, ScaledVector2(300f, 200f));
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
            TaskHelper.Abort();

            if (config.DelayMS > 0)
                TaskHelper.DelayNext((int)config.DelayMS);
            TaskHelper.Enqueue
            (() =>
                {
                    InventoryManager.Instance()->RefuseTrade();
                    NotifyTradeCancel();
                }
            );
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
            foreach (var line in config.ExtraCommands.Split('\n'))
            {
                var trimmed = line.Trim();
                if (string.IsNullOrEmpty(trimmed)) continue;

                if (trimmed.StartsWith("/wait ", StringComparison.OrdinalIgnoreCase))
                {
                    var part = trimmed[6..].Trim();
                    if (int.TryParse(part, out var ms) && ms > 0)
                        TaskHelper.DelayNext(ms);

                    continue;
                }

                var match = WaitParamRegex().Match(trimmed);

                if (match.Success)
                {
                    var command = match.Groups[1].Value.Trim();

                    if (!string.IsNullOrEmpty(command))
                    {
                        TaskHelper.DelayNext(100);
                        TaskHelper.Enqueue(() => ChatManager.Instance().SendMessage(command));
                    }

                    if (int.TryParse(match.Groups[2].Value, out var w) && w > 0)
                        TaskHelper.DelayNext(w);

                    continue;
                }

                TaskHelper.DelayNext(100);
                TaskHelper.Enqueue(() => ChatManager.Instance().SendMessage(trimmed));
            }
        }
    }

    private class Config : ModuleConfig
    {
        public string ExtraCommands    = string.Empty;
        public bool   SendChat         = true;
        public bool   SendNotification = true;

        public uint DelayMS = 500;
    }

    [GeneratedRegex(@"^(.*?)<wait\.(\d+)>\s*$", RegexOptions.IgnoreCase, "zh-CN")]
    private static partial Regex WaitParamRegex();
}
