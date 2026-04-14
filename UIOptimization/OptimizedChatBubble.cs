using System.Runtime.InteropServices;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using FFXIVClientStructs.FFXIV.Component.Text;
using OmenTools.Interop.Game;
using OmenTools.Interop.Game.Models;

namespace DailyRoutines.ModulesPublic;

public unsafe class OptimizedChatBubble : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("OptimizedChatBubbleTitle"),
        Description = Lang.Get("OptimizedChatBubbleDescription"),
        Category    = ModuleCategory.UIOptimization,
        Author      = ["Middo", "Xww"]
    };

    public override ModulePermission Permission { get; } = new() { AllDefaultEnabled = true };
    
    private static readonly CompSig ChatBubbleSig = new("E8 ?? ?? ?? ?? 0F B6 E8 48 8D 5F 18 40 0A 6C 24 ?? BE");
    private delegate        ulong   ChatBubbleDelegate(ChatBubbleStruct* chatBubbleStruct);
    private          Hook<ChatBubbleDelegate> ChatBubbleHook;

    private static readonly CompSig SetupChatBubbleSig = new("E8 ?? ?? ?? ?? 49 FF 46 60");
    private delegate        byte    SetupChatBubbleDelegate(nint       unk,         nint        newBubble, nint a3);
    private delegate        uint    GetStringSizeDelegate(TextChecker* textChecker, Utf8String* str);
    private          Hook<SetupChatBubbleDelegate> SetupChatBubbleHook;

    private static readonly CompSig               GetStringSizeSig = new("E8 ?? ?? ?? ?? 49 8D 56 40");
    private readonly GetStringSizeDelegate getStringSize    = GetStringSizeSig.GetDelegate<GetStringSizeDelegate>();

    private readonly MemoryPatch showMiniTalkPlayerPatch = new("0F 84 ?? ?? ?? ?? ?? ?? ?? 48 8B CF 49 89 46", [0x90, 0xE9]);

    private Config moduleConfig = null!;

    private readonly HashSet<nint> newBubbles = [];

    protected override void Init()
    {
        moduleConfig = Config.Load(this) ?? new();

        ChatBubbleHook = ChatBubbleSig.GetHook<ChatBubbleDelegate>(ChatBubbleDetour);
        ChatBubbleHook.Enable();

        SetupChatBubbleHook = SetupChatBubbleSig.GetHook<SetupChatBubbleDelegate>(SetupChatBubbleDetour);
        SetupChatBubbleHook.Enable();

        showMiniTalkPlayerPatch.Set(moduleConfig.IsShowInCombat);
    }

    protected override void ConfigUI()
    {
        if (ImGui.Checkbox(Lang.Get("OptimizedChatBubble-ShowInCombat"), ref moduleConfig.IsShowInCombat))
        {
            moduleConfig.Save(this);
            showMiniTalkPlayerPatch.Set(moduleConfig.IsShowInCombat);
        }

        using (ImRaii.ItemWidth(150f * GlobalUIScale))
        {
            if (ImGui.InputInt(Lang.Get("OptimizedChatBubble-MaxLine"), ref moduleConfig.MaxLines, 1))
                moduleConfig.MaxLines = Math.Clamp(moduleConfig.MaxLines, 1, 7);
            if (ImGui.IsItemDeactivatedAfterEdit())
                moduleConfig.Save(this);

            if (ImGui.InputUInt($"{Lang.Get("OptimizedChatBubble-BaseDuration")} (ms)", ref moduleConfig.Duration, 500, 1000))
                moduleConfig.Duration = Math.Clamp(moduleConfig.Duration, 1000, 60_000);
            if (ImGui.IsItemDeactivatedAfterEdit())
                moduleConfig.Save(this);

            if (ImGui.InputUInt($"{Lang.Get("OptimizedChatBubble-AdditionalDuration")} (ms)", ref moduleConfig.AdditionalDuration, 1, 10))
                moduleConfig.AdditionalDuration = Math.Clamp(moduleConfig.AdditionalDuration, 0, 10_000);
            if (ImGui.IsItemDeactivatedAfterEdit())
                moduleConfig.Save(this);
        }
    }

    private ulong ChatBubbleDetour(ChatBubbleStruct* chatBubbleStruct)
    {
        try
        {
            return ChatBubbleHook.Original(chatBubbleStruct);
        }
        finally
        {
            chatBubbleStruct->LineCount = (byte)Math.Clamp(moduleConfig.MaxLines, 1, 7);

            newBubbles.RemoveWhere
            (b =>
                {
                    var bubble = (ChatBubbleEntry*)b;

                    if (bubble->Timestamp < 200)
                    {
                        if (bubble->Timestamp >= 0)
                            bubble->Timestamp++;
                        return false;
                    }

                    bubble->Timestamp += moduleConfig.Duration - 4000;

                    if (moduleConfig.AdditionalDuration > 0)
                    {
                        var characterCounts    = getStringSize(&RaptureTextModule.Instance()->TextChecker, &bubble->String);
                        var additionalDuration = moduleConfig.AdditionalDuration * Math.Clamp(characterCounts, 0, 194 * moduleConfig.MaxLines);
                        bubble->Timestamp += additionalDuration;
                    }

                    return true;
                }
            );
        }
    }

    private byte SetupChatBubbleDetour(nint unk, nint newBubble, nint a3)
    {
        try
        {
            if (moduleConfig.Duration != 4000 || moduleConfig.AdditionalDuration > 0)
                newBubbles.Add(newBubble);

            return SetupChatBubbleHook.Original(unk, newBubble, a3);
        }
        catch
        {
            return 0;
        }
    }
    
    [StructLayout(LayoutKind.Explicit)]
    private struct ChatBubbleStruct
    {
        [FieldOffset(0x8C)]
        public byte LineCount;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct ChatBubbleEntry
    {
        [FieldOffset(0x000)]
        public Utf8String String;

        [FieldOffset(0x1B8)]
        public long Timestamp;
    }

    private class Config : ModuleConfig
    {
        public uint AdditionalDuration;
        public uint Duration       = 4000;
        public bool IsShowInCombat = true;
        public int  MaxLines       = 2;
    }
}
