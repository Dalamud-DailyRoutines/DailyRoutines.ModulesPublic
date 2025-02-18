using DailyRoutines.Abstracts;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Hooking;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.Shell;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace DailyRoutines.Modules;

public unsafe class BetterCommandInput : DailyModuleBase
{
    public override ModuleInfo Info => new()
    {
        Title = GetLoc("BetterCommandInputTitle"),
        Description = GetLoc("BetterCommandInputDescription"),
        Category = ModuleCategories.System,
    };

    private static readonly CompSig ProcessSendedChatSig = new("E8 ?? ?? ?? ?? FE 86 ?? ?? ?? ?? C7 86 ?? ?? ?? ?? ?? ?? ?? ??");
    private delegate void ProcessSendedChatDelegate(ShellCommandModule* module, Utf8String* message, UIModule* uiModule);
    private static   Hook<ProcessSendedChatDelegate>? ProcessSendedChatHook;
    
    private static DateTime _lastChatTime = DateTime.MinValue;

    private static ShellCommandModule* shellCommandModule;

    private static Config ModuleConfig = null!;

    public override void Init()
    {
        ModuleConfig = new Config().Load(this);
        ProcessSendedChatHook ??=
            DService.Hook.HookFromSignature<ProcessSendedChatDelegate>(ProcessSendedChatSig.Get(), ProcessSendedChatDetour);
        
        ProcessSendedChatHook.Enable();
    }

    public override void ConfigUI()
    {
        if(ImGui.Checkbox(GetLoc("BetterCommandInput-DeleteSpaceBeforeCommand"), ref ModuleConfig.IsAvoidingSpace))
            ModuleConfig.Save(this);

        ImGui.Spacing();

        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(LightSkyBlue, GetLoc("Whitelist"));

        ImGui.SameLine();
        if (ImGuiOm.ButtonIconWithText(FontAwesomeIcon.Plus, GetLoc("Add")))
        {
            ModuleConfig.Whitelist.Add(string.Empty);
            ModuleConfig.Save(this);
        }

        ImGui.Spacing();

        for (var i = 0; i < ModuleConfig.Whitelist.Count; i++)
        {
            var whiteListCommand = ModuleConfig.Whitelist[i];
            var input = whiteListCommand;
            using var id = ImRaii.PushId($"{whiteListCommand}_{i}_Command");

            ImGui.AlignTextToFramePadding();
            ImGui.InputText($"###Command{whiteListCommand}-{i}", ref input, 48);

            if (ImGui.IsItemDeactivatedAfterEdit())
            { 
                ModuleConfig.Whitelist[i] = input;
                ModuleConfig.Save(this);
            }

            ImGui.SameLine();
            if (ImGuiOm.ButtonIcon("Delete", FontAwesomeIcon.TrashAlt, $"{GetLoc("Delete")}"))
            {
                ModuleConfig.Whitelist.Remove(whiteListCommand);
                ModuleConfig.Save(this);
            }
        }
    }

    private static void ProcessSendedChatDetour(ShellCommandModule* module, Utf8String* message, UIModule* uiModule)
    {
        shellCommandModule = module;
        
        var messageDecode = message->ExtractText();
        const string regex = @"^[ 　]*[/／]";
        var isMatchRegex = Regex.IsMatch(messageDecode, regex);
        var isStartWithSlash = messageDecode.StartsWith('/') || messageDecode.StartsWith('／');
        var shouldMessageBeHandled = ModuleConfig.IsAvoidingSpace? isMatchRegex : isStartWithSlash;
        if (string.IsNullOrWhiteSpace(messageDecode) || !shouldMessageBeHandled)
        {
            ProcessSendedChatHook.Original(module, message, uiModule);
            return;
        }

        if (HandleSlashCommand(messageDecode, out var handledMessage))
        {
            var stringFinal = Utf8String.FromSequence(new SeStringBuilder().Append(handledMessage).Build().Encode());
            ProcessSendedChatHook.Original(module, stringFinal, uiModule);
            stringFinal->Dtor(true);
            return;
        }
        
        ProcessSendedChatHook.Original(module, message, uiModule);
    }

    private static bool HandleSlashCommand(string command, out string handledMessage)
    {
        handledMessage = string.Empty;
        if (shellCommandModule == null || !IsValid(command)) return false;
        if (ModuleConfig.IsAvoidingSpace)
        {
            command = command.TrimStart(' ', '　');
        }

        var spaceIndex = command.IndexOf(' ');
        if (spaceIndex == -1)
        {
            var lower = command.ToLowerAndHalfWidth();
            foreach (var whiteListCommand in ModuleConfig.Whitelist)
            {
                if (lower.Equals(whiteListCommand, StringComparison.CurrentCultureIgnoreCase)) 
                    lower = whiteListCommand;
            }
            var str = Utf8String.FromString(lower);
            ProcessSendedChatHook.Original(shellCommandModule, str, UIModule.Instance());
            str->Dtor(true);
        }
        else
        {
            var lower = command[..spaceIndex].ToLowerAndHalfWidth();
            foreach (var whiteListCommand in ModuleConfig.Whitelist)
            {
                if (lower.Equals(whiteListCommand, StringComparison.CurrentCultureIgnoreCase)) 
                    lower = whiteListCommand;
            }
            var str = Utf8String.FromString($"{lower}{command[spaceIndex..]}");
            ProcessSendedChatHook.Original(shellCommandModule, str, UIModule.Instance());
            str->Dtor(true);
        }

        _lastChatTime = DateTime.Now;
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsValid(ReadOnlySpan<char> chars) =>
        (DateTime.Now - _lastChatTime).TotalMilliseconds >= 500f && 
        (ContainsUppercase(chars) || ContainsFullWidth(chars) || ContainsSpace(chars));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool ContainsUppercase(ReadOnlySpan<char> chars)
    {
        foreach (var c in chars)
            if (char.IsUpper(c)) return true;
        
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool ContainsSpace(ReadOnlySpan<char> chars)
    {
        foreach (var c in chars)
            if (c is ' ' or '　') return true;

        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool ContainsFullWidth(ReadOnlySpan<char> chars)
    {
        foreach (var c in chars)
            if (c.IsFullWidth()) return true;

        return false;
    }

    public class Config : ModuleConfiguration
    {
        public bool IsAvoidingSpace = true;
        public List<string> Whitelist = [];
    }
}
