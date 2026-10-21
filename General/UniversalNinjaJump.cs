using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.System.Resource.Handle;
using InteropGenerator.Runtime;
using OmenTools.Interop.Game.Models;

namespace DailyRoutines.ModulesPublic;

public unsafe class UniversalNinjaJump : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("UniversalNinjaJumpTitle"),
        Description = Lang.Get("UniversalNinjaJumpDescription"),
        Category    = ModuleCategory.General
    };

    private static readonly CompSig GetAnimationBindingSig = new("40 53 48 83 EC 20 48 8B D9 E8 F2 00 00 00 0F B7 C8");
    private delegate nint GetAnimationBindingDelegate
    (
        PartialAnimationPackResourceHandle* pap,
        byte*                               animationName
    );
    private Hook<GetAnimationBindingDelegate>? GetAnimationBindingHook;

    protected override void Init()
    {
        _ = GetNinjaPap();

        GetAnimationBindingHook = GetAnimationBindingSig.GetHook<GetAnimationBindingDelegate>(GetAnimationBindingDetour);
        GetAnimationBindingHook.Enable();
    }

    private nint GetAnimationBindingDetour
    (
        PartialAnimationPackResourceHandle* pap,
        byte*                               animationName
    )
    {
        if (pap                          != null &&
            pap->HavokAnimationContainer != null &&
            animationName                != null &&
            IsJumpAnimation(animationName))
            ReplaceBinding(pap, animationName);

        return GetAnimationBindingHook.Original(pap, animationName);
    }

    private static void ReplaceBinding
    (
        PartialAnimationPackResourceHandle* pap,
        byte*                               animationName
    )
    {
        var ninjaPap = GetNinjaPap();

        if (ninjaPap == null ||
            ninjaPap == pap)
            return;
        if (ninjaPap->AnimationCount          == 0 ||
            ninjaPap->HavokAnimationContainer == null)
            return;

        var name  = new CStringPointer(animationName);
        var index = pap->GetAnimationHavokIndex(name);
        if (index < 0 ||
            index >= pap->HavokAnimationContainer->Bindings.Length)
            return;

        var ninjaIndex = ninjaPap->GetAnimationHavokIndex(name);
        if (ninjaIndex < 0 ||
            ninjaIndex >= ninjaPap->HavokAnimationContainer->Bindings.Length)
            return;

        var ninjaBinding = ninjaPap->HavokAnimationContainer->Bindings.Data[ninjaIndex].ptr;
        if (ninjaBinding == null)
            return;

        pap->HavokAnimationContainer->Bindings.Data[index].ptr = ninjaBinding;
    }

    private static bool IsJumpAnimation
    (
        byte* animationName
    )
    {
        if (animationName[0] != (byte)'c') return false;

        var name = new CStringPointer(animationName).AsSpan();

        return name.SequenceEqual("cbbm_jump_1"u8) ||
               name.SequenceEqual("cbbm_jump_2"u8) ||
               name.SequenceEqual("cbbm_jump_3"u8);
    }

    private static PartialAnimationPackResourceHandle* GetNinjaPap() =>
        (PartialAnimationPackResourceHandle*)ResourceHandle.GetSync(NINJA_PAP_PATH);

    #region 常量

    private const string NINJA_PAP_PATH = "chara/human/c0101/animation/a0001/bt_nin_nin/resident/move_a.pap";

    #endregion
}
