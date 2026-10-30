using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using Dalamud.Hooking;
using FFXIVClientStructs.Expansion.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;

namespace DailyRoutines.ModulesPublic;

public unsafe class AutoShowChatLogInKeyboundBrawler : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title               = Lang.Get("AutoShowChatLogInKeyboundBrawlerTitle"),
        Description         = Lang.Get("AutoShowChatLogInKeyboundBrawlerDescription"),
        Category            = ModuleCategory.GoldSaucer,
        ModulesPrerequisite = ["AutoKeyboundBrawler"]
    };

    private Hook<AgentChatLog.Delegates.HideLogWindow> HideLogWindowHook;
    private Hook<AgentKTGHud.Delegates.HideChatLog>    HideChatLogHook;

    private bool isDisabling;

    protected override void Init()
    {
        HideChatLogHook = IGameInteropProvider.Instance().HookFromMemberFunction
        (
            typeof(AgentKTGHud.MemberFunctionPointers),
            nameof(AgentKTGHud.HideChatLog),
            (AgentKTGHud.Delegates.HideChatLog)HideChatLogDetour
        );
        HideChatLogHook.Enable();

        HideLogWindowHook = IGameInteropProvider.Instance().HookFromMemberFunction
        (
            typeof(AgentChatLog.MemberFunctionPointers),
            nameof(AgentChatLog.HideLogWindow),
            (AgentChatLog.Delegates.HideLogWindow)HideLogWindowDetour
        );
        HideLogWindowHook.Enable();

        var hud = AgentKTGHud.Instance();
        if (hud != null      &&
            hud->IsShown     &&
            hud->HasDirector &&
            ChatLog != null)
            ShowChatLog();
    }

    protected override void Uninit()
    {
        var hud = AgentKTGHud.Instance();

        if (hud != null  &&
            hud->IsShown &&
            hud->HasDirector)
        {
            isDisabling = true;
            HideChatLogHook.Original(hud);
        }
    }

    private void HideChatLogDetour
    (
        AgentKTGHud* hud
    )
    {
        var addon = ChatLog;

        if (hud   == null ||
            addon == null ||
            !hud->IsShown ||
            !hud->HasDirector)
        {
            HideChatLogHook.Original(hud);
            return;
        }

        HideChatLogHook.Original(hud);
        var agent = AgentChatLog.Instance();
        if (agent != null)
            agent->ShowAddon();
    }

    private void HideLogWindowDetour
    (
        AgentChatLog* agent
    )
    {
        var hud = AgentKTGHud.Instance();

        if (hud != null  &&
            hud->IsShown &&
            hud->HasDirector)
        {
            if (isDisabling)
            {
                HideLogWindowHook.Original(agent);
                return;
            }

            if (ChatLog != null &&
                !ChatLog->IsVisible)
                ShowChatLog();

            return;
        }

        HideLogWindowHook.Original(agent);
    }

    private static void ShowChatLog()
    {
        var agent = AgentChatLog.Instance();
        if (agent == null)
            return;

        agent->ShowAddon();
    }
}
