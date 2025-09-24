using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using DailyRoutines.Abstracts;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace DailyRoutines.ModulesPublic;

public unsafe class AutoChangeKeyboardLayout : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("AutoChangeKeyboardLayoutTitle"),
        Description = GetLoc("AutoChangeKeyboardLayoutDescription"),
        Category    = ModuleCategories.System,
        Author      = ["JiaXX"]
    };
    
    private static readonly CompSig SetTextInputTargetSig =
        new("4C 8B DC 55 53 57 41 54 41 57 49 8D AB ?? ?? ?? ?? 48 81 EC ?? ?? ?? ?? 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 85 ?? ?? ?? ?? 48 8B 9D ?? ?? ?? ??");
    private delegate void SetTextInputTargetDelegate(
        AtkComponentTextInput* component,
        AtkEventType           eventType,
        int                    eventParam,
        AtkEvent*              atkEvent,
        AtkEventData*          atkEventData);
    private static Hook<SetTextInputTargetDelegate>? SetTextInputTargetHook;

    private static Config ModuleConfig = null!;
    
    private static Dictionary<ushort, KeyboardLayoutInfo>? CachedLayouts;

    protected override void Init()
    {
        ModuleConfig = LoadConfig<Config>() ?? new();

        var currentLayoutHandle = InputMethodController.CurrentLayout;
        var currentLangID       = (ushort)(currentLayoutHandle.ToInt64() & 0xFFFF);

        if (ModuleConfig.FocusLayoutLangID == 0)
        {
            ModuleConfig.FocusLayoutLangID = currentLangID;
            SaveConfig(ModuleConfig);
        }

        if (ModuleConfig.UnfocusLayoutLangID == 0)
        {
            ModuleConfig.UnfocusLayoutLangID = currentLangID;
            SaveConfig(ModuleConfig);
        }

        SetTextInputTargetHook ??= SetTextInputTargetSig.GetHook<SetTextInputTargetDelegate>(ChangeKeyboardLayout);
        SetTextInputTargetHook.Enable();
    }

    protected override void ConfigUI()
    {
        if (Throttler.Throttle("AutoChangeKeyboardLayout-GetLayouts", 1_000))
            CachedLayouts = InputMethodController.GetAllKeyboardLayouts();

        if (CachedLayouts == null) return;

        // 聚焦时的布局选择
        ImGui.Text(GetLoc("Focused"));

        using (var focusCombo = ImRaii.Combo("##FocusLayout", CachedLayouts.GetValueOrDefault(ModuleConfig.FocusLayoutLangID).Name ?? GetLoc("Unknown")))
        {
            if (focusCombo)
            {
                foreach (var (langID, layout) in CachedLayouts)
                {
                    var isSelected = ModuleConfig.FocusLayoutLangID == langID;
                    if (ImGui.Selectable(layout.Name, isSelected))
                    {
                        ModuleConfig.FocusLayoutLangID = langID;
                        SaveConfig(ModuleConfig);
                    }

                    if (isSelected)
                        ImGui.SetItemDefaultFocus();
                }
            }
        }

        ImGui.Spacing();

        // 失焦时的布局选择
        ImGui.Text(GetLoc("Unfocused"));
        
        using (var unfocusCombo = ImRaii.Combo("##UnfocusLayout", CachedLayouts.GetValueOrDefault(ModuleConfig.UnfocusLayoutLangID).Name ?? GetLoc("Unknown")))
        {
            if (unfocusCombo)
            {
                foreach (var (langID, layout) in CachedLayouts)
                {
                    var isSelected = ModuleConfig.UnfocusLayoutLangID == langID;
                    if (ImGui.Selectable(layout.Name, isSelected))
                    {
                        ModuleConfig.UnfocusLayoutLangID = langID;
                        SaveConfig(ModuleConfig);
                    }

                    if (isSelected)
                        ImGui.SetItemDefaultFocus();
                }
            }
        }

        ImGui.NewLine();

        var currentLangID = (ushort)(InputMethodController.CurrentLayout.ToInt64() & 0xFFFF);
        ImGui.Text($"{GetLoc("Current")}\n\t\t{CachedLayouts.GetValueOrDefault(currentLangID).Name ?? GetLoc("Unknown")}");
    }

    private static void ChangeKeyboardLayout(
        AtkComponentTextInput* component,
        AtkEventType           eventType,
        int                    eventParam,
        AtkEvent*              atkEvent,
        AtkEventData*          atkEventData)
    {
        SetTextInputTargetHook!.Original(component, eventType, eventParam, atkEvent, atkEventData);

        switch (eventType)
        {
            case AtkEventType.FocusStart: // 聚焦
                DService.Framework.RunOnTick(() => CheckSlashAndSwitchLayout(component), TimeSpan.FromMilliseconds(50));
                break;
            case AtkEventType.FocusStop: // 失焦
                var unfocusLayout = InputMethodController.FindKeyboardLayout(ModuleConfig.UnfocusLayoutLangID);
                if (unfocusLayout != nint.Zero)
                    InputMethodController.SwitchToLayout(unfocusLayout);
                break;
        }
    }

    private static void CheckSlashAndSwitchLayout(AtkComponentTextInput* textInputEventInterface)
    {
        if (textInputEventInterface == null) return;

        var textNode = textInputEventInterface->AtkTextNode;
        if (textNode == null) return;

        // 指令
        var nodeText = textNode->NodeText.ExtractText();
        if (nodeText.StartsWith('/')) return;

        var focusLayout = InputMethodController.FindKeyboardLayout(ModuleConfig.FocusLayoutLangID);
        if (focusLayout != nint.Zero)
            InputMethodController.SwitchToLayout(focusLayout);
    }

    private static class InputMethodController
    {
        [DllImport("user32.dll")]
        private static extern void ActivateKeyboardLayout(nint hkl, uint Flags);

        [DllImport("user32.dll")]
        private static extern nint GetKeyboardLayout(uint idThread);

        [DllImport("user32.dll")]
        private static extern int GetKeyboardLayoutList(int nBuff, nint[] lpList);

        [DllImport("user32.dll")]
        private static extern nint LoadKeyboardLayout(string pwszKLID, uint Flags);

        public static nint CurrentLayout => GetKeyboardLayout(0);

        private static Dictionary<ushort, KeyboardLayoutInfo>? allLayouts;

        public static Dictionary<ushort, KeyboardLayoutInfo> GetAllKeyboardLayouts()
        {
            if (allLayouts != null) return allLayouts;

            allLayouts = new Dictionary<ushort, KeyboardLayoutInfo>();
            
            var layoutCount = GetKeyboardLayoutList(0, null);
            if (layoutCount == 0) 
                return allLayouts;

            var layouts     = new nint[layoutCount];
            var actualCount = GetKeyboardLayoutList(layoutCount, layouts);
            if (actualCount == 0) 
                return allLayouts;

            foreach (var layout in layouts)
            {
                var langID     = (ushort)(layout.ToInt64() & 0xFFFF);
                var name       = GetLayoutDisplayName(langID);
                var layoutInfo = new KeyboardLayoutInfo { Handle = layout, Name = name, LangID = langID };
                allLayouts[langID] = layoutInfo;
            }

            return allLayouts;
        }

        private static string GetLayoutDisplayName(ushort langID)
        {
            try
            {
                var culture = new CultureInfo(langID);
                return culture.DisplayName;
            }
            catch { return string.Format($"0x{langID:X4}"); }
        }

        public static void SwitchToLayout(nint layoutHandle)
        {
            try
            {
                if (CurrentLayout == layoutHandle) return;

                ActivateKeyboardLayout(layoutHandle, 0);
            }
            catch
            {
                // ignored
            }
        }

        public static nint FindKeyboardLayout(ushort langID)
        {
            var layoutCount = GetKeyboardLayoutList(0, null);
            if (layoutCount == 0) return nint.Zero;

            var layouts     = new nint[layoutCount];
            var actualCount = GetKeyboardLayoutList(layoutCount, layouts);
            if (actualCount == 0) return nint.Zero;

            foreach (var layout in layouts)
            {
                var layoutLangID = (ushort)(layout.ToInt64() & 0xFFFF);
                if (layoutLangID == langID)
                    return layout;
            }

            var klid = $"{langID:X8}";
            return LoadKeyboardLayout(klid, 0x00000001);
        }
    }

    private class Config : ModuleConfiguration
    {
        public ushort FocusLayoutLangID;   // 聚焦时的布局语言ID
        public ushort UnfocusLayoutLangID; // 失焦时的布局语言ID
    }

    private struct KeyboardLayoutInfo
    {
        public nint   Handle;
        public string Name;
        public ushort LangID;
    }
}
