using System.Collections.Generic;
using System.Linq;
using DailyRoutines.Abstracts;
using DailyRoutines.Helpers;
using DailyRoutines.Infos;
using DailyRoutines.Managers;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Hooking;
using Dalamud.Memory;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Lumina.Excel.Sheets;
using Camera = FFXIVClientStructs.FFXIV.Client.Game.Camera;

namespace DailyRoutines.ModulesPublic;

public unsafe class OptimizedInteraction : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title = GetLoc("OptimizedInteractionTitle"),
        Description = GetLoc("OptimizedInteractionDescription"),
        Category = ModuleCategories.System,
    };

    public override ModulePermission Permission { get; } = new() { NeedAuth = true };

    // 当前位置无法进行该操作
    private static readonly CompSig CameraObjectBlockedSig = new("E8 ?? ?? ?? ?? 84 C0 75 ?? B9 ?? ?? ?? ?? E8 ?? ?? ?? ?? EB ?? 40 B7");
    private delegate bool CameraObjectBlockedDelegate(nint a1, nint a2, nint a3);
    private static Hook<CameraObjectBlockedDelegate>? CameraObjectBlockedHook;

    // 目标处于视野之外
    private static readonly CompSig IsObjectInViewRangeSig = new("E8 ?? ?? ?? ?? 84 C0 75 ?? 48 8B 0D ?? ?? ?? ?? E8 ?? ?? ?? ?? 48 8B C8 48 8B 10 FF 52 ?? 48 8B C8 BA ?? ?? ?? ?? E8 ?? ?? ?? ?? E9");
    private  delegate bool IsObjectInViewRangeDelegate(TargetSystem* system, GameObject* gameObject);
    private static Hook<IsObjectInViewRangeDelegate>? IsObjectInViewRangeHook;

    // 跳跃中无法进行该操作 / 飞行中无法进行该操作
    private static readonly CompSig InteractCheck0Sig = new("48 89 5C 24 ?? 48 89 74 24 ?? 57 48 83 EC ?? 49 8B 00 49 8B C8");
    private delegate bool InteractCheck0Delegate(nint a1, nint a2, nint a3, nint a4, bool a5);
    private static Hook<InteractCheck0Delegate>? InteractCheck0Hook;

    // 跳跃中无法进行该操作
    private delegate bool IsPlayerOnJumpingDelegate(nint a1);

    private static readonly CompSig IsPlayerOnJumping0Sig =
        new("E8 ?? ?? ?? ?? 84 C0 0F 85 ?? ?? ?? ?? 48 8D 8D ?? ?? ?? ?? E8 ?? ?? ?? ?? 84 C0 0F 85");
    private static Hook<IsPlayerOnJumpingDelegate>? IsPlayerOnJumping0Hook;
    private static readonly CompSig IsPlayerOnJumping1Sig = new("E8 ?? ?? ?? ?? 84 C0 0F 85 ?? ?? ?? ?? 83 BF ?? ?? ?? ?? ?? 75 ?? 38 1D");
    private static Hook<IsPlayerOnJumpingDelegate>? IsPlayerOnJumping1Hook;
    private static readonly CompSig IsPlayerOnJumping2Sig =
        new("40 53 48 83 EC ?? 48 8D 99 ?? ?? ?? ?? 48 8B CB E8 ?? ?? ?? ?? 84 C0 75");
    private static Hook<IsPlayerOnJumpingDelegate>? IsPlayerOnJumping2Hook;

    // 检查目标高低
    private static readonly CompSig CheckTargetPositionSig = new("40 53 57 41 56 48 83 EC ?? 48 8B 02");
    private delegate bool CheckTargetPositionDelegate(
        EventFramework* framework, GameObject* source, GameObject* target, ushort interactType, bool sendError);
    private static Hook<CheckTargetPositionDelegate>? CheckTargetPositionHook;

    // 检查目标距离
    private static readonly CompSig CheckTargetDistanceSig = 
        new ("E8 ?? ?? ?? ?? 0F 2F 05 ?? ?? ?? ?? 76 ?? 48 8B 03 48 8B CB FF 50 ?? 48 8B C8 BA ?? ?? ?? ?? E8 ?? ?? ?? ?? EB");
    private delegate float CheckTargetDistanceDelegate(GameObject* localPlayer, GameObject* target);
    private static Hook<CheckTargetDistanceDelegate>? CheckTargetDistanceHook;

    // 交互
    private static readonly CompSig InteractWithObjectSig =
        new("48 89 5C 24 ?? 48 89 6C 24 ?? 56 48 83 EC ?? 48 8B E9 41 0F B6 F0");
    private delegate ulong InteractWithObjectDelegate(
        TargetSystem* system, GameObject* obj, bool checkLOS = true);
    private static Hook<InteractWithObjectDelegate>? InteractWithObjectHook;
    
    // 当前位置无法进行该操作
    private static readonly CompSig CheckCameraPositionSig =
        new("E8 ?? ?? ?? ?? 84 C0 75 ?? B9 ?? ?? ?? ?? E8 ?? ?? ?? ?? EB");
    private delegate bool CheckCameraPositionDelegate(TargetSystem* system, Camera* activeCamera, GameObject* obj);
    private static Hook<CheckCameraPositionDelegate>? CheckCameraPositionHook;

    private static readonly HashSet<string> WhitelistObjectNames = 
    [
        // 市场布告板
        LuminaGetter.GetRow<EObjName>(2000073)!.Value.Singular.ExtractText(),
        // 简易以太之光
        LuminaGetter.GetRow<EObjName>(2003395)!.Value.Singular.ExtractText(),
    ];

    private static readonly List<uint> FirstQuest = [65621, 65644, 65645, 65659, 65660, 66104, 66105, 66106];

    protected override void Init()
    {
        TaskHelper ??= new();
        
        InitHooks();
        SwitchHooks(true);
    }

    private void InitHooks()
    {
        CameraObjectBlockedHook ??= CameraObjectBlockedSig.GetHook<CameraObjectBlockedDelegate>(CameraObjectBlockedDetour);

        IsObjectInViewRangeHook ??= 
            IsObjectInViewRangeSig.GetHook<IsObjectInViewRangeDelegate>(IsObjectInViewRangeHookDetour);

        InteractCheck0Hook ??= InteractCheck0Sig.GetHook<InteractCheck0Delegate>(InteractCheck0Detour);

        IsPlayerOnJumping0Hook ??= IsPlayerOnJumping0Sig.GetHook<IsPlayerOnJumpingDelegate>(IsPlayerOnJumpingDetour);
        IsPlayerOnJumping1Hook ??= IsPlayerOnJumping1Sig.GetHook<IsPlayerOnJumpingDelegate>(IsPlayerOnJumpingDetour);
        IsPlayerOnJumping2Hook ??= IsPlayerOnJumping2Sig.GetHook<IsPlayerOnJumpingDelegate>(IsPlayerOnJumpingDetour);

        CheckTargetPositionHook ??=
            CheckTargetPositionSig.GetHook<CheckTargetPositionDelegate>(CheckTargetPositionDetour);

        CheckTargetDistanceHook ??= 
            CheckTargetDistanceSig.GetHook<CheckTargetDistanceDelegate>(CheckTargetDistanceDetour);

        InteractWithObjectHook ??=
            InteractWithObjectSig.GetHook<InteractWithObjectDelegate>(InteractWithObjectDetour);

        CheckCameraPositionHook ??=
            CheckCameraPositionSig.GetHook<CheckCameraPositionDelegate>(CheckCameraPositionDetour);
    }

    private static void SwitchHooks(bool isEnable)
    {
        CameraObjectBlockedHook.Toggle(isEnable);
        IsObjectInViewRangeHook.Toggle(isEnable);
        InteractCheck0Hook.Toggle(isEnable);
        IsPlayerOnJumping0Hook.Toggle(isEnable);
        IsPlayerOnJumping1Hook.Toggle(isEnable);
        IsPlayerOnJumping2Hook.Toggle(isEnable);
        CheckTargetPositionHook.Toggle(isEnable);
        CheckTargetDistanceHook.Toggle(isEnable);
        InteractWithObjectHook.Toggle(isEnable);
        CheckCameraPositionHook.Toggle(isEnable);
    }

    private static bool CameraObjectBlockedDetour(nint a1, nint a2, nint a3) => true;

    private static bool IsObjectInViewRangeHookDetour(TargetSystem* system, GameObject* gameObject) => true;

    private static bool InteractCheck0Detour(nint a1, nint a2, nint a3, nint a4, bool a5) => true;

    private static bool IsPlayerOnJumpingDetour(nint a1) => false;

    private static bool CheckTargetPositionDetour(
        EventFramework* framework, GameObject* source, GameObject* target, ushort interactType, bool sendError) => true;

    private static float CheckTargetDistanceDetour(GameObject* localPlayer, GameObject* target) => 0f;
    
    private ulong InteractWithObjectDetour(TargetSystem* system, GameObject* obj, bool checkLOS)
    {
        if (obj == null || DService.ObjectTable.LocalPlayer is not { } localPlayer) return 0;
        
        // 咏唱状态
        MemoryHelper.Write(DService.Condition.Address + 27, false);
        
        // 动画锁
        ActionManager.Instance()->AnimationLock = 0;
        
        // 以太之光
        if (obj->ObjectKind is ObjectKind.Aetheryte &&
            EventFrameworkHelper.TryGetNearestEventID(x => x.EventId.ContentId is EventHandlerContent.Aetheryte,
                                                      x => x.NameString == obj->NameString,
                                                      localPlayer.Position, out var eventIDAetheryte) &&
            FirstQuest.Any(x => UIState.Instance()->IsUnlockLinkUnlockedOrQuestCompleted(x)))
        {
            DismountAndSend(eventIDAetheryte);
            return 1;
        }

        // 一些通用的
        if (WhitelistObjectNames.Contains(obj->NameString))
        {
            if (obj->EventHandler != null)
            {
                var info = obj->EventHandler->Info;
                DismountAndSend(info.EventId.Id);
                return 1;
            }
            else if (EventFrameworkHelper.TryGetNearestEventID(_ => true, x => x.NameString == obj->NameString,
                                                               localPlayer.Position, out var eventIDWhitelist))
            {
                DismountAndSend(eventIDWhitelist);
                return 1;
            }
        }

        return InteractWithObjectHook.Original(system, obj, false);

        void DismountAndSend(uint eventID)
        {
            MovementManager.Dismount();
            TaskHelper.Enqueue(() =>
            {
                if (MovementManager.IsManagerBusy || DService.Condition[ConditionFlag.Mounted] ||
                    DService.Condition[ConditionFlag.Jumping]) return false;
                new EventStartPackt(localPlayer.GameObjectId, eventID).Send();
                return true;
            });
        }
    }

    private static bool CheckCameraPositionDetour(TargetSystem* system, Camera* activeCamera, GameObject* obj)
        => true;
}
