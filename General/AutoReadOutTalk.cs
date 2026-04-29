using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Hooking;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using InteropGenerator.Runtime;
using OmenTools.OmenService;

namespace DailyRoutines.ModulesPublic;

public unsafe class AutoReadOutTalk : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title           = Lang.Get("AutoReadOutTalkTitle"),
        Description     = Lang.Get("AutoReadOutTalkDescription"),
        Category        = ModuleCategory.General,
        ModulesConflict = ["AutoTalkSkip"]
    };

    private Config config = null!;
    
    private delegate void ShowBattleTalkDelegate(UIModule* module, CStringPointer name, CStringPointer text, float duration, byte style);
    private Hook<ShowBattleTalkDelegate>? ShowBattleTalkHook;
    
    private delegate void ShowBattleTalkImageDelegate
    (
        UIModule*      module,
        CStringPointer name,
        CStringPointer text,
        float          duration,
        uint           image,
        byte           style,
        int            sound,
        uint           entityID
    );
    private Hook<ShowBattleTalkImageDelegate>? ShowBattleTalkImageHook;

    protected override void Init()
    {
        config = Config.Load(this) ?? new();

        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PostRefresh, "Talk", OnAddon);
        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "Talk", OnAddon);
        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PreHide,     "Talk", OnAddon);

        ShowBattleTalkHook = UIModule.Instance()->VirtualTable->HookVFuncFromName("ShowBattleTalk", (ShowBattleTalkDelegate)ShowBattleTalkDetour);
        ShowBattleTalkHook.Enable();

        ShowBattleTalkImageHook = UIModule.Instance()->VirtualTable->HookVFuncFromName
            ("ShowBattleTalkImage", (ShowBattleTalkImageDelegate)ShowBattleTalkImageDetour);
        ShowBattleTalkImageHook.Enable();
    }

    protected override void Uninit()
    {
        DService.Instance().AddonLifecycle.UnregisterListener(OnAddon);
        CancelBefore();
    }

    protected override void ConfigUI()
    {
        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), Lang.Get("Format"));

        using (ImRaii.PushIndent())
        {
            ImGui.InputText($"##FormatInput", ref config.Format);
            if (ImGui.IsItemDeactivatedAfterEdit())
                config.Save(this);
        }
    }

    private void ShowBattleTalkDetour(UIModule* module, CStringPointer name, CStringPointer text, float duration, byte style)
    {
        ShowBattleTalkHook.Original(module, name, text, duration, style);

        var speaker = name.HasValue ? name.ExtractText() : string.Empty;
        var line    = text.HasValue ? text.ExtractText() : string.Empty;

        if (string.IsNullOrEmpty(line) || string.IsNullOrEmpty(speaker) || duration < 3) return;

        CancelBefore();
        NotifyHelper.Speak(string.Format(config.Format, speaker, line));
    }

    private void ShowBattleTalkImageDetour
    (
        UIModule*      module,
        CStringPointer name,
        CStringPointer text,
        float          duration,
        uint           image,
        byte           style,
        int            sound,
        uint           entityID
    )
    {
        ShowBattleTalkImageHook.Original(module, name, text, duration, image, style, sound, entityID);

        if (sound > -1) return;

        var speaker = name.HasValue ? name.ExtractText() : string.Empty;
        var line    = text.HasValue ? text.ExtractText() : string.Empty;

        if (string.IsNullOrEmpty(line) || string.IsNullOrEmpty(speaker) || duration < 3) return;

        CancelBefore();
        NotifyHelper.Speak(string.Format(config.Format, speaker, line));
    }

    private void OnAddon(AddonEvent type, AddonArgs args)
    {
        switch (type)
        {
            case AddonEvent.PostRefresh:
                string? line    = null;
                string? speaker = null;

                if (Talk == null) return;
                
                // 没有实际文本
                if (Talk->AtkValues[0].Type != AtkValueType.ManagedString || !Talk->AtkValues[0].String.HasValue) return;

                // 没有说话人
                if (Talk->AtkValues[1].Type == AtkValueType.ManagedString && Talk->AtkValues[1].String.HasValue)
                    speaker = Talk->AtkValues[1].String.ExtractText();

                // 非普通对话
                if (Talk->AtkValues[3].Type != AtkValueType.UInt || Talk->AtkValues[3].UInt != 0) return;

                line = Talk->AtkValues[0].String.ExtractText();

                if (string.IsNullOrEmpty(line)) return;

                CancelBefore();
                NotifyHelper.Speak(string.Format(config.Format, speaker, line));
                break;

            case AddonEvent.PreFinalize:
            case AddonEvent.PreHide:
                CancelBefore();
                break;
        }
    }

    private static void CancelBefore() =>
        NotifyHelper.StopSpeak();

    private class Config : ModuleConfig
    {
        public string Format = "{0}: {1}";
    }
}
