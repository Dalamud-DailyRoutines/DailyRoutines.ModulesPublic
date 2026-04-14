using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Component.GUI;
using OmenTools.Interop.Game.Models;

namespace DailyRoutines.ModulesPublic;

public unsafe class LargerIME : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("LargerIMETitle"),
        Description = Lang.Get("LargerIMEDescription"),
        Category    = ModuleCategory.UIOptimization
    };

    public override ModulePermission Permission { get; } = new() { AllDefaultEnabled = true };
    
    private static readonly CompSig TextInputReceiveEventSig =
        new("4C 8B DC 55 53 57 41 54 41 57 49 8D AB ?? ?? ?? ?? 48 81 EC ?? ?? ?? ?? 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 85 ?? ?? ?? ?? 48 8B 9D ?? ?? ?? ??");
    private delegate void TextInputReceiveEventDelegate
    (
        AtkComponentTextInput* component,
        AtkEventType           eventType,
        int                    i,
        AtkEvent*              atkEvent,
        AtkEventData*          eventData
    );
    private Hook<TextInputReceiveEventDelegate>? TextInputReceiveEventHook;

    private Config config = null!;

    protected override void Init()
    {
        config = Config.Load(this) ?? new();

        TextInputReceiveEventHook ??= TextInputReceiveEventSig.GetHook<TextInputReceiveEventDelegate>(TextInputReceiveEventDetour);
        TextInputReceiveEventHook.Enable();
    }

    protected override void ConfigUI()
    {
        ImGui.SetNextItemWidth(100f * GlobalUIScale);
        if (ImGui.InputFloat($"{Lang.Get("Scale")}###FontScaleInput", ref config.Scale, 0.1f, 1, "%.1f"))
            config.Scale = MathF.Max(0.1f, config.Scale);
        if (ImGui.IsItemDeactivatedAfterEdit())
            config.Save(this);
    }

    private void TextInputReceiveEventDetour
    (
        AtkComponentTextInput* component,
        AtkEventType           eventType,
        int                    i,
        AtkEvent*              atkEvent,
        AtkEventData*          eventData
    )
    {
        TextInputReceiveEventHook.Original(component, eventType, i, atkEvent, eventData);

        ModifyTextInputComponent(component);
    }

    private void ModifyTextInputComponent(AtkComponentTextInput* component)
    {
        if (component == null) return;

        var imeBackground = component->AtkComponentInputBase.AtkComponentBase.UldManager.SearchNodeById(4);
        if (imeBackground == null) return;

        imeBackground->SetScale(config.Scale, config.Scale);
    }

    private class Config : ModuleConfig
    {
        public float Scale = 2f;
    }
}
