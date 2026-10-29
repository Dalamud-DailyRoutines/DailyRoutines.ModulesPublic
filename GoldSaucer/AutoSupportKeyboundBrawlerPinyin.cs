using System.Globalization;
using System.Text;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using Dalamud.Hooking;
using FFXIVClientStructs.Expansion.FFXIV.Client.UI;
using FFXIVClientStructs.Expansion.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using InteropGenerator.Runtime;
using Lumina.Excel.Sheets;
using OmenTools.Interop.Game.Lumina;
using TinyPinyin;

namespace DailyRoutines.ModulesPublic;

public unsafe class AutoSupportKeyboundBrawlerPinyin : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = "自动支持魔光键影拼音输入",
        Description = "在副本“魔光键影”中，使输入判定支持拼音，且自动在词条上方显示对应的拼音。",
        Category    = ModuleCategory.GoldSaucer
    };

    public override ModulePermission Permission { get; } = new()
    {
        CNOnly           = true,
        CNDefaultEnabled = true
    };

    private Hook<AgentKTGInput.Delegates.SubmitWord>    SubmitWordHook;
    private Hook<AddonKTGEWord.Delegates.ApplyWordData> ApplyWordDataHook;

    private readonly Dictionary<string, byte[]> readings           = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> normalizedReadings = new(StringComparer.Ordinal);

    protected override void Init()
    {
        foreach (var row in LuminaGetter.Get<KTGTypeWordTextData>())
        {
            var word    = row.Unknown0.ExtractText();
            var reading = row.Unknown1.ExtractText();
            if (string.IsNullOrWhiteSpace(word))
                continue;

            if (string.IsNullOrWhiteSpace(reading) ||
                !reading.All(x => char.IsAsciiLetter(x) || char.IsWhiteSpace(x) || x == '\''))
            {
                reading = CultureInfo.CurrentCulture.TextInfo.ToTitleCase(PinyinHelper.GetPinyin(word).ToLowerInvariant())
                                     .Replace(" ", string.Empty);
            }

            readings[word]           = Encoding.UTF8.GetBytes(reading + '\0');
            normalizedReadings[word] = NormalizeReading(reading);
        }

        SubmitWordHook = IGameInteropProvider.Instance().HookFromMemberFunction
        (
            typeof(AgentKTGInput.MemberFunctionPointers),
            nameof(AgentKTGInput.SubmitWord),
            (AgentKTGInput.Delegates.SubmitWord)SubmitWordDetour
        );
        SubmitWordHook.Enable();

        ApplyWordDataHook = IGameInteropProvider.Instance().HookFromMemberFunction
        (
            typeof(AddonKTGEWord.MemberFunctionPointers),
            nameof(AddonKTGEWord.ApplyWordData),
            (AddonKTGEWord.Delegates.ApplyWordData)ApplyWordDataDetour
        );
        ApplyWordDataHook.Enable();
    }

    protected override void Uninit()
    {
        readings.Clear();
        normalizedReadings.Clear();
    }

    private bool SubmitWordDetour
    (
        AgentKTGInput* agent,
        AtkValue*      inputValue
    )
    {
        if (inputValue == null ||
            inputValue->Type is not (AtkValueType.String or AtkValueType.ManagedString))
            return SubmitWordHook.Original(agent, inputValue);

        var hud = agent->HudAgent;
        if (hud == null   ||
            !hud->IsShown ||
            !hud->HasDirector)
            return SubmitWordHook.Original(agent, inputValue);

        var input = NormalizeReading(inputValue->String.ToString());
        if (input.Length == 0)
            return SubmitWordHook.Original(agent, inputValue);

        foreach (ref var pair in hud->Entities)
        {
            ref var entry = ref pair.Item2;
            if (!entry.IsVisible     ||
                entry.Disposed       ||
                entry.Timestamp >= 0 ||
                entry.Name.IsEmpty)
                continue;

            var word = entry.Name.ToString();
            if (!normalizedReadings.TryGetValue(word, out var reading) ||
                !string.Equals(input, reading, StringComparison.Ordinal))
                continue;

            var translatedValue = new AtkValue();
            translatedValue.SetManagedString(word);

            try
            {
                return SubmitWordHook.Original(agent, &translatedValue);
            }
            finally
            {
                translatedValue.Dtor();
            }
        }

        return SubmitWordHook.Original(agent, inputValue);
    }

    private void ApplyWordDataDetour
    (
        AddonKTGEWord* addon,
        uint           index,
        uint           entityID,
        CStringPointer name,
        CStringPointer title,
        uint           state,
        uint           timelineState
    )
    {
        var        bytes = name.AsSpan();
        Span<char> word  = stackalloc char[Encoding.UTF8.GetCharCount(bytes)];
        Encoding.UTF8.GetChars(bytes, word);

        if (!readings.GetAlternateLookup<ReadOnlySpan<char>>().TryGetValue(word, out var reading))
        {
            ApplyWordDataHook.Original(addon, index, entityID, name, title, state, timelineState);
            return;
        }

        fixed (byte* text = reading)
            ApplyWordDataHook.Original(addon, index, entityID, name, text, state, timelineState);
    }

    private static string NormalizeReading
    (
        string value
    ) =>
        new string(value.Where(x => !char.IsWhiteSpace(x) && x != '\'').ToArray()).ToLowerInvariant();
}
