using System.Text;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using DailyRoutines.Manager;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.Graphics.Environment;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit;
using KamiToolKit.Nodes;
using KamiToolKit.Premade.Node.Simple;
using Lumina.Data;
using Lumina.Excel.Sheets;
using OmenTools.Info.Game;
using OmenTools.Interop.Game;
using OmenTools.Interop.Game.Lumina;
using OmenTools.Interop.Game.Models;
using OmenTools.OmenService;

namespace DailyRoutines.ModulesPublic;

public unsafe class FastSetWeatherTime : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("FastSetWeatherTimeTitle"),
        Description = Lang.Get("FastSetWeatherTimeDescription", COMMAND),
        Category    = ModuleCategory.UIOptimization
    };

    public override ModulePermission Permission { get; } = new() { AllDefaultEnabled = true };
    
    private static readonly CompSig                        PlayWeatherSoundSig = new("48 89 5C 24 ?? 48 89 6C 24 ?? 56 57 41 56 48 83 EC ?? 45 33 F6 0F 29 74 24");
    private delegate        void*                          PlayWeatherSoundDelegate(void* manager, byte weatherID, void* a3, void* a4);
    private                 Hook<PlayWeatherSoundDelegate> PlayWeatherSoundHook;

    private static readonly CompSig                          UpdateBgmSituationSig = new("48 89 5C 24 ?? 57 48 83 EC 20 B8 ?? ?? ?? ?? 49 8B F9 41 8B D8");
    private delegate        void*                            UpdateBgmSituationDelegate(void* manager, ushort bgmSituationID, int column, void* a4, void* a5);
    private                 Hook<UpdateBgmSituationDelegate> UpdateBgmSituationHook;
    
    // mov eax, 0
    private readonly MemoryPatchWithPointer<uint> renderSunlightShadowPatch =
        new("49 0F BE 40 ?? 84 C0", [0xB8, 0x00, 0x00, 0x00, 0x00], pointerOffset: 1);

    // mov dl, 0, nop, nop
    private readonly MemoryPatchWithPointer<byte> renderWeatherPatch =
        new("48 89 5C 24 ?? 57 48 83 EC 30 80 B9 ?? ?? ?? ?? ?? 49 8B F8 0F 29 74 24 ?? 48 8B D9 0F 28 F1", [0xB2, 0x00, 0x90, 0x90], 0x55, 1);

    // mov r9, 0
    private readonly MemoryPatchWithPointer<uint> renderTimePatch =
        new("48 89 5C 24 ?? 57 48 83 EC 30 4C 8B 15", [0x49, 0xC7, 0xC1, 0x00, 0x00, 0x00, 0x00], 0x19, 3);

    private static uint RealTime
    {
        get
        {
            var date = EorzeaDate.GetTime();
            return (uint)(date.Second + 60 * date.Minute + 3600 * date.Hour);
        }
    }

    private static byte RealWeather =>
        *(byte*)((nint)EnvManager.Instance() + 0x26);

    private byte CustomWeather
    {
        get => renderWeatherPatch.CurrentValue;
        set => renderWeatherPatch.Set(value);
    }

    private uint CustomTime
    {
        get => renderTimePatch.CurrentValue;
        set => renderTimePatch.Set(value);
    }
    
    private Config config = null!;
    
    private TextButtonNode? openButton;

    protected override void Init()
    {
        config = Config.Load(this) ?? new();

        PlayWeatherSoundHook ??= PlayWeatherSoundSig.GetHook<PlayWeatherSoundDelegate>(PlayWeatherSoundDetour);
        PlayWeatherSoundHook.Enable();

        UpdateBgmSituationHook ??= UpdateBgmSituationSig.GetHook<UpdateBgmSituationDelegate>(UpdateBgmSituationDetour);
        UpdateBgmSituationHook.Enable();

        DService.Instance().ClientState.TerritoryChanged += OnZoneChanged;

        AddonDRFastSetWeather.Addon = new(this)
        {
            InternalName = "DRFastSetWeather",
            Title        = $"{Lang.Get("Weather")} & {Lang.Get("Time")}",
            Size         = new(254f, 50f)
        };

        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PostRequestedUpdate, "_NaviMap", OnAddon);
        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PreFinalize,         "_NaviMap", OnAddon);

        CommandManager.Instance().AddSubCommand(COMMAND, new(OnCommand) { HelpMessage = Lang.Get("FastSetWeatherTime-CommandHelp") });
    }

    protected override void Uninit()
    {
        DService.Instance().ClientState.TerritoryChanged -= OnZoneChanged;

        DService.Instance().AddonLifecycle.UnregisterListener(OnAddon);
        OnAddon(AddonEvent.PreFinalize, null);

        CommandManager.Instance().RemoveSubCommand(COMMAND);

        AddonDRFastSetWeather.Addon?.Dispose();
        AddonDRFastSetWeather.Addon = null;

        ToggleWeather(false);
        ToggleTime(false);
    }

    protected override void ConfigUI()
    {
        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), Lang.Get("FastSetWeatherTime-CommandHelp"));

        using (ImRaii.PushIndent())
        {
            ImGui.TextUnformatted($"1. /pdr {COMMAND}");

            if (ImageHelper.Instance().TryGetImage(NAVI_MAP_IMAGE_URL, out var image))
            {
                ImGui.TextUnformatted($"2. {Lang.Get("FastSetWeatherTime-OperationHelp-ClickNaviMap")}");
                ImGui.Image(image.Handle, image.Size);
            }
        }
    }
    
    #region 事件

    private void* PlayWeatherSoundDetour(void* manager, byte weatherID, void* a3, void* a4)
    {
        if (IsWeatherCustom())
            weatherID = GetDisplayWeather();

        return PlayWeatherSoundHook.Original(manager, weatherID, a3, a4);
    }

    private void* UpdateBgmSituationDetour(void* manager, ushort bgmSituationID, int column, void* a4, void* a5)
    {
        if (IsTimeCustom() && column != 3)
        {
            var seconds = CustomTime % 86400;
            var isDay   = seconds is >= 21600 and < 64800;
            column = isDay ? 1 : 2;
        }

        return UpdateBgmSituationHook.Original(manager, bgmSituationID, column, a4, a5);
    }

    private void OnZoneChanged(ushort _)
    {
        config.ZoneSettings.TryGetValue(GameState.TerritoryType, out var info);

        if (info is { IsWeatherEnabled: true, WeatherID: not 255 })
            ToggleWeather(true, info.WeatherID);
        else
            ToggleWeather(false);

        if (info is { IsTimeEnabled: true })
            ToggleTime(true, info.Time);
        else
            ToggleTime(false);
    }

    private void OnAddon(AddonEvent type, AddonArgs args)
    {
        switch (type)
        {
            case AddonEvent.PreFinalize:
                openButton?.Dispose();
                openButton = null;
                break;

            case AddonEvent.PostRequestedUpdate:
                if (!NaviMap->IsAddonAndNodesReady()) return;

                if (openButton == null)
                {
                    openButton = new()
                    {
                        Position  = new(158, 24),
                        Size      = new(36),
                        IsVisible = true,
                        String    = string.Empty,
                        OnClick   = () => AddonDRFastSetWeather.Addon.Toggle()
                    };

                    openButton.BackgroundNode.IsVisible = false;
                    openButton.AttachNode(NaviMap->RootNode);
                }

                openButton.TextTooltip = LuminaGetter.GetRowOrDefault<Weather>(GetDisplayWeather()).Name;
                break;
        }
    }

    private static void OnCommand(string command, string args) =>
        AddonDRFastSetWeather.Addon.Toggle();

    #endregion

    #region 控制

    private void ToggleWeather(bool isEnabled, byte weatherID = 255)
    {
        if (!isEnabled || weatherID == 255)
            DisableCustomWeather();
        else
        {
            EnableCustomWeather();
            CustomWeather = weatherID;
        }
    }

    private void EnableCustomWeather()
    {
        if (IsWeatherCustom()) return;

        renderWeatherPatch.Enable();
        renderSunlightShadowPatch.Enable();
    }

    private void DisableCustomWeather()
    {
        if (!IsWeatherCustom()) return;

        renderWeatherPatch.Disable();
        renderSunlightShadowPatch.Disable();
    }

    private void ToggleTime(bool isEnabled, uint time = 0)
    {
        if (!isEnabled)
            DisableCustomTime();
        else
        {
            EnableCustomTime();
            CustomTime = time;
        }
    }

    private void EnableCustomTime()
    {
        if (IsTimeCustom()) return;
        renderTimePatch.Enable();
    }

    private void DisableCustomTime()
    {
        if (!IsTimeCustom()) return;
        renderTimePatch.Disable();
    }

    #endregion

    #region 工具

    private byte GetDisplayWeather() =>
        IsWeatherCustom() ? CustomWeather : RealWeather;

    private uint GetDisplayTime() =>
        IsTimeCustom() ? CustomTime : RealTime;

    private bool IsWeatherCustom() =>
        renderWeatherPatch.IsEnabled;

    private bool IsTimeCustom() =>
        renderTimePatch.IsEnabled;

    private static (List<byte> WeatherList, string ENVBFile) ParseLVB(ushort zoneID)
    {
        var weathers = new List<byte>();

        try
        {
            var file = DService.Instance().Data.GetFile<LVBFile>($"bg/{LuminaGetter.GetRowOrDefault<TerritoryType>(zoneID).Bg}.lvb");
            if (file?.WeatherIDs == null || file.WeatherIDs.Length == 0)
                return ([], string.Empty);

            foreach (var weather in file.WeatherIDs)
            {
                if (weather is > 0 and < 255)
                    weathers.Add((byte)weather);
            }

            weathers.Sort();
            return (weathers, file.ENVBFile);
        }
        catch
        {
            // ignored
        }

        return ([], string.Empty);
    }

    #endregion

    private class Config : ModuleConfig
    {
        public Dictionary<uint, ZoneSetting> ZoneSettings = [];
    }

    private class AddonDRFastSetWeather(FastSetWeatherTime module) : NativeAddon
    {
        private TextButtonNode? clearButtonNode;

        private NumericInputNode? hourInputNode;
        private NumericInputNode? minuteInputNode;

        private TextButtonNode?   saveButtonNode;
        private NumericInputNode? secondInputNode;

        private SliderNode? timeNode;

        private       Dictionary<byte, (IconButtonNode IconButton, SimpleNineGridNode EnabledIcon)> weatherButtons = [];
        public static AddonDRFastSetWeather?                                                        Addon { get; set; }

        protected override void OnSetup(AtkUnitBase* addon, Span<AtkValue> atkValues)
        {
            weatherButtons.Clear();

            var layout = new VerticalListNode
            {
                IsVisible = true,
                Position  = ContentStartPosition
            };

            var windowHeight = 125f;

            var weathers = ParseLVB((ushort)GameState.TerritoryType)
                           .WeatherList
                           .Where
                           (weather => LuminaGetter.TryGetRow(weather, out Weather weatherRow) &&
                                       !string.IsNullOrEmpty(weatherRow.Name.ToString())       &&
                                       ImageHelper.TryGetGameIcon((uint)weatherRow.Icon, out _)
                           )
                           .ToList();

            const float WEATHER_BUTTON_HEIGHT = 54f;

            if (weathers is { Count: > 0 })
            {
                windowHeight += weathers.Count / 4 * WEATHER_BUTTON_HEIGHT + (weathers.Count / 4 - 1) * 5;
                SetWindowSize(Size.X, windowHeight);

                var currentRow = new HorizontalFlexNode
                {
                    IsVisible = true,
                    Size      = new(0, WEATHER_BUTTON_HEIGHT)
                };

                var itemsInCurrentRow = 0;

                foreach (var weather in weathers)
                {
                    var weatherRow = LuminaGetter.GetRowOrDefault<Weather>(weather);

                    if (itemsInCurrentRow >= 4)
                    {
                        layout.Height += currentRow.Height;
                        layout.AddNode(currentRow);
                        layout.AddDummy(5f);

                        currentRow = new HorizontalFlexNode
                        {
                            IsVisible = true,
                            Size      = new(0, WEATHER_BUTTON_HEIGHT)
                        };

                        itemsInCurrentRow = 0;
                    }

                    var weatherButton = new IconButtonNode
                    {
                        Size      = new(WEATHER_BUTTON_HEIGHT),
                        IsVisible = true,
                        IsEnabled = true,
                        IconId    = (uint)weatherRow.Icon,
                        OnClick = () =>
                        {
                            if (module.IsWeatherCustom() && module.GetDisplayWeather() == weather)
                                module.ToggleWeather(false);
                            else
                                module.ToggleWeather(true, weather);

                            foreach (var (id, (_, enabledIcon)) in weatherButtons)
                                enabledIcon.IsVisible = module.IsWeatherCustom() && module.GetDisplayWeather() == id;
                        },
                        TextTooltip = $"{weatherRow.Name}"
                    };
                    var enabledIconNode = new SimpleNineGridNode
                    {
                        TexturePath        = "ui/uld/ContentsReplaySetting_hr1.tex",
                        TextureCoordinates = new(36, 44),
                        TextureSize        = new(36),
                        Size               = new(22),
                        Position           = new(22, 24),
                        IsVisible          = module.IsWeatherCustom() && module.GetDisplayWeather() == weather
                    };
                    enabledIconNode.AttachNode(weatherButton);

                    weatherButtons[weather] = (weatherButton, enabledIconNode);

                    currentRow.AddNode(weatherButton);
                    currentRow.AddDummy(5);
                    currentRow.Width += weatherButton.Size.X + 4;

                    itemsInCurrentRow++;
                }

                if (itemsInCurrentRow > 0)
                {
                    layout.Height += currentRow.Height;
                    layout.AddNode(currentRow);
                }
            }

            windowHeight += 40 + 5 + 40;
            SetWindowSize(Size.X, windowHeight);

            layout.AddDummy(5f);

            var timeEnabled = new CheckboxNode
            {
                IsVisible = true,
                String    = Lang.Get("FastSetWeatherTime-Addon-ModifyTime"),
                Size      = new(100, 28),
                IsChecked = module.IsTimeCustom(),
                OnClick = x =>
                {
                    module.ToggleTime(x, RealTime);
                    timeNode.Value = (int)RealTime;
                }
            };
            layout.AddNode(timeEnabled);

            timeNode = new()
            {
                Range = new(0, (int)(MAX_TIME - 1)),
                Value = (int)module.GetDisplayTime(),
                Size  = new(Size.X - ContentStartPosition.X, 28),
                OnValueChanged = x =>
                {
                    if (!module.IsTimeCustom()) return;
                    module.ToggleTime(true, (uint)x);
                }
            };

            timeNode.ValueNode.FontSize      = 0;
            timeNode.FloatValueNode.FontSize = 0;

            layout.AddNode(timeNode);

            var timeRow = new HorizontalListNode
            {
                IsVisible = true,
                Size      = new(100, 35)
            };

            hourInputNode = new()
            {
                Size = new(78f, 30f),
                OnValueUpdate = hour =>
                {
                    if (!module.IsTimeCustom()) return;

                    var span = TimeSpan.FromSeconds(module.GetDisplayTime());
                    module.ToggleTime(true, (uint)(span.Minutes * 60 + span.Seconds + hour * 60 * 60));
                    timeNode.Value = (int)module.GetDisplayTime();
                }
            };

            timeRow.Width += hourInputNode.Width;
            timeRow.AddNode(hourInputNode);

            minuteInputNode = new()
            {
                Size = new(78f, 30f),
                OnValueUpdate = minute =>
                {
                    if (!module.IsTimeCustom()) return;

                    var span = TimeSpan.FromSeconds(module.GetDisplayTime());
                    module.ToggleTime(true, (uint)(minute * 60 + span.Seconds + span.Hours * 60 * 60));
                    timeNode.Value = (int)module.GetDisplayTime();
                }
            };

            timeRow.Width += minuteInputNode.Width;
            timeRow.AddNode(minuteInputNode);

            secondInputNode = new()
            {
                Size = new(78f, 30f),
                OnValueUpdate = second =>
                {
                    if (!module.IsTimeCustom()) return;

                    var span = TimeSpan.FromSeconds(module.GetDisplayTime());
                    module.ToggleTime(true, (uint)(span.Minutes * 60 + second + span.Hours * 60 * 60));
                    timeNode.Value = (int)module.GetDisplayTime();
                }
            };

            timeRow.Width += secondInputNode.Width;
            timeRow.AddNode(secondInputNode);

            layout.AddNode(timeRow);

            windowHeight += 35;
            SetWindowSize(Size.X, windowHeight);

            var operationRow = new HorizontalFlexNode
            {
                IsVisible = true,
                Size      = new(Size.X - 2 * ContentStartPosition.X, 45),
                Position  = new(0, -10)
            };
            layout.AddNode(operationRow);

            saveButtonNode = new TextButtonNode
            {
                String = Lang.Get("Save"),
                Size   = new(operationRow.Width / 2 - 5f, 30),
                OnClick = () =>
                {
                    if (!module.IsTimeCustom() && !module.IsWeatherCustom()) return;

                    var originalSetting = module.config.ZoneSettings.TryGetValue(GameState.TerritoryType, out var data) ? data : new();
                    module.config.ZoneSettings[GameState.TerritoryType] = new()
                    {
                        IsTimeEnabled    = module.IsTimeCustom()    || originalSetting.IsTimeEnabled,
                        IsWeatherEnabled = module.IsWeatherCustom() || originalSetting.IsWeatherEnabled,
                        Time             = module.IsTimeCustom() ? module.GetDisplayTime() : originalSetting.Time,
                        WeatherID        = module.IsWeatherCustom() ? module.GetDisplayWeather() : originalSetting.WeatherID
                    };
                    module.config.Save(ModuleManager.Instance().GetModule<FastSetWeatherTime>());

                    var setting = module.config.ZoneSettings[GameState.TerritoryType];

                    var message = Lang.Get
                    (
                        "FastSetWeatherTime-Notification-Saved",
                        GameState.TerritoryTypeData.ExtractPlaceName(),
                        GameState.TerritoryType,
                        setting.IsWeatherEnabled && setting.WeatherID != 255
                            ? LuminaWrapper.GetWeatherName(setting.WeatherID)
                            : LuminaWrapper.GetAddonText(7),
                        setting.IsTimeEnabled && TimeSpan.FromSeconds(setting.Time) is { } timeSpan
                            ? $"{timeSpan.Hours:D2}:{timeSpan.Minutes:D2}:{timeSpan.Seconds:D2}"
                            : LuminaWrapper.GetAddonText(7)
                    );
                    NotifyHelper.Instance().Chat(message);
                }
            };
            operationRow.AddNode(saveButtonNode);

            clearButtonNode = new TextButtonNode
            {
                String = Lang.Get("Clear"),
                Size   = new(operationRow.Width / 2 - 5f, 30),
                OnClick = () =>
                {
                    if (module.config.ZoneSettings.Remove(GameState.TerritoryType))
                    {
                        module.config.Save(ModuleManager.Instance().GetModule<FastSetWeatherTime>());
                        NotifyHelper.Instance().Chat(Lang.Get("FastSetWeatherTime-Notification-Cleard"));
                    }
                }
            };
            operationRow.AddNode(clearButtonNode);

            layout.AttachNode(this);
        }

        protected override void OnFinalize(AtkUnitBase* addon)
        {
            weatherButtons.Clear();
            timeNode        = null;
            hourInputNode   = null;
            minuteInputNode = null;
            secondInputNode = null;
            saveButtonNode  = null;
            clearButtonNode = null;
        }

        protected override void OnUpdate(AtkUnitBase* addon)
        {
            if (timeNode != null && hourInputNode != null && minuteInputNode != null && secondInputNode != null)
            {
                if (!module.IsTimeCustom())
                    timeNode.Value = (int)module.GetDisplayTime();

                var span = TimeSpan.FromSeconds(module.GetDisplayTime());
                hourInputNode.Value   = span.Hours;
                minuteInputNode.Value = span.Minutes;
                secondInputNode.Value = span.Seconds;
            }

            if (saveButtonNode != null && clearButtonNode != null)
            {
                saveButtonNode.IsEnabled  = GameState.TerritoryType > 0 && (module.IsTimeCustom() || module.IsWeatherCustom());
                clearButtonNode.IsEnabled = module.config.ZoneSettings.ContainsKey(GameState.TerritoryType);
            }
        }
    }

    #region 自定义类

    private class ZoneSetting
    {
        public bool IsWeatherEnabled { get; set; }
        public byte WeatherID        { get; set; } = 255;

        public bool IsTimeEnabled { get; set; }
        public uint Time          { get; set; }
    }

    private class LVBFile : FileResource
    {
        public string   ENVBFile;
        public ushort[] WeatherIDs;

        public override void LoadFile()
        {
            WeatherIDs = new ushort[32];

            var pos = 0xC;
            if (Data[pos] != 'S' || Data[pos + 1] != 'C' || Data[pos + 2] != 'N' || Data[pos + 3] != '1')
                pos += 0x14;
            var sceneChunkStart = pos;
            pos += 0x10;
            var settingsStart = sceneChunkStart + 8 + BitConverter.ToInt32(Data, pos);
            pos = settingsStart + 0x40;
            var weatherTableStart = settingsStart + BitConverter.ToInt32(Data, pos);
            pos = weatherTableStart;
            for (var i = 0; i < 32; i++)
                WeatherIDs[i] = BitConverter.ToUInt16(Data, pos + i * 2);

            if (Data.TryFindBytes("2E 65 6E 76 62 00", out pos))
            {
                var end = pos + 5;

                while (Data[pos - 1] != 0 && pos > 0)
                    pos--;

                ENVBFile = Encoding.UTF8.GetString(Data.Skip(pos).Take(end - pos).ToArray());
            }
        }
    }

    #endregion
    
    #region 常量

    private const uint   MAX_TIME = 60 * 60 * 24;
    private const string COMMAND  = "wt";

    private const string NAVI_MAP_IMAGE_URL =
        "https://raw.githubusercontent.com/AtmoOmen/StaticAssets/refs/heads/main/DailyRoutines/image/FastSetWeatherTime-NaviMap.png";

    #endregion
}
