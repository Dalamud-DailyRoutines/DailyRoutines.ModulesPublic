using System.Collections.Frozen;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using Dalamud.Plugin.Services;
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
        Category    = ModuleCategory.Notice
    };
    
    private Config config = null!;

    private uint lastWeather;

    protected override void Init()
    {
        config = Config.Load(this) ?? new();

        DService.Instance().ClientState.TerritoryChanged += OnZoneChanged;
        OnZoneChanged(0);
    }

    protected override void Uninit()
    {
        DService.Instance().ClientState.TerritoryChanged -= OnZoneChanged;
        FrameworkManager.Instance().Unreg(OnUpdate);

        lastWeather = 0;
    }

    protected override void ConfigUI()
    {
        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), LuminaWrapper.GetAddonText(8555));

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
                if (!DService.Instance().Texture.TryGetFromGameIcon(new((uint)data.Icon), out var icon)) continue;

                if (ImGuiOm.SelectableImageWithText
                    (
                        icon.GetWrapOrEmpty().Handle,
                        new(ImGui.GetTextLineHeightWithSpacing()),
                        $"{data.Name.ToString()}",
                        config.Weathers.Contains(weather),
                        ImGuiSelectableFlags.DontClosePopups
                    ))
                {
                    if (!config.Weathers.Add(weather))
                        config.Weathers.Remove(weather);

                    config.Save(this);
                }
            }
        }
    }

    private void OnZoneChanged(uint u)
    {
        FrameworkManager.Instance().Unreg(OnUpdate);

        if (GameState.TerritoryType != 939) return;

        FrameworkManager.Instance().Reg(OnUpdate, 10_000);
    }

    private unsafe void OnUpdate(IFramework framework)
    {
        if (GameState.TerritoryType != 939)
        {
            FrameworkManager.Instance().Unreg(OnUpdate);
            return;
        }

        var weatherID = WeatherManager.Instance()->GetCurrentWeather();
        if (lastWeather == weatherID || !LuminaGetter.TryGetRow<Weather>(weatherID, out var weather)) return;

        lastWeather = weatherID;
        if (!config.Weathers.Contains(weatherID)) return;

        var message = Lang.Get("AutoNotifyDiademWeather-Notification", weather.Name.ToString());
        NotifyHelper.Instance().Chat(message);
        NotifyHelper.Instance().NotificationInfo(message);
    }

    private class Config : ModuleConfig
    {
        public HashSet<uint> Weathers = [];
    }
    
    #region 常量

    private static readonly FrozenSet<uint> SpecialWeathers = [133, 134, 135, 136];

    #endregion
}
