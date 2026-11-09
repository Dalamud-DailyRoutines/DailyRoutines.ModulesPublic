using System.Globalization;
using System.Text;
using DailyRoutines.Common.Extensions;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using Dalamud.Game.Config;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Data;
using OmenTools.Interop.Game.Lumina;
using AgentHUD = FFXIVClientStructs.Expansion.FFXIV.Client.UI.Agent.AgentHUD;

namespace DailyRoutines.ModulesPublic;

internal unsafe class OptimizedDTRTime : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("OptimizedDTRTimeTitle"),
        Description = Lang.Get("OptimizedDTRTimeDescription"),
        Category    = ModuleCategory.Interface,
        PreviewImageURL =
        [
            "https://gh.atmoomen.top/raw.githubusercontent.com/Dalamud-DailyRoutines/DailyRoutines/main/Resources/Modules/OptimizedDTRTime/preview-1.webp"
        ]
    };

    public override string ModuleName => nameof(OptimizedDTRTime);

    public override ModuleBehaviour Behaviour { get; } = new()
    {
        AllDefaultEnabled = true
    };

    private Config config = null!;

    private Hook<AgentHUD.Delegates.UpdateDTRTimeText> UpdateDTRTimeTextHook = null!;

    protected override void Init()
    {
        config = LoadConfig<Config>() ?? new();

        UpdateDTRTimeTextHook = IGameInteropProvider.Instance().HookFromMemberFunction
        (
            typeof(AgentHUD.MemberFunctionPointers),
            nameof(AgentHUD.UpdateDTRTimeText),
            (AgentHUD.Delegates.UpdateDTRTimeText)UpdateDTRTimeTextDetour
        );
        UpdateDTRTimeTextHook.Enable();
    }

    protected override void ConfigUI()
    {
        using (ImRaii.Heading1(Lang.Get("OptimizedDTRTime-ShowSeconds")))
        {
            if (ImGui.Checkbox(LuminaWrapper.GetAddonText(1129), ref config.ShowEorzeaSeconds))
                SaveConfig(config);

            if (ImGui.Checkbox(LuminaWrapper.GetAddonText(1127), ref config.ShowLocalSeconds))
                SaveConfig(config);

            if (ImGui.Checkbox(LuminaWrapper.GetAddonText(1128), ref config.ShowServerSeconds))
                SaveConfig(config);
        }
    }

    private void UpdateDTRTimeTextDetour
    (
        AgentHUD*        agent,
        StringArrayData* stringArray,
        int              displayFlags
    )
    {
        UpdateDTRTimeTextHook.Original(agent, stringArray, displayFlags);

        if (displayFlags == 0 || !IClientState.Instance().IsLoggedIn)
            return;

        var template   = stringArray->StringArray[TIME_TEXT_INDEX].ToString();
        var serverTime = DateTimeOffset.FromUnixTimeSeconds(Framework.GetServerTime());
        var localTime  = serverTime.ToLocalTime();
        var timeFormat = IGameConfig.Instance().UiControl.GetUInt(nameof(UiControlOption.InfoSettingDispType));
        var use12Hours = timeFormat == 2 || (timeFormat == 0 && GameState.ClientLanguage == Language.English);
        var position   = 0;

        var text = new StringBuilder(template.Length);

        for (var clockFlag = EORZEA_TIME_FLAG; clockFlag <= SERVER_TIME_FLAG; clockFlag <<= 1)
        {
            if ((displayFlags & clockFlag) == 0)
                continue;

            var colonIndex = template.IndexOf(':', position);
            if (colonIndex < 0)
                return;

            var start = colonIndex;
            while (start > position && char.IsAsciiDigit(template[start - 1]))
                start--;

            var end = colonIndex + 1;
            while (end < template.Length && char.IsAsciiDigit(template[end]))
                end++;

            if (clockFlag == EORZEA_TIME_FLAG)
                text.Append(template, position, end - position);
            else
            {
                var time = clockFlag == LOCAL_TIME_FLAG ?
                               localTime :
                               serverTime;
                var hour = time.Hour;
                if (use12Hours)
                    hour = hour % 12 == 0 ?
                               12 :
                               hour % 12;

                text.Append(template,                     position, start - position);
                text.Append(CultureInfo.InvariantCulture, $"{hour}:{time.Minute:00}");
            }

            var showSeconds = clockFlag switch
            {
                EORZEA_TIME_FLAG => config.ShowEorzeaSeconds,
                LOCAL_TIME_FLAG  => config.ShowLocalSeconds,
                _                => config.ShowServerSeconds
            };

            if (showSeconds)
            {
                var second = clockFlag == EORZEA_TIME_FLAG ?
                                 Framework.Instance()->ClientTime.EorzeaTime % 60 :
                                 serverTime.Second;
                text.Append(CultureInfo.InvariantCulture, $":{second:00}");
            }

            position = end;
        }

        text.Append(template, position, template.Length - position);

        using var timeText = new Utf8String(text.ToString());
        stringArray->SetValueAndUpdate(TIME_TEXT_INDEX, timeText.StringPtr, false);
    }

    private class Config : ModuleConfig
    {
        public bool ShowEorzeaSeconds;
        public bool ShowLocalSeconds  = true;
        public bool ShowServerSeconds = true;
    }

    #region 常量

    private const int TIME_TEXT_INDEX  = 2;
    private const int EORZEA_TIME_FLAG = 2;
    private const int LOCAL_TIME_FLAG  = 4;
    private const int SERVER_TIME_FLAG = 8;

    #endregion
}
