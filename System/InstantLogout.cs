using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Lumina.Excel.Sheets;
using Lumina.Text.ReadOnly;
using OmenTools.Interop.Game.Helpers;
using OmenTools.Interop.Game.Lumina;
using OmenTools.OmenService;
using OmenTools.Threading.TaskHelper;
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

    private Hook<AgentHUD.Delegates.HandleMainCommandOperation>? HandleMainCommandOperationHook;

    private Hook<AgentShowDelegate>? AgentCloseMessageShowHook;

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

        AgentCloseMessageShowHook = DService.Instance().Hook.HookFromAddress<AgentShowDelegate>
        (
            AgentModule.Instance()->GetAgentByInternalId(AgentId.CloseMessage)->VirtualTable->GetVFuncByName("Show"),
            AgentCloseMessageShowDetour
        );
        AgentCloseMessageShowHook.Enable();

        ChatManager.Instance().RegPreExecuteCommandInner(OnPreExecuteCommandInner);
    }

    protected override void Uninit() =>
        ChatManager.Instance().Unreg(OnPreExecuteCommandInner);

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
        if (message == command.Command.ToString() || message == command.Alias.ToString())
        {
            action(taskHelper);
            return true;
        }

        return false;
    }

    private static void Logout(TaskHelper _) =>
        ContentsFinderHelper.RequestDutyNormal(167, ContentsFinderHelper.DefaultOption);

    private static void Shutdown(TaskHelper taskHelper)
    {
        taskHelper.Enqueue(() => Logout(taskHelper));
        taskHelper.Enqueue
        (() =>
            {
                if (DService.Instance().ClientState.IsLoggedIn) return false;

                ChatManager.Instance().SendMessage("/xlkill");
                return true;
            }
        );
    }

    #region 常量

    private static readonly TextCommand LogoutLine   = LuminaGetter.GetRowOrDefault<TextCommand>(172);
    private static readonly TextCommand ShutdownLine = LuminaGetter.GetRowOrDefault<TextCommand>(173);

    #endregion
}
