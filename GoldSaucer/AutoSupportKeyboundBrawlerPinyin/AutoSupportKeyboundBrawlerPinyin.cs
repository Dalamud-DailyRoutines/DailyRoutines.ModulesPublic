using System.Collections.Frozen;
using System.Globalization;
using System.Text;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using Dalamud.Hooking;
using FFXIVClientStructs.Expansion.FFXIV.Client.UI;
using FFXIVClientStructs.Expansion.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Component.GUI;
using FFXIVClientStructs.Interop;
using InteropGenerator.Runtime;
using Lumina.Excel.Sheets;
using OmenTools.Interop.Game.Lumina;
using TinyPinyin;

namespace DailyRoutines.ModulesPublic.AutoSupportKeyboundBrawlerPinyin;

public unsafe partial class AutoSupportKeyboundBrawlerPinyin : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = "自动支持魔光键影中文输入",
        Description = "在副本“魔光键影”中，使输入判定支持拼音、注音与三代仓颉，且自动在词条上方显示对应的输入码。",
        Category    = ModuleCategory.GoldSaucer,
        PreviewImageURL =
        [
            "https://gh.atmoomen.top/raw.githubusercontent.com/Dalamud-DailyRoutines/DailyRoutines/main/Resources/Modules/AutoSupportKeyboundBrawlerPinyin/preview-1.webp"
        ]
    };

    public override ModuleBehaviour Permission { get; } = new()
    {
        CNOnly           = true,
        CNDefaultEnabled = true
    };

    private Hook<AgentKTGInput.Delegates.SubmitWord>    SubmitWordHook;
    private Hook<AddonKTGEWord.Delegates.ApplyWordData> ApplyWordDataHook;

    private Config config = null!;

    private readonly Dictionary<string, string> readings           = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> normalizedReadings = new(StringComparer.Ordinal);

    protected override void Init()
    {
        config = LoadConfig<Config>() ?? new();

        RebuildReadings();

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
        cangjieReadings.Clear();
    }

    protected override void ConfigUI()
    {
        using var combo = ImRaii.Combo
        (
            "输入模式",
            INPUT_MODE_NAMES[(int)config.Mode]
        );
        if (!combo)
            return;

        foreach (var mode in Enum.GetValues<InputMode>())
        {
            if (!ImGui.Selectable(INPUT_MODE_NAMES[(int)mode], config.Mode == mode))
                continue;

            config.Mode = mode;
            config.Save(this);
            RebuildReadings();
        }
    }

    private void RebuildReadings()
    {
        readings.Clear();
        normalizedReadings.Clear();
        cangjieReadings.Clear();

        foreach (var row in LuminaGetter.Get<KTGTypeWordTextData>())
        {
            var word    = row.Unknown0.ExtractText();
            var reading = row.Unknown1.ExtractText();
            if (string.IsNullOrWhiteSpace(word)                                                ||
                word.Any(x => x is >= '\u3040' and <= '\u30FF' or >= '\uFF66' and <= '\uFF9F') ||
                reading.Any(x => x is >= '\u3040' and <= '\u30FF' or >= '\uFF66' and <= '\uFF9F'))
                continue;

            var text = new string
            (
                word.Where
                (x => !char.IsPunctuation(x) &&
                      !char.IsSymbol(x)      &&
                      !char.IsWhiteSpace(x)
                ).ToArray()
            );
            if (text.Length == 0)
                continue;

            if (config.Mode == InputMode.Cangjie)
            {
                var codes = text.Select
                (x => CANGJIE_READINGS.TryGetValue(x, out var values) ?
                          values :
                          [x.ToString().ToLowerInvariant()]
                ).ToArray();
                readings[word]        = string.Join(' ', codes.Select(x => x[0]));
                cangjieReadings[word] = codes;
                continue;
            }

            if (READING_OVERRIDES.TryGetValue(word, out var overriddenReading))
                reading = overriddenReading;
            else if (string.IsNullOrWhiteSpace(reading) ||
                     !reading.All
                     (x => char.IsAsciiLetter(x) ||
                           char.IsWhiteSpace(x)  ||
                           x == '\''
                     ))
                reading = string.Concat(text.Select(x => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(PinyinHelper.GetPinyin(x).ToLowerInvariant())));

            reading = new string
            (
                reading.Where
                (x => !char.IsPunctuation(x) &&
                      !char.IsSymbol(x)
                ).ToArray()
            );

            if (config.Mode is InputMode.Zhuyin or InputMode.ZhuyinKeyboard)
            {
                reading = ConvertToZhuyin(reading, config.Mode == InputMode.ZhuyinKeyboard);
                if (reading.Length == 0)
                    continue;
            }

            readings[word]           = reading;
            normalizedReadings[word] = NormalizeReading(reading);
        }
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

            if (config.Mode == InputMode.Cangjie)
            {
                if (!MatchesCangjieReading(word, input))
                    continue;
            }
            else if (!normalizedReadings.TryGetValue(word, out var reading) ||
                     !string.Equals(input, reading, StringComparison.Ordinal))
                continue;

            using var rented = new RentedAtkValues(1);
            rented[0].SetManagedString(word);
            return SubmitWordHook.Original(agent, rented);
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
        if (!readings.TryGetValue(name.ToString(), out var reading))
        {
            ApplyWordDataHook.Original(addon, index, entityID, name, title, state, timelineState);
            return;
        }

        using var str = new Utf8String(reading);
        ApplyWordDataHook.Original(addon, index, entityID, name, str.StringPtr, state, timelineState);
    }

    private string NormalizeReading
    (
        string value
    )
    {
        var normalized = new StringBuilder(value.Length);

        foreach (var character in value)
        {
            if (char.IsWhiteSpace(character) ||
                character == '\'')
                continue;

            if ((char.IsPunctuation(character) ||
                 char.IsSymbol(character)) &&
                (config.Mode != InputMode.ZhuyinKeyboard ||
                 !ZHUYIN_KEYS.Contains(character)))
                continue;

            var isTone = config.Mode switch
            {
                InputMode.Zhuyin         => character is 'ˉ' or 'ˊ' or 'ˇ' or 'ˋ' or '˙',
                InputMode.ZhuyinKeyboard => character is '3' or '4' or '6' or '7',
                _                        => false
            };
            if (isTone)
                continue;

            var radicalIndex = config.Mode == InputMode.Cangjie ?
                                   CANGJIE_RADICALS.IndexOf(character) :
                                   -1;
            normalized.Append
            (
                radicalIndex >= 0 ?
                    (char)('a' + radicalIndex) :
                    char.ToLowerInvariant(character)
            );
        }

        return normalized.ToString();
    }

    private enum InputMode
    {
        Pinyin,
        Zhuyin,
        ZhuyinKeyboard,
        Cangjie
    }

    private class Config : ModuleConfig
    {
        public InputMode Mode = InputMode.Pinyin;
    }

    #region 常量

    private static readonly string[] INPUT_MODE_NAMES = ["汉语拼音", "注音符号", "注音键位", "三代仓颉"];

    private static readonly FrozenDictionary<string, string> READING_OVERRIDES = new Dictionary<string, string>
    {
        ["第陆区"]           = "DiLiuQu",
        ["梣木"]            = "ChenMu",
        ["掷弹兵"]           = "ZhiDanBing",
        ["高级掷弹兵"]         = "GaoJiZhiDanBing",
        ["重装甲战斗员"]        = "ZhongZhuangJiaZhanDouYuan",
        ["燃烧弹"]           = "RanShaoDan",
        ["加农弹"]           = "JiaNongDan",
        ["辟邪护符"]          = "BiXieHuFu",
        ["强力辟邪护符"]        = "QiangLiBiXieHuFu",
        ["兀鹰杉木"]          = "WuYingShaMu",
        ["吉尔伽美什"]         = "JiErGaMeiShi",
        ["吉尔伽美什光束"]       = "JiErGaMeiShiGuangShu",
        ["天堂弹珠台"]         = "TianTangDanZhuTai",
        ["阳露出了笑容"]        = "YangLuChuLeXiaoRong",
        ["三重灾祸"]          = "SanChongZaiHuo",
        ["劲歌热舞吧"]         = "JingGeReWuBa",
        ["下次就推心置腹地聊聊吧"]   = "XiaCiJiuTuiXinZhiFuDeLiaoLiaoBa",
        ["得活着才能是塔克斯"]     = "DeiHuoZheCaiNengShiTaKeSi",
        ["玛琳·华勒斯"]        = "MaLinHuaLeiSi",
        ["巴雷特·华勒斯"]       = "BaLeiTeHuaLeiSi",
        ["爱丽丝·盖恩斯伯勒"]     = "AiLiSiGaiEnSiBoLei",
        ["华勒斯"]           = "HuaLeiSi",
        ["盖恩斯伯勒"]         = "GaiEnSiBoLei",
        ["忘却之都"]          = "WangQueZhiDu",
        ["我得狠狠揍你这家伙一下才行"] = "WoDeiHenHenZouNiZheJiaHuoYiXiaCaiXing",
        ["我想重拾我的人生"]      = "WoXiangChongShiWoDeRenSheng"
    }.ToFrozenDictionary(StringComparer.Ordinal);

    #endregion
}
