using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using OmenTools.Info.Game.Enums;
using OmenTools.Interop.Game.Models;
using OmenTools.OmenService;
using ModuleBase = DailyRoutines.Common.Module.Abstractions.ModuleBase;

namespace DailyRoutines.ModulesPublic.Duty;

public class AutoVeryEasyQuestBattle : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoVeryEasyQuestBattleTitle"),
        Description = Lang.Get("AutoVeryEasyQuestBattleDescription"),
        Category    = ModuleCategory.Duty
    };

    public override ModulePermission Permission { get; } = new() { NeedAuth = true, AllDefaultEnabled = true };

    private static readonly CompSig HandleStartOrEndCommandSig =
        new("4C 8B DC 55 57 41 56 41 57 49 8D AB ?? ?? ?? ?? 48 81 EC ?? ?? ?? ?? 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 85 ?? ?? ?? ?? 48 8B F9");
    private delegate void HandleStartOrEndCommandDelegate
    (
        QuestEventHandler* thisPtr,
        uint               command
    );
    private Hook<HandleStartOrEndCommandDelegate>? HandleStartOrEndCommandHook;
    
    private bool needToNotifyThisTime;

    protected override void Init()
    {
        HandleStartOrEndCommandHook = HandleStartOrEndCommandSig.GetHook<HandleStartOrEndCommandDelegate>(HandleStartOrEndCommandDetour);
        HandleStartOrEndCommandHook.Enable();
        
        ExecuteCommandManager.Instance().RegPre(OnPreUseCommand);
    }

    protected override void Uninit() =>
        ExecuteCommandManager.Instance().Unreg(OnPreUseCommand);
    
    private unsafe void HandleStartOrEndCommandDetour
    (
        QuestEventHandler* thisPtr,
        uint               command
    )
    {
        needToNotifyThisTime = true;
        HandleStartOrEndCommandHook.Original(thisPtr, command);
        needToNotifyThisTime = false;
    }

    private void OnPreUseCommand
    (
        ref bool               isPrevented,
        ref ExecuteCommandFlag command,
        ref uint               param1,
        ref uint               param2,
        ref uint               param3,
        ref uint               param4
    )
    {
        if (command != ExecuteCommandFlag.StartSoloQuestBattle) return;

        param1 = 2;

        if (!needToNotifyThisTime) return;

        var message = Lang.Get("AutoVeryEasyQuestBattle-Notification");
        
        NotifyHelper.Instance().Chat(message);
        NotifyHelper.Toast(message);
    }
}
