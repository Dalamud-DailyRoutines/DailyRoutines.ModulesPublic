using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.System.Resource.Handle;
using FFXIVClientStructs.Interop;
using FFXIVClientStructs.STD;
using InteropGenerator.Runtime;
using OmenTools.Interop.Game.Models;
using Control = FFXIVClientStructs.FFXIV.Client.Game.Control.Control;

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

    private PartialAnimationPackResourceHandle* humanNinjaPap;
    private PartialAnimationPackResourceHandle* lalafellNinjaPap;

    protected override void Init()
    {
        humanNinjaPap    = LoadNinjaPap(HUMAN_NINJA_PAP_PATH);
        lalafellNinjaPap = LoadNinjaPap(LALAFELL_NINJA_PAP_PATH);

        GetAnimationBindingHook = GetAnimationBindingSig.GetHook<GetAnimationBindingDelegate>(GetAnimationBindingDetour);
        GetAnimationBindingHook.Enable();
    }

    protected override void Uninit()
    {
        ReleaseNinjaPap(ref humanNinjaPap);
        ReleaseNinjaPap(ref lalafellNinjaPap);
    }

    private nint GetAnimationBindingDetour
    (
        PartialAnimationPackResourceHandle* pap,
        byte*                               animationName
    )
    {
        var binding = GetAnimationBindingHook.Original(pap, animationName);

        if (binding                      == nint.Zero ||
            pap                          == null      ||
            pap->HavokAnimationContainer == null      ||
            animationName                == null      ||
            !IsJumpAnimation(animationName)           ||
            !IsLocalPlayerAnimationPack(pap))
            return binding;

        var ninjaPap = GetLocalPlayerNinjaPap();
        if (ninjaPap                          == null ||
            ninjaPap                          == pap  ||
            ninjaPap->HavokAnimationContainer == null ||
            ninjaPap->AnimationCount          == 0)
            return binding;

        var ninjaIndex = ninjaPap->GetAnimationHavokIndex(new CStringPointer(animationName));
        if (ninjaIndex < 0 ||
            ninjaIndex >= ninjaPap->HavokAnimationContainer->Bindings.Length)
            return binding;

        var ninjaBinding = ninjaPap->HavokAnimationContainer->Bindings.Data[ninjaIndex].ptr;
        return ninjaBinding == null ?
                   binding :
                   (nint)ninjaBinding;
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

    private static bool IsLocalPlayerAnimationPack
    (
        PartialAnimationPackResourceHandle* pap
    )
    {
        var localPlayer = Control.GetLocalPlayer();
        if (localPlayer == null)
            return false;

        var characterBase = localPlayer->GetCharacterBase();
        if (characterBase == null)
            return false;

        foreach (var animationPack in characterBase->AnimationsToLoad.AsSpan())
        {
            if (animationPack.Value == pap)
                return true;
        }

        foreach (var animationContainer in characterBase->SkeletonAnimationContainers)
        {
            if (ContainsAnimationPack(animationContainer.PapVector1, pap) ||
                ContainsAnimationPack(animationContainer.PapVector2, pap) ||
                ContainsAnimationPack(animationContainer.PapVector3, pap))
                return true;
        }

        return false;
    }

    private static bool ContainsAnimationPack
    (
        StdVector<Pointer<ResourceHandle>>  animationPacks,
        PartialAnimationPackResourceHandle* pap
    )
    {
        foreach (var animationPack in animationPacks.AsSpan())
        {
            if (animationPack.Value == (ResourceHandle*)pap)
                return true;
        }

        return false;
    }

    private PartialAnimationPackResourceHandle* GetLocalPlayerNinjaPap()
    {
        var localPlayer = Control.GetLocalPlayer();
        if (localPlayer == null)
            return null;

        return localPlayer->DrawData.CustomizeData.Race == LALAFELL_RACE ?
                   lalafellNinjaPap :
                   humanNinjaPap;
    }

    private static PartialAnimationPackResourceHandle* LoadNinjaPap
    (
        string path
    ) =>
        (PartialAnimationPackResourceHandle*)ResourceHandle.GetSync(path);

    private static void ReleaseNinjaPap
    (
        ref PartialAnimationPackResourceHandle* pap
    )
    {
        if (pap == null)
            return;

        pap->DecRef();
        pap = null;
    }

    #region 常量

    private const byte   LALAFELL_RACE           = 4;
    private const string HUMAN_NINJA_PAP_PATH    = "chara/human/c0101/animation/a0001/bt_nin_nin/resident/move_a.pap";
    private const string LALAFELL_NINJA_PAP_PATH = "chara/human/c1101/animation/a0001/bt_nin_nin/resident/move_a.pap";

    #endregion
}
