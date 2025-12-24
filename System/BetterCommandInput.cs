using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using DailyRoutines.Abstracts;
using Lumina.Text.ReadOnly;

namespace DailyRoutines.ModulesPublic;

public partial class BetterCommandInput : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("BetterCommandInputTitle"),
        Description = GetLoc("BetterCommandInputDescription"),
        Category    = ModuleCategories.System,
    };
    
    private static DateTime LastChatTime = DateTime.MinValue;
    
    private static Config ModuleConfig = null!;

    protected override void Init()
    {
        ModuleConfig = LoadConfig<Config>() ?? new();

        ChatManager.RegPreExecuteCommandInner(OnPreExecuteCommandInner);
    }

    protected override void Uninit() => 
        ChatManager.Unreg(OnPreExecuteCommandInner);

    protected override void ConfigUI()
    {
        if(ImGui.Checkbox(GetLoc("BetterCommandInput-DeleteSpaceBeforeCommand"), ref ModuleConfig.IsAvoidingSpace))
            ModuleConfig.Save(this);

        ImGui.Spacing();

        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), GetLoc("Whitelist"));

        ImGui.SameLine();
        if (ImGuiOm.ButtonIconWithText(FontAwesomeIcon.Plus, GetLoc("Add")))
        {
            ModuleConfig.Whitelist.Add(string.Empty);
            ModuleConfig.Save(this);
        }

        ImGui.Spacing();

        for (var i = 0; i < ModuleConfig.Whitelist.Count; i++)
        {
            var       whiteListCommand = ModuleConfig.Whitelist[i];
            var       input            = whiteListCommand;

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
    
    private static void OnPreExecuteCommandInner(ref bool isPrevented, ref ReadOnlySeString message)
    {
        var messageDecode          = message.ToString();
        var isMatchRegex           = CommandRegex().IsMatch(messageDecode);
        var isStartWithSlash       = messageDecode.StartsWith('/') || messageDecode.StartsWith('／');
        var shouldMessageBeHandled = ModuleConfig.IsAvoidingSpace ? isMatchRegex : isStartWithSlash;
        
        if (string.IsNullOrWhiteSpace(messageDecode) || !shouldMessageBeHandled)
            return;

        if (HandleSlashCommand(messageDecode, out var handledMessage))
            message = new(handledMessage);
    }
    
    private static bool HandleSlashCommand(string command, out string handledMessage)
    {
        handledMessage = string.Empty;
        
        if (!IsValid(command)) return false;
        if (ModuleConfig.IsAvoidingSpace)
            command = command.TrimStart(' ', '　');

        var spaceIndex = command.IndexOf(' ');
        if (spaceIndex == -1)
        {
            var lower = command.ToLowerAndHalfWidth();
            foreach (var whiteListCommand in ModuleConfig.Whitelist)
            {
                if (lower.Equals(whiteListCommand, StringComparison.CurrentCultureIgnoreCase)) 
                    lower = whiteListCommand;
            }
            
            handledMessage = lower;
        }
        else
        {
            var lower = command[..spaceIndex].ToLowerAndHalfWidth();
            foreach (var whiteListCommand in ModuleConfig.Whitelist)
            {
                if (lower.Equals(whiteListCommand, StringComparison.CurrentCultureIgnoreCase)) 
                    lower = whiteListCommand;
            }
            
            handledMessage = $"{lower}{command[spaceIndex..]}";
        }

        LastChatTime = DateTime.Now;
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsValid(ReadOnlySpan<char> chars) =>
        (DateTime.Now - LastChatTime).TotalMilliseconds >= 500f && 
        (ContainsUppercase(chars) || ContainsFullWidth(chars) || ContainsSpace(chars));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool ContainsUppercase(ReadOnlySpan<char> chars)
    {
        foreach (var c in chars)
        {
            if (char.IsUpper(c)) 
                return true;
        }

        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool ContainsSpace(ReadOnlySpan<char> chars)
    {
        foreach (var c in chars)
        {
            if (c is ' ' or '　') 
                return true;
        }

        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool ContainsFullWidth(ReadOnlySpan<char> chars)
    {
        foreach (var c in chars)
        {
            if (c.IsFullWidth()) 
                return true;
        }

        return false;
    }

    public class Config : ModuleConfiguration
    {
        public bool         IsAvoidingSpace = true;
        public List<string> Whitelist       = [];
    }

    [GeneratedRegex("^[ 　]*[/／]")]
    private static partial Regex CommandRegex();
}
