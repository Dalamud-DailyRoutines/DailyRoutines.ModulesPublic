using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using Dalamud.Game.Agent;
using Dalamud.Game.Agent.AgentArgTypes;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using Lumina.Text.ReadOnly;
using OmenTools.Interop.Game.Helpers;
using OmenTools.Interop.Game.Lumina;
using OmenTools.Interop.Game.Models;
using OmenTools.OmenService;
using OmenTools.Threading.TaskHelper;
using AgentId = FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentId;
using AgentShowDelegate = OmenTools.Interop.Game.Models.Native.AgentShowDelegate;

namespace DailyRoutines.ModulesPublic;

public unsafe class InstantLogout : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("InstantLogoutTitle"),
        Description = Lang.Get("InstantLogoutDescription"),
        Category    = ModuleCategory.System
    };

    public override ModulePermission Permission { get; } = new() { AllDefaultEnabled = true };

    private Hook<AgentHUD.Delegates.HandleMainCommandOperation>? HandleMainCommandOperationHook;

    private Hook<AgentShowDelegate>? AgentCloseMessageShowHook;

    // TODO: FFCS
    private static readonly CompSig ExitGameSig = new
        ("40 53 48 83 EC ?? 48 8B D9 BA ?? ?? ?? ?? 48 81 C1 ?? ?? ?? ?? E8 ?? ?? ?? ?? 83 78 ?? ?? 74 ?? 83 78 ?? ?? 74");
    private delegate void ExitGameDelegate(Framework* framework);
    private Hook<ExitGameDelegate>? ExitGameHook;

    protected override void Init()
    {
        TaskHelper ??= new();

        HandleMainCommandOperationHook = DService.Instance().Hook.HookFromMemberFunction
        (
            typeof(AgentHUD.MemberFunctionPointers),
            "HandleMainCommandOperation",
            (AgentHUD.Delegates.HandleMainCommandOperation)HandleMainCommandOperationDetour
        );
        HandleMainCommandOperationHook.Enable();

        AgentCloseMessageShowHook = AgentModule.Instance()->GetAgentByInternalId(AgentId.CloseMessage)->VirtualTable->HookVFuncFromName
        (
            "Show",
            (AgentShowDelegate)AgentCloseMessageShowDetour
        );
        AgentCloseMessageShowHook.Enable();
        
        DService.Instance().AgentLifecycle.RegisterListener(AgentEvent.PreReceiveEvent, Dalamud.Game.Agent.AgentId.Lobby, OnAgentLobby);

        ExitGameHook = ExitGameSig.GetHook<ExitGameDelegate>(ExitGameDetour);
        ExitGameHook.Enable();

        ChatManager.Instance().RegPreExecuteCommandInner(OnPreExecuteCommandInner);
    }

    protected override void Uninit()
    {
        ChatManager.Instance().Unreg(OnPreExecuteCommandInner);
        DService.Instance().AgentLifecycle.UnregisterListener(AgentEvent.PreReceiveEvent, Dalamud.Game.Agent.AgentId.Lobby, OnAgentLobby);
    }

    protected override void ConfigUI()
    {
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), $"{Lang.Get("InstantLogout-ManualOperation")}:");

        using (ImRaii.PushIndent())
        {
            if (ImGui.Button(Lang.Get("InstantLogout-Logout")))
                Logout(TaskHelper);

            ImGui.SameLine();
            if (ImGui.Button(Lang.Get("InstantLogout-Shutdown")))
                Shutdown(TaskHelper);
        }
    }

    // 从窗口标题栏退出
    private void ExitGameDetour(Framework* framework) =>
        Shutdown(TaskHelper);
    
    // 从标题界面退出游戏
    private void OnAgentLobby(AgentEvent type, AgentArgs args)
    {
        var eventArgs = args as AgentReceiveEventArgs;
        if (eventArgs.EventKind != 0 || eventArgs.ValueCount != 1) return;

        var atkValues = (AtkValue*)eventArgs.AtkValues;
        if (atkValues[0].Int != 12) return;

        args.PreventOriginal();
        Shutdown(TaskHelper);
    }
    
    // 从系统菜单退出
    private bool HandleMainCommandOperationDetour
    (
        AgentHUD*            agent,
        MainCommandOperation operation,
        uint                 param1,
        int                  param2,
        byte*                param3
    )
    {
        if (operation == MainCommandOperation.ExecuteMainCommand && param2 is -1)
        {
            switch (param1)
            {
                case 23:
                    Logout(TaskHelper);
                    return false;
                case 24:
                    Shutdown(TaskHelper);
                    return false;
            }
        }

        return HandleMainCommandOperationHook.Original(agent, operation, param1, param2, param3);
    }

    // 从关闭程序对话框退出
    private void AgentCloseMessageShowDetour(AgentInterface* agent) =>
        Shutdown(TaskHelper);

    // 从文本指令退出
    private void OnPreExecuteCommandInner(ref bool isPrevented, ref ReadOnlySeString message)
    {
        var messageDecode = message.ToString();

        if (string.IsNullOrWhiteSpace(messageDecode) || !messageDecode.StartsWith('/'))
            return;

        if (CheckCommand(messageDecode, LogoutLine,   TaskHelper, Logout) ||
            CheckCommand(messageDecode, ShutdownLine, TaskHelper, Shutdown))
            isPrevented = true;
    }
    
    #region 实际操作

    private static void Logout(TaskHelper _) =>
        ContentsFinderHelper.RequestDutyNormal(167, ContentsFinderHelper.DefaultOption);

    private static void Shutdown(TaskHelper taskHelper)
    {
        taskHelper.Enqueue(() => Logout(taskHelper));
        taskHelper.Enqueue
        (() =>
            {
                if (GameState.IsLoggedIn) return false;

                ChatManager.Instance().SendMessage("/xlkill");
                return true;
            }
        );
    }

    #endregion
    
    private static bool CheckCommand(string message, TextCommand command, TaskHelper taskHelper, Action<TaskHelper> action)
    {
        if (message == command.Command.ToString() || message == command.Alias.ToString())
        {
            action(taskHelper);
            return true;
        }

        return false;
    }

    #region 常量

    private static readonly TextCommand LogoutLine   = LuminaGetter.GetRowOrDefault<TextCommand>(172);
    private static readonly TextCommand ShutdownLine = LuminaGetter.GetRowOrDefault<TextCommand>(173);

    #endregion
}
