using System.Collections.Generic;
using System.Linq;
using DailyRoutines.Abstracts;
using DailyRoutines.Infos;
using DailyRoutines.Managers;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel.Sheets;

namespace DailyRoutines.ModulesPublic;

public class AutoNotifyDiademWeather : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("AutoNotifyDiademWeatherTitle"),
        Description = GetLoc("AutoNotifyDiademWeatherDescription"),
        Category    = ModuleCategories.Notice
    };

    private static readonly List<uint> SpecialWeathers = [133, 134, 135, 136];
    
    private static Config ModuleConfig = null!;

    private static uint LastWeather;

    protected override void Init()
    {
        ModuleConfig = LoadConfig<Config>() ?? new();
        
        DService.ClientState.TerritoryChanged += OnZoneChanged;
        OnZoneChanged(DService.ClientState.TerritoryType);
    }

    protected override void ConfigUI()
    {
        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), LuminaWrapper.GetAddonText(8555));
        
        var weathers = string.Join(',',
                                   ModuleConfig.Weathers
                                               .Select(x => LuminaGetter.GetRow<Weather>(x)?.Name.ExtractText() ?? string.Empty)
                                               .Distinct());
        using var combo = ImRaii.Combo("###WeathersCombo", weathers, ImGuiComboFlags.HeightLarge);
        if (combo)
        {
            foreach (var weather in SpecialWeathers)
            {
                if (!LuminaGetter.TryGetRow<Weather>(weather, out var data)) continue;
                if (!DService.Texture.TryGetFromGameIcon(new((uint)data.Icon), out var icon)) continue;

                if (ImGuiOm.SelectableImageWithText(icon.GetWrapOrEmpty().Handle,
                                                    new(ImGui.GetTextLineHeightWithSpacing()), $"{data.Name.ExtractText()}",
                                                    ModuleConfig.Weathers.Contains(weather),
                                                    ImGuiSelectableFlags.DontClosePopups))
                {
                    if (!ModuleConfig.Weathers.Add(weather))
                        ModuleConfig.Weathers.Remove(weather);

                    SaveConfig(ModuleConfig);
                }
            }
        }
    }

    private static void OnZoneChanged(ushort zone)
    {
        FrameworkManager.Unregister(OnUpdate);
        
        zone = (ushort)GameState.TerritoryType;
        if (zone != 939) return;

        FrameworkManager.Register(OnUpdate, throttleMS: 10_000);
    }

    private static unsafe void OnUpdate(IFramework framework)
    {
        if (GameState.TerritoryType != 939)
        {
            FrameworkManager.Unregister(OnUpdate);
            return;
        }
        
        var weatherID = WeatherManager.Instance()->GetCurrentWeather();
        if (LastWeather == weatherID || !LuminaGetter.TryGetRow<Weather>(weatherID, out var weather)) return;
        
        LastWeather = weatherID;
        if (!ModuleConfig.Weathers.Contains(weatherID)) return;

        var message = GetLoc("AutoNotifyDiademWeather-Notification", weather.Name.ExtractText());
        Chat(message);
        NotificationInfo(message);
    }

    protected override void Uninit()
    {
        DService.ClientState.TerritoryChanged -= OnZoneChanged;
        FrameworkManager.Unregister(OnUpdate);

        LastWeather = 0;
    }

    private class Config : ModuleConfiguration
    {
        public HashSet<uint> Weathers = [];
    }
}
