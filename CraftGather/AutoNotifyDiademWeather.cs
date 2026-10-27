using DailyRoutines.Common.Extensions;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel.Sheets;
using OmenTools.Interop.Game.Lumina;
using OmenTools.OmenService;

namespace DailyRoutines.ModulesPublic;

public class AutoNotifyDiademWeather : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoNotifyDiademWeatherTitle"),
        Description = Lang.Get("AutoNotifyDiademWeatherDescription"),
        Category    = ModuleCategory.CraftGather
    };

    private Config config = null!;

    private uint lastWeather;

    protected override void Init()
    {
        config = Config.Load(this) ?? new();

        IClientState.Instance().TerritoryChanged += OnZoneChanged;
        OnZoneChanged(0);
    }

    protected override void Uninit()
    {
        IClientState.Instance().TerritoryChanged -= OnZoneChanged;
        FrameworkManager.Instance().Unreg(OnUpdate);

        lastWeather = 0;
    }

    protected override void ConfigUI()
    {
        using var heading = ImRaii.Heading1(LuminaWrapper.GetAddonText(8555));
        
        var weathers = string.Join
        (
            ',',
            config.Weathers
                  .Select(x => LuminaGetter.GetRow<Weather>(x)?.Name.ToString() ?? string.Empty)
                  .Distinct()
        );
        using var combo = ImRaii.Combo("###WeathersCombo", weathers, ImGuiComboFlags.HeightLarge);

        if (combo)
        {
            foreach (var weather in SpecialWeathers)
            {
                if (!LuminaGetter.TryGetRow<Weather>(weather, out var data)) continue;
                if (!ITextureProvider.Instance().TryGetFromGameIcon(new((uint)data.Icon), out var icon)) continue;

                if (ImGuiOm.SelectableImageWithText
                    (
                        icon.GetWrapOrEmpty().Handle,
                        new(ImGui.GetTextLineHeightWithSpacing()),
                        $"{data.Name.ToString()}",
                        config.Weathers.Contains(weather),
                        ImGuiSelectableFlags.DontClosePopups
                    ))
                {
                    if (config.Weathers.Contains(weather))
                        config.Weathers.Remove(weather);
                    else
                        config.Weathers.Add(weather);

                    config.Save(this);
                }
            }
        }
    }

    private void OnZoneChanged
    (
        uint u
    )
    {
        FrameworkManager.Instance().Unreg(OnUpdate);

        if (GameState.TerritoryType != DIADEM_ZONE) return;

        FrameworkManager.Instance().Reg(OnUpdate, 10_000);
    }

    private unsafe void OnUpdate
    (
        IFramework framework
    )
    {
        if (GameState.TerritoryType != DIADEM_ZONE)
        {
            FrameworkManager.Instance().Unreg(OnUpdate);
            return;
        }

        var weatherID = WeatherManager.Instance()->GetCurrentWeather();
        if (lastWeather == weatherID ||
            !LuminaGetter.TryGetRow<Weather>(weatherID, out var weather))
            return;

        lastWeather = weatherID;
        if (!config.Weathers.Contains(weatherID)) return;

        var message = Lang.Get
        (
            "AutoNotifyDiademWeather-Notification",
            new Dictionary<string, object>
            {
                ["weather"] = weather.Name
            }
        );
        
        NotifyHelper.Toast(message);
        NotifyHelper.Instance().Chat(message, false);
        NotifyHelper.Instance().TrayInfo(message);
    }

    private class Config : ModuleConfig
    {
        public uint[] Weathers = [];
    }

    #region 常量

    private static readonly uint[] SpecialWeathers = [133, 134, 135, 136];

    private const uint DIADEM_ZONE = 939;

    #endregion
}
