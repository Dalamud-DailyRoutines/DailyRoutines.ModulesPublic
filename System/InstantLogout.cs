using System;
using DailyRoutines.Abstracts;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Lumina.Excel.Sheets;
using Lumina.Text.ReadOnly;

namespace DailyRoutines.ModulesPublic;

public unsafe class InstantLogout : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("InstantLogoutTitle"),
        Description = GetLoc("InstantLogoutDescription"),
        Category    = ModuleCategories.System,
    };

    private static readonly CompSig                          SystemMenuExecuteSig = new("E8 ?? ?? ?? ?? 40 B5 01 41 B9 ?? ?? ?? ??");
    private delegate        nint                             SystemMenuExecuteDelegate(AgentHUD* agentHud, int a2, uint a3, int a4, nint a5);
    private static          Hook<SystemMenuExecuteDelegate>? SystemMenuExecuteHook;

    private static Hook<AgentShowDelegate>? AgentCloseMessageShowHook;

    private static readonly TextCommand LogoutLine   = LuminaGetter.GetRowOrDefault<TextCommand>(172);
    private static readonly TextCommand ShutdownLine = LuminaGetter.GetRowOrDefault<TextCommand>(173);
    
    protected override void Init()
    {
        TaskHelper ??= new();

        SystemMenuExecuteHook ??= SystemMenuExecuteSig.GetHook<SystemMenuExecuteDelegate>(SystemMenuExecuteDetour);
        SystemMenuExecuteHook.Enable();

        AgentCloseMessageShowHook ??= DService.Hook.HookFromAddress<AgentShowDelegate>(
            GetVFuncByName(AgentModule.Instance()->GetAgentByInternalId(AgentId.CloseMessage)->VirtualTable, "Show"),
            AgentCloseMessageShowDetour);
        AgentCloseMessageShowHook.Enable();

        ChatManager.RegPreExecuteCommandInner(OnPreExecuteCommandInner);
    }

    protected override void Uninit() => 
        ChatManager.Unreg(OnPreExecuteCommandInner);

    protected override void ConfigUI()
    {
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), $"{GetLoc("InstantLogout-ManualOperation")}:");

        using (ImRaii.PushIndent())
        {
            if (ImGui.Button(GetLoc("InstantLogout-Logout"))) 
                Logout(TaskHelper);
        
            ImGui.SameLine();
            if (ImGui.Button(GetLoc("InstantLogout-Shutdown"))) 
                Shutdown(TaskHelper);
        }
    }

    private nint SystemMenuExecuteDetour(AgentHUD* agentHud, int a2, uint a3, int a4, nint a5)
    {
        if (a2 is 1 && a4 is -1)
        {
            switch (a3)
            {
                case 23:
                    Logout(TaskHelper);
                    return 0;
                case 24:
                    Shutdown(TaskHelper);
                    return 0;
            }
        }
        
        return SystemMenuExecuteHook.Original(agentHud, a2, a3, a4, a5);
    }
    
    private void AgentCloseMessageShowDetour(AgentInterface* agent) => 
        Shutdown(TaskHelper);
    
    private void OnPreExecuteCommandInner(ref bool isPrevented, ref ReadOnlySeString message)
    {
        var messageDecode = message.ToString();

        if (string.IsNullOrWhiteSpace(messageDecode) || !messageDecode.StartsWith('/'))
            return;

        if (CheckCommand(messageDecode, LogoutLine,   TaskHelper, Logout) ||
            CheckCommand(messageDecode, ShutdownLine, TaskHelper, Shutdown))
            isPrevented = true;
    }

    private static bool CheckCommand(string message, TextCommand command, TaskHelper taskHelper, Action<TaskHelper> action)
    {
        if (message == command.Command.ExtractText() || message == command.Alias.ExtractText())
        {
            action(taskHelper);
            return true;
        }
        
        return false;
    }

    private static void Logout(TaskHelper _) => 
        RequestDutyNormal(167, DefaultOption);

    private static void Shutdown(TaskHelper taskHelper)
    {
        taskHelper.Enqueue(() => Logout(taskHelper));
        taskHelper.Enqueue(() =>
        {
            if (DService.ClientState.IsLoggedIn) return false;

            ChatManager.SendMessage("/xlkill");
            return true;
        });
    }
}
