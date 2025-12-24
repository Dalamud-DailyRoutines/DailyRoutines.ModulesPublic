using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using DailyRoutines.Abstracts;
using DailyRoutines.Managers;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.Graphics.Environment;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit;
using KamiToolKit.Nodes;
using Lumina.Data;
using Lumina.Excel.Sheets;

namespace DailyRoutines.ModulesPublic;

public unsafe class FastSetWeatherTime : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("FastSetWeatherTimeTitle"),
        Description = GetLoc("FastSetWeatherTimeDescription", Command),
        Category    = ModuleCategories.UIOptimization
    };
    
    public override ModulePermission Permission { get; } = new() { AllDefaultEnabled = true };

    private const uint   MaxTime = 60 * 60 * 24;
    private const string Command = "wt";

    private const string NaviMapImageURL =
        "https://raw.githubusercontent.com/AtmoOmen/StaticAssets/refs/heads/main/DailyRoutines/image/FastSetWeatherTime-NaviMap.png";
    
    // mov eax, 0
    private static readonly MemoryPatchWithPointer<uint> RenderSunlightShadowPatch = 
        new("49 0F BE 40 ?? 84 C0", [0xB8, 0x00, 0x00, 0x00, 0x00], pointerOffset: 1);
    
    // mov dl, 0, nop, nop
    private static readonly MemoryPatchWithPointer<byte> RenderWeatherPatch = 
        new("48 89 5C 24 ?? 57 48 83 EC 30 80 B9 ?? ?? ?? ?? ?? 49 8B F8 0F 29 74 24 ?? 48 8B D9 0F 28 F1", [0xB2, 0x00, 0x90, 0x90], 0x55, 1);
    
    // mov r9, 0
    private static readonly MemoryPatchWithPointer<uint> RenderTimePatch = 
        new("48 89 5C 24 ?? 57 48 83 EC 30 4C 8B 15", [0x49, 0xC7, 0xC1, 0x00, 0x00, 0x00, 0x00], 0x19, 3);

    private static readonly CompSig                        PlayWeatherSoundSig = new("48 89 5C 24 ?? 48 89 6C 24 ?? 56 57 41 56 48 83 EC ?? 45 33 F6 0F 29 74 24");
    private delegate        void*                          PlayWeatherSoundDelegate(void* manager, byte weatherID, void* a3, void* a4);
    private static          Hook<PlayWeatherSoundDelegate> PlayWeatherSoundHook;
    
    private static readonly CompSig                          UpdateBgmSituationSig = new("48 89 5C 24 ?? 57 48 83 EC 20 B8 ?? ?? ?? ?? 49 8B F9 41 8B D8");
    private delegate        void*                            UpdateBgmSituationDelegate(void* manager, ushort bgmSituationID, int column, void* a4, void* a5);
    private static          Hook<UpdateBgmSituationDelegate> UpdateBgmSituationHook;
    
    private static uint RealTime
    {
        get
        {
            var date = EorzeaDate.GetTime();
            return (uint)(date.Second + (60 * date.Minute) + (3600 * date.Hour));
        }
    }

    private static byte RealWeather => 
        *(byte*)((nint)EnvManager.Instance() + 0x26);

    private static byte CustomWeather
    {
        get => RenderWeatherPatch.CurrentValue;
        set => RenderWeatherPatch.Set(value);
    }
    
    private static uint CustomTime
    {
        get => RenderTimePatch.CurrentValue;
        set => RenderTimePatch.Set(value);
    }

    private static TextButtonNode? OpenButton;
    
    private static Config ModuleConfig = null!;

    protected override void Init()
    {
        ModuleConfig = LoadConfig<Config>() ?? new();

        PlayWeatherSoundHook ??= PlayWeatherSoundSig.GetHook<PlayWeatherSoundDelegate>(PlayWeatherSoundDetour);
        PlayWeatherSoundHook.Enable();
        
        UpdateBgmSituationHook ??= UpdateBgmSituationSig.GetHook<UpdateBgmSituationDelegate>(UpdateBgmSituationDetour);
        UpdateBgmSituationHook.Enable();

        DService.ClientState.TerritoryChanged += OnZoneChanged;
        
        AddonDRFastSetWeather.Addon = new()
        {
            InternalName = "DRFastSetWeather",
            Title        = $"{GetLoc("Weather")} & {GetLoc("Time")}",
            Size         = new(254f, 50f),
        };
        
        DService.AddonLifecycle.RegisterListener(AddonEvent.PostDraw,    "_NaviMap", OnAddon);
        DService.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "_NaviMap", OnAddon);

        CommandManager.AddSubCommand(Command, new(OnCommand) { HelpMessage = GetLoc("FastSetWeatherTime-CommandHelp") });
    }

    protected override void Uninit()
    {
        DService.ClientState.TerritoryChanged -= OnZoneChanged;
        
        DService.AddonLifecycle.UnregisterListener(OnAddon);
        OnAddon(AddonEvent.PreFinalize, null);

        CommandManager.RemoveSubCommand(Command);
        
        AddonDRFastSetWeather.Addon?.Dispose();
        AddonDRFastSetWeather.Addon = null;

        ToggleWeather(false);
        ToggleTime(false);
    }
    
    protected override void ConfigUI()
    {
        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), GetLoc("FastSetWeatherTime-CommandHelp"));
        using (ImRaii.PushIndent())
        {
            ImGui.Text($"1. /pdr {Command}");
            
            if (ImageHelper.TryGetImage(NaviMapImageURL, out var image))
            {
                ImGui.Text($"2. {GetLoc("FastSetWeatherTime-OperationHelp-ClickNaviMap")}");
                ImGui.Image(image.Handle, image.Size);
            }
        }
    }

    #region 事件
    
    private static void* PlayWeatherSoundDetour(void* manager, byte weatherID, void* a3, void* a4)
    {
        if (IsWeatherCustom())
            weatherID = GetDisplayWeather();

        return PlayWeatherSoundHook.Original(manager, weatherID, a3, a4);
    }

    private static void* UpdateBgmSituationDetour(void* manager, ushort bgmSituationID, int column, void* a4, void* a5)
    {
        if (IsTimeCustom() && column != 3)
        {
            var seconds = CustomTime % 86400;
            var isDay   = seconds is >= 21600 and < 64800;
            column = isDay ? 1 : 2;
        }

        return UpdateBgmSituationHook.Original(manager, bgmSituationID, column, a4, a5);
    }

    private static void OnZoneChanged(ushort _)
    {
        ModuleConfig.ZoneSettings.TryGetValue(GameState.TerritoryType, out var info);

        if (info is { IsWeatherEnabled: true, WeatherID: not 255 })
            ToggleWeather(true, info.WeatherID);
        else
            ToggleWeather(false);

        if (info is { IsTimeEnabled: true })
            ToggleTime(true, info.Time);
        else
            ToggleTime(false);
    }
    
    private static void OnAddon(AddonEvent type, AddonArgs args)
    {
        switch (type)
        {
            case AddonEvent.PreFinalize:
                OpenButton?.DetachNode();
                OpenButton = null;
                break;
            case AddonEvent.PostDraw:
                if (NaviMap == null) return;

                if (OpenButton == null)
                {
                    OpenButton = new()
                    {
                        Position  = new(158, 24),
                        Size      = new(36),
                        IsVisible = true,
                        SeString  = string.Empty,
                        OnClick   = () => AddonDRFastSetWeather.Addon.Toggle()
                    };

                    OpenButton.BackgroundNode.IsVisible = false;
                    OpenButton.AttachNode(NaviMap->RootNode);
                }

                OpenButton.Tooltip = LuminaGetter.GetRowOrDefault<Weather>(GetDisplayWeather()).Name;
                break;
        }
    }

    private static void OnCommand(string command, string args) => 
        AddonDRFastSetWeather.Addon.Toggle();

    #endregion
    
    #region 控制

    private static void ToggleWeather(bool isEnabled, byte weatherID = 255)
    {
        if (!isEnabled || weatherID == 255)
            DisableCustomWeather();
        else
        {
            EnableCustomWeather();
            CustomWeather = weatherID;
        }
    }

    private static void EnableCustomWeather()
    {
        if (IsWeatherCustom()) return;
        
        RenderWeatherPatch.Enable();
        RenderSunlightShadowPatch.Enable();
    }

    private static void DisableCustomWeather()
    {
        if (!IsWeatherCustom()) return;
        
        RenderWeatherPatch.Disable();
        RenderSunlightShadowPatch.Disable();
    }
    
    private static void ToggleTime(bool isEnabled, uint time = 0)
    {
        if (!isEnabled)
            DisableCustomTime();
        else
        {
            EnableCustomTime();
            CustomTime = time;
        }
    }
    
    private static void EnableCustomTime()
    {
        if (IsTimeCustom()) return;
        RenderTimePatch.Enable();
    }

    private static void DisableCustomTime()
    {
        if (!IsTimeCustom()) return;
        RenderTimePatch.Disable();
    }

    #endregion
    
    #region 工具

    private static byte GetDisplayWeather() => 
        IsWeatherCustom() ? CustomWeather : RealWeather;

    private static uint GetDisplayTime() =>
        IsTimeCustom() ? CustomTime : RealTime;

    private static bool IsWeatherCustom() =>
        RenderWeatherPatch.IsEnabled;
    
    private static bool IsTimeCustom() => 
        RenderTimePatch.IsEnabled;

    private static (List<byte> WeatherList, string ENVBFile) ParseLVB(ushort zoneID)
    {
        var weathers = new List<byte>();
        
        try
        {
            var file = DService.Data.GetFile<LVBFile>($"bg/{LuminaGetter.GetRowOrDefault<TerritoryType>(zoneID).Bg}.lvb");
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

    private class Config : ModuleConfiguration
    {
        public Dictionary<uint, ZoneSetting> ZoneSettings = [];
    }

    private class AddonDRFastSetWeather : NativeAddon
    {
        public static AddonDRFastSetWeather? Addon { get; set; }

        private Dictionary<byte, (IconButtonNode IconButton, SimpleNineGridNode EnabledIcon)> WeatherButtons = [];

        private SliderNode? TimeNode;
        
        private NumericInputNode? HourInputNode;
        private NumericInputNode? MinuteInputNode;
        private NumericInputNode? SecondInputNode;
        
        private TextButtonNode? SaveButtonNode;
        private TextButtonNode? ClearButtonNode;

        protected override void OnSetup(AtkUnitBase* addon)
        {
            WeatherButtons.Clear();

            var layout = new VerticalListNode
            {
                IsVisible = true,
                Position  = ContentStartPosition,
            };

            var windowHeight = 125f;

            var weathers = ParseLVB((ushort)GameState.TerritoryType)
                           .WeatherList
                           .Where(weather => LuminaGetter.TryGetRow(weather, out Weather weatherRow) &&
                                             !string.IsNullOrEmpty(weatherRow.Name.ToString())       &&
                                             ImageHelper.TryGetGameIcon((uint)weatherRow.Icon, out _))
                           .ToList();

            const float weatherButtonHeight = 54f;
            if (weathers is { Count: > 0 })
            {
                windowHeight += (weathers.Count / 4 * weatherButtonHeight) + (((weathers.Count / 4) - 1) * 5);
                SetWindowSize(Size.X, windowHeight);

                var currentRow = new HorizontalFlexNode
                {
                    IsVisible = true,
                    Size      = new(0, weatherButtonHeight)
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
                            Size      = new(0, weatherButtonHeight)
                        };

                        itemsInCurrentRow = 0;
                    }

                    var weatherButton = new IconButtonNode
                    {
                        Size      = new(weatherButtonHeight),
                        IsVisible = true,
                        IsEnabled = true,
                        IconId    = (uint)weatherRow.Icon,
                        OnClick = () =>
                        {
                            if (IsWeatherCustom() && GetDisplayWeather() == weather)
                                ToggleWeather(false);
                            else
                                ToggleWeather(true, weather);

                            foreach (var (id, (_, enabledIcon)) in WeatherButtons)
                                enabledIcon.IsVisible = IsWeatherCustom() && GetDisplayWeather() == id;
                        },
                        Tooltip = $"{weatherRow.Name}",
                    };
                    var enabledIconNode = new SimpleNineGridNode
                    {
                        TexturePath        = "ui/uld/ContentsReplaySetting_hr1.tex",
                        TextureCoordinates = new(36, 44),
                        TextureSize        = new(36),
                        Size               = new(22),
                        Position           = new(22, 24),
                        IsVisible          = IsWeatherCustom() && GetDisplayWeather() == weather,
                    };
                    enabledIconNode.AttachNode(weatherButton);

                    WeatherButtons[weather] = (weatherButton, enabledIconNode);

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
                String    = GetLoc("FastSetWeatherTime-Addon-ModifyTime"),
                Size      = new(100, 28),
                IsChecked = IsTimeCustom(),
                OnClick = x =>
                {
                    ToggleTime(x, RealTime);
                    TimeNode.Value = (int)RealTime;
                }
            };
            layout.AddNode(timeEnabled);

            TimeNode = new()
            {
                Range = new(0, (int)(MaxTime - 1)),
                Value = (int)GetDisplayTime(),
                Size  = new(Size.X - ContentStartPosition.X, 28),
                OnValueChanged = x =>
                {
                    if (!IsTimeCustom()) return;
                    ToggleTime(true, (uint)x);
                }
            };

            TimeNode.ValueNode.FontSize      = 0;
            TimeNode.FloatValueNode.FontSize = 0;

            layout.AddNode(TimeNode);

            var timeRow = new HorizontalListNode
            {
                IsVisible = true,
                Size      = new(100, 35),
            };

            HourInputNode = new()
            {
                Size = new(78f, 30f),
                OnValueUpdate = hour =>
                {
                    if (!IsTimeCustom()) return;

                    var span = TimeSpan.FromSeconds(GetDisplayTime());
                    ToggleTime(true, (uint)((span.Minutes * 60) + span.Seconds + (hour * 60 * 60)));
                    TimeNode.Value = (int)GetDisplayTime();
                }
            };

            timeRow.Width += HourInputNode.Width;
            timeRow.AddNode(HourInputNode);

            MinuteInputNode = new()
            {
                Size = new(78f, 30f),
                OnValueUpdate = minute =>
                {
                    if (!IsTimeCustom()) return;

                    var span = TimeSpan.FromSeconds(GetDisplayTime());
                    ToggleTime(true, (uint)((minute * 60) + span.Seconds + (span.Hours * 60 * 60)));
                    TimeNode.Value = (int)GetDisplayTime();
                }
            };

            timeRow.Width += MinuteInputNode.Width;
            timeRow.AddNode(MinuteInputNode);

            SecondInputNode = new()
            {
                Size = new(78f, 30f),
                OnValueUpdate = second =>
                {
                    if (!IsTimeCustom()) return;

                    var span = TimeSpan.FromSeconds(GetDisplayTime());
                    ToggleTime(true, (uint)((span.Minutes * 60) + second + (span.Hours * 60 * 60)));
                    TimeNode.Value = (int)GetDisplayTime();
                }
            };

            timeRow.Width += SecondInputNode.Width;
            timeRow.AddNode(SecondInputNode);

            layout.AddNode(timeRow);

            windowHeight += 35;
            SetWindowSize(Size.X, windowHeight);
            
            var operationRow = new HorizontalFlexNode
            {
                IsVisible = true,
                Size      = new(Size.X - (2 * ContentStartPosition.X), 45),
                Position  = new(0, -10)
            };
            layout.AddNode(operationRow);

            SaveButtonNode = new TextButtonNode
            {
                String = GetLoc("Save"),
                Size   = new((operationRow.Width / 2) - 5f, 30),
                OnClick = () =>
                {
                    if (!IsTimeCustom() && !IsWeatherCustom()) return;
                    
                    var originalSetting = ModuleConfig.ZoneSettings.TryGetValue(GameState.TerritoryType, out var data) ? data : new();
                    ModuleConfig.ZoneSettings[GameState.TerritoryType] = new()
                    {
                        IsTimeEnabled    = IsTimeCustom()    || originalSetting.IsTimeEnabled,
                        IsWeatherEnabled = IsWeatherCustom() || originalSetting.IsWeatherEnabled,
                        Time             = IsTimeCustom() ? GetDisplayTime() : originalSetting.Time,
                        WeatherID        = IsWeatherCustom() ? GetDisplayWeather() : originalSetting.WeatherID,
                    };
                    ModuleConfig.Save(ModuleManager.GetModule<FastSetWeatherTime>());

                    var setting = ModuleConfig.ZoneSettings[GameState.TerritoryType];

                    var message = GetLoc("FastSetWeatherTime-Notification-Saved",
                                         GameState.TerritoryTypeData.ExtractPlaceName(),
                                         GameState.TerritoryType,
                                         setting.IsWeatherEnabled && setting.WeatherID != 255
                                             ? LuminaWrapper.GetWeatherName(setting.WeatherID)
                                             : LuminaWrapper.GetAddonText(7),
                                         setting.IsTimeEnabled && TimeSpan.FromSeconds(setting.Time) is { } timeSpan
                                             ? $"{timeSpan.Hours:D2}:{timeSpan.Minutes:D2}:{timeSpan.Seconds:D2}"
                                             : LuminaWrapper.GetAddonText(7));
                    Chat(message);
                }
            };
            operationRow.AddNode(SaveButtonNode);
            
            ClearButtonNode = new TextButtonNode
            {
                String = GetLoc("Clear"),
                Size   = new((operationRow.Width / 2) - 5f, 30),
                OnClick = () =>
                {
                    if (ModuleConfig.ZoneSettings.Remove(GameState.TerritoryType))
                    {
                        ModuleConfig.Save(ModuleManager.GetModule<FastSetWeatherTime>());
                        Chat(GetLoc("FastSetWeatherTime-Notification-Cleard"));
                    }
                }
            };
            operationRow.AddNode(ClearButtonNode);

            layout.AttachNode(this);
        }

        protected override void OnFinalize(AtkUnitBase* addon)
        {
            WeatherButtons.Clear();
            TimeNode        = null;
            HourInputNode   = null;
            MinuteInputNode = null;
            SecondInputNode = null;
            SaveButtonNode  = null;
            ClearButtonNode = null;
        }

        protected override void OnUpdate(AtkUnitBase* addon)
        {
            if (TimeNode != null && HourInputNode != null && MinuteInputNode != null && SecondInputNode != null)
            {
                if (!IsTimeCustom())
                    TimeNode.Value = (int)GetDisplayTime();

                var span = TimeSpan.FromSeconds(GetDisplayTime());
                HourInputNode.Value = span.Hours;
                MinuteInputNode.Value = span.Minutes;
                SecondInputNode.Value = span.Seconds;
            }

            if (SaveButtonNode != null && ClearButtonNode != null)
            {
                SaveButtonNode.IsEnabled  = GameState.TerritoryType > 0 && (IsTimeCustom() || IsWeatherCustom());
                ClearButtonNode.IsEnabled = ModuleConfig.ZoneSettings.ContainsKey(GameState.TerritoryType);
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
        public ushort[] WeatherIDs;
        public string   ENVBFile;

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
                WeatherIDs[i] = BitConverter.ToUInt16(Data, pos + (i * 2));

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
}
