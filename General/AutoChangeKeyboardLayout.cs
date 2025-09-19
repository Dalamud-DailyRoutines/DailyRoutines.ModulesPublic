using System;
using System.Runtime.InteropServices;
using DailyRoutines.Abstracts;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace DailyRoutines.ModulesPublic;

public partial class AutoChangeKeyboardLayout : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("AutoChangeKeyboardLayoutTitle"),
        Description = GetLoc("AutoChangeKeyboardLayoutDescription"),
        Category    = ModuleCategories.General,
        Author      = ["JiaXX"]
    };

    protected override void Init() => DService.AddonLifecycle.RegisterListener(AddonEvent.PostDraw, "ChatLog", OnChatLogDraw);

    private static void OnChatLogDraw(AddonEvent evt, AddonArgs args)
    {
        if (ChatLogFocusDetector.IsChatLogFocused())
        {
            InputMethodController.SwitchToChinese();
            return;
        }

        InputMethodController.SwitchToEnglish();
    }

    protected override void Uninit() => DService.AddonLifecycle.UnregisterListener(OnChatLogDraw);

    public static partial class InputMethodController
    {
        [LibraryImport("user32.dll", EntryPoint = "ActivateKeyboardLayout")]
        private static partial void ActivateKeyboardLayout(nint hkl, uint Flags);

        [LibraryImport("user32.dll", EntryPoint = "GetKeyboardLayout")]
        private static partial nint GetKeyboardLayout(uint idThread);

        public static nint currentLayout => GetKeyboardLayout(0);

        public static void SwitchToEnglish()
        {
            try
            {
                var englishLayout = new nint(0x4090409);
                if (currentLayout == englishLayout) return;
                ActivateKeyboardLayout(englishLayout, 0);
            }
            catch (Exception)
            {
                // ignored
            }
        }

        public static void SwitchToChinese()
        {
            try
            {
                var chineseLayout = new nint(0x8040804);
                if (currentLayout == chineseLayout) return;
                ActivateKeyboardLayout(chineseLayout, 0);
            }
            catch (Exception)
            {
                // ignored
            }
        }
    }

    public static unsafe class ChatLogFocusDetector
    {
        public static bool IsChatLogFocused()
        {
            try
            {
                return IsChatLogTextInputFocused() || IsChatLogLikelyFocused();
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static bool IsChatLogTextInputFocused()
        {
            try
            {
                var raptureAtkModule = RaptureAtkModule.Instance();
                if (raptureAtkModule == null)
                    return false;

                var textInputEventInterface = raptureAtkModule->TextInput.TargetTextInputEventInterface;
                if (textInputEventInterface == null)
                    return false;

                var ownerNode = textInputEventInterface->GetOwnerNode();
                if (ownerNode == null || ownerNode->GetNodeType() != NodeType.Component)
                    return false;

                var componentNode = (AtkComponentNode*)ownerNode;
                var componentBase = componentNode->Component;
                if (componentBase == null || componentBase->GetComponentType() != ComponentType.TextInput)
                    return false;

                var componentTextInput = (AtkComponentTextInput*)componentBase;
                var addon = componentTextInput->ContainingAddon;
                if (addon == null)
                    addon = componentTextInput->ContainingAddon2;
                if (addon == null)
                {
                    addon = RaptureAtkUnitManager.Instance()->GetAddonByNode(
                        (AtkResNode*)componentTextInput->OwnerNode);
                }
                return addon != null && addon->NameString == "ChatLog";
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static bool IsChatLogLikelyFocused()
        {
            try
            {
                var raptureAtkModule = RaptureAtkModule.Instance();
                if (raptureAtkModule == null)
                    return false;
                return raptureAtkModule->TextInput.TargetTextInputEventInterface != null;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
