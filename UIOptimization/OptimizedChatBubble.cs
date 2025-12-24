using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using DailyRoutines.Abstracts;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using FFXIVClientStructs.FFXIV.Component.Text;

namespace DailyRoutines.ModulesPublic;

public unsafe class OptimizedChatBubble : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("OptimizedChatBubbleTitle"),
        Description = GetLoc("OptimizedChatBubbleDescription"),
        Category    = ModuleCategories.UIOptimization,
        Author      = ["Middo","Xww"]
    };
    
    public override ModulePermission Permission { get; } = new() { AllDefaultEnabled = true };

    private static readonly CompSig                  ChatBubbleSig = new("E8 ?? ?? ?? ?? 0F B6 E8 48 8D 5F 18 40 0A 6C 24 ?? BE");
    private delegate        ulong                    ChatBubbleDelegate(ChatBubbleStruct* chatBubbleStruct);
    private static          Hook<ChatBubbleDelegate> ChatBubbleHook;

    private static readonly CompSig                       SetupChatBubbleSig = new("E8 ?? ?? ?? ?? 49 FF 46 60");
    private delegate        byte                          SetupChatBubbleDelegate(nint unk, nint newBubble, nint a3);
    private static          Hook<SetupChatBubbleDelegate> SetupChatBubbleHook;

    private static readonly CompSig               GetStringSizeSig = new("E8 ?? ?? ?? ?? 49 8D 56 40");
    private delegate        uint                  GetStringSizeDelegate(TextChecker* textChecker, Utf8String* str);
    private static readonly GetStringSizeDelegate GetStringSize = GetStringSizeSig.GetDelegate<GetStringSizeDelegate>();

    private static readonly MemoryPatch ShowMiniTalkPlayerPatch = new("0F 84 ?? ?? ?? ?? ?? ?? ?? 48 8B CF 49 89 46", [0x90, 0xE9]);

    private static Config ModuleConfig = null!;

    private static readonly HashSet<nint> NewBubbles = [];

    protected override void Init()
    {
        ModuleConfig = LoadConfig<Config>() ?? new();

        ChatBubbleHook = ChatBubbleSig.GetHook<ChatBubbleDelegate>(ChatBubbleDetour);
        ChatBubbleHook.Enable();

        SetupChatBubbleHook = SetupChatBubbleSig.GetHook<SetupChatBubbleDelegate>(SetupChatBubbleDetour);
        SetupChatBubbleHook.Enable();

        ShowMiniTalkPlayerPatch.Set(ModuleConfig.IsShowInCombat);
    }

    protected override void ConfigUI()
    {
        if (ImGui.Checkbox(GetLoc("OptimizedChatBubble-ShowInCombat"), ref ModuleConfig.IsShowInCombat))
        {
            SaveConfig(ModuleConfig);
            ShowMiniTalkPlayerPatch.Set(ModuleConfig.IsShowInCombat);
        }

        using (ImRaii.ItemWidth(150f * GlobalFontScale))
        {
            if (ImGui.InputInt(GetLoc("OptimizedChatBubble-MaxLine"), ref ModuleConfig.MaxLines, 1))
                ModuleConfig.MaxLines = Math.Clamp(ModuleConfig.MaxLines, 1, 7);
            if (ImGui.IsItemDeactivatedAfterEdit())
                SaveConfig(ModuleConfig);

            if (ImGui.InputUInt($"{GetLoc("OptimizedChatBubble-BaseDuration")} (ms)", ref ModuleConfig.Duration, 500, 1000))
                ModuleConfig.Duration = Math.Clamp(ModuleConfig.Duration, 1000, 60_000);
            if (ImGui.IsItemDeactivatedAfterEdit())
                SaveConfig(ModuleConfig);

            if (ImGui.InputUInt($"{GetLoc("OptimizedChatBubble-AdditionalDuration")} (ms)", ref ModuleConfig.AdditionalDuration, 1, 10))
                ModuleConfig.AdditionalDuration = Math.Clamp(ModuleConfig.AdditionalDuration, 0, 10_000);
            if (ImGui.IsItemDeactivatedAfterEdit())
                SaveConfig(ModuleConfig);
        }
    }

    private static ulong ChatBubbleDetour(ChatBubbleStruct* chatBubbleStruct)
    {
        try
        {
            return ChatBubbleHook.Original(chatBubbleStruct);
        }
        finally
        {
            chatBubbleStruct->LineCount = (byte)Math.Clamp(ModuleConfig.MaxLines, 1, 7);

            NewBubbles.RemoveWhere(b =>
            {
                var bubble = (ChatBubbleEntry*)b;
                if (bubble->Timestamp < 200)
                {
                    if (bubble->Timestamp >= 0)
                        bubble->Timestamp++;
                    return false;
                }

                bubble->Timestamp += ModuleConfig.Duration - 4000;
                if (ModuleConfig.AdditionalDuration > 0)
                {
                    var characterCounts = GetStringSize(&RaptureTextModule.Instance()->TextChecker, &bubble->String);
                    var additionalDuration = ModuleConfig.AdditionalDuration * Math.Clamp(characterCounts, 0, 194 * ModuleConfig.MaxLines);
                    bubble->Timestamp += additionalDuration;
                }
                return true;
            });
        }
    }
    
    private static byte SetupChatBubbleDetour(nint unk, nint newBubble, nint a3)
    {
        try
        {
            if (ModuleConfig.Duration != 4000 || ModuleConfig.AdditionalDuration > 0)
                NewBubbles.Add(newBubble);
            
            return SetupChatBubbleHook.Original(unk, newBubble, a3);
        }
        catch
        {
            return 0;
        }
    }

    protected override void Uninit() => ShowMiniTalkPlayerPatch.Dispose();

    [StructLayout(LayoutKind.Explicit)]
    private struct ChatBubbleStruct
    {
        [FieldOffset(0x8C)] public byte LineCount;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct ChatBubbleEntry
    {
        [FieldOffset(0x000)] public Utf8String String;
        [FieldOffset(0x1B8)] public long Timestamp;
    }

    private class Config : ModuleConfiguration
    {
        public int  MaxLines       = 2;
        public uint Duration       = 4000;
        public bool IsShowInCombat = true;
        public uint AdditionalDuration;
    }
}
