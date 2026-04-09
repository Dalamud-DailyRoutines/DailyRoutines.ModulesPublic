using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using Lumina.Text.ReadOnly;
using OmenTools.OmenService;

namespace DailyRoutines.ModulesPublic;

public partial class BetterCommandInput : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("BetterCommandInputTitle"),
        Description = Lang.Get("BetterCommandInputDescription"),
        Category    = ModuleCategory.System
    };
    
    private Config config = null!;
    
    private DateTime lastChatTime = DateTime.MinValue;
    
    protected override void Init()
    {
        config = Config.Load(this) ?? new();

        ChatManager.Instance().RegPreExecuteCommandInner(OnPreExecuteCommandInner);
    }

    protected override void Uninit() =>
        ChatManager.Instance().Unreg(OnPreExecuteCommandInner);

    protected override void ConfigUI()
    {
        if (ImGui.Checkbox(Lang.Get("BetterCommandInput-DeleteSpaceBeforeCommand"), ref config.IsAvoidingSpace))
            config.Save(this);

        ImGui.Spacing();

        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), Lang.Get("Whitelist"));

        ImGui.SameLine();

        if (ImGuiOm.ButtonIconWithText(FontAwesomeIcon.Plus, Lang.Get("Add")))
        {
            config.Whitelist.Add(string.Empty);
            config.Save(this);
        }

        ImGui.Spacing();

        for (var i = 0; i < config.Whitelist.Count; i++)
        {
            var whiteListCommand = config.Whitelist[i];
            var input            = whiteListCommand;

            using var id = ImRaii.PushId($"{whiteListCommand}_{i}_Command");

            ImGui.AlignTextToFramePadding();
            ImGui.InputText($"###Command{whiteListCommand}-{i}", ref input, 48);

            if (ImGui.IsItemDeactivatedAfterEdit())
            {
                config.Whitelist[i] = input;
                config.Save(this);
            }

            ImGui.SameLine();

            if (ImGuiOm.ButtonIcon("Delete", FontAwesomeIcon.TrashAlt, $"{Lang.Get("Delete")}"))
            {
                config.Whitelist.Remove(whiteListCommand);
                config.Save(this);
            }
        }
    }

    private void OnPreExecuteCommandInner(ref bool isPrevented, ref ReadOnlySeString message)
    {
        var messageDecode          = message.ToString();
        var isMatchRegex           = CommandRegex().IsMatch(messageDecode);
        var isStartWithSlash       = messageDecode.StartsWith('/') || messageDecode.StartsWith('／');
        var shouldMessageBeHandled = config.IsAvoidingSpace ? isMatchRegex : isStartWithSlash;

        if (string.IsNullOrWhiteSpace(messageDecode) || !shouldMessageBeHandled)
            return;

        if (HandleSlashCommand(messageDecode, out var handledMessage))
            message = new(handledMessage);
    }

    private bool HandleSlashCommand(string command, out string handledMessage)
    {
        handledMessage = string.Empty;

        if (!IsValid(command)) return false;
        if (config.IsAvoidingSpace)
            command = command.TrimStart(' ', '　');

        var spaceIndex = command.IndexOf(' ');

        if (spaceIndex == -1)
        {
            var lower = command.ToLowerAndHalfWidth();

            foreach (var whiteListCommand in config.Whitelist)
            {
                if (lower.Equals(whiteListCommand, StringComparison.CurrentCultureIgnoreCase))
                    lower = whiteListCommand;
            }

            handledMessage = lower;
        }
        else
        {
            var lower = command[..spaceIndex].ToLowerAndHalfWidth();

            foreach (var whiteListCommand in config.Whitelist)
            {
                if (lower.Equals(whiteListCommand, StringComparison.CurrentCultureIgnoreCase))
                    lower = whiteListCommand;
            }

            handledMessage = $"{lower}{command[spaceIndex..]}";
        }

        lastChatTime = StandardTimeManager.Instance().Now;
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool IsValid(ReadOnlySpan<char> chars) =>
        (StandardTimeManager.Instance().Now - lastChatTime).TotalMilliseconds >= 500f &&
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

    [GeneratedRegex("^[ 　]*[/／]")]
    private static partial Regex CommandRegex();

    public class Config : ModuleConfig
    {
        public bool         IsAvoidingSpace = true;
        public List<string> Whitelist       = [];
    }
}
