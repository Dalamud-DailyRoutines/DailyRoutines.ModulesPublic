using System;
using DailyRoutines.Abstracts;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace DailyRoutines.ModulesPublic;

public unsafe class LargerIME : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("LargerIMETitle"),
        Description = GetLoc("LargerIMEDescription"),
        Category    = ModuleCategories.UIOptimization
    };
    
    private static readonly CompSig TextInputReceiveEventSig =
        new("40 55 53 57 41 56 41 57 48 8D AC 24 ?? ?? ?? ?? 48 81 EC ?? ?? ?? ?? 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 85 ?? ?? ?? ?? 48 8B 9D ?? ?? ?? ??");
    private delegate void TextInputReceiveEventDelegate(AtkComponentTextInput* component, AtkEventType eventType, int i, AtkEvent* atkEvent, AtkEventData* eventData);
    private static   Hook<TextInputReceiveEventDelegate>? TextInputReceiveEventHook;

    private static Config ModuleConfig = null!;

    protected override void Init()
    {
        ModuleConfig = LoadConfig<Config>() ?? new();

        TextInputReceiveEventHook ??= TextInputReceiveEventSig.GetHook<TextInputReceiveEventDelegate>(TextInputReceiveEventDetour);
        TextInputReceiveEventHook.Enable();
    }

    protected override void ConfigUI()
    {
        ImGui.SetNextItemWidth(100f * GlobalFontScale);
        if (ImGui.InputFloat($"{GetLoc("Scale")}###FontScaleInput", ref ModuleConfig.Scale, 0.1f, 1, "%.1f"))
            ModuleConfig.Scale = MathF.Max(0.1f, ModuleConfig.Scale);
        if (ImGui.IsItemDeactivatedAfterEdit())
            SaveConfig(ModuleConfig);
    }
    
    private static void TextInputReceiveEventDetour(
        AtkComponentTextInput* component, 
        AtkEventType eventType, 
        int i, 
        AtkEvent* atkEvent, 
        AtkEventData* eventData)
    {
        TextInputReceiveEventHook.Original(component, eventType, i, atkEvent, eventData);
        
        ModifyTextInputComponent(component);
    }

    private static void ModifyTextInputComponent(AtkComponentTextInput* component)
    {
        if (component == null) return;

        var imeBackground = component->AtkComponentInputBase.AtkComponentBase.UldManager.SearchNodeById(4);
        if (imeBackground == null) return;
        
        imeBackground->SetScale(ModuleConfig.Scale, ModuleConfig.Scale);
    }

    private class Config : ModuleConfiguration
    {
        public float Scale = 2f;
    }
}
