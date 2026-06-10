using System.Numerics;
using OmenTools.Interop.Game.Lumina;

namespace DailyRoutines.ModulesPublic.Interface.UnifiedGlamourManager;

public partial class UnifiedGlamourManager
{
    private const string PLATE_EDITOR_ADDON_NAME = nameof(MiragePrismMiragePlate);
    private const byte FEMALE_SEX                = 1;
    private const string COMMAND                 = "ugm";
    private const string STONE_SCRIPT_URL        = "https://greasyfork.org/en/scripts/580109-%E7%9F%B3%E4%B9%8B%E5%AE%B6%E5%B9%BB%E5%8C%96%E6%A8%A1%E6%9D%BF%E5%AF%BC%E5%85%A5dr";
    private const string STONE_URL               = "https://ff14risingstones.web.sdo.com/pc/index.html#/glamour";
    private const string FAVORITE_ICON_ON        = "★";
    private const string FAVORITE_ICON_OFF       = "☆";
    private string newPresetTitle;
    private string newPresetNote;


    private const int TASK_TIMEOUT_MS              = 30_000;
    private const int REFRESH_STEP_DELAY_MS        = 1;
    private const int APPLY_RETRY_DELAY_MS         = 50;
    private const int DEFAULT_MIN_EQUIP_LEVEL      = 1;
    private const int DEFAULT_MAX_EQUIP_LEVEL      = 100;
    private const int MAX_EQUIP_LEVEL_INPUT        = 999;
    private const int VIRTUALIZED_LIST_BUFFER_ROWS = 3;
    private const int VIRTUALIZED_GRID_BUFFER_ROWS = 2;

    private const uint MIRAGE_PLATE_OPEN_MODE_AGENT_SHOW = 0;
    private const uint PRISM_BOX_CAPACITY                = 800;

    private static readonly SourceFilter[] SourceFilters =
    [
        SourceFilter.All,
        SourceFilter.Favorite,
        SourceFilter.PrismBox,
        SourceFilter.Cabinet
    ];

    private static readonly uint[][] JobFilterClassJobIDs =
    [
        [],
        [1, 19],
        [3, 21],
        [32],
        [37],
        [6, 24],
        [28],
        [33],
        [40],
        [2, 20],
        [4, 22],
        [29, 30],
        [34],
        [39],
        [41],
        [5, 23],
        [31],
        [38],
        [7, 25],
        [26, 27],
        [35],
        [36],
        [42],
        [8, 9, 10, 11, 12, 13, 14, 15],
        [16, 17, 18]
    ];

    private static readonly uint[] PLATE_SLOT_ADDON_TEXT_IDS =
    [
        11960, 11961, 11962, 11963, 11964, 11965, 11966, 11968, 11967, 11969, 750, 749
    ];
    
    // 5 是腰带，13 是职业水晶
    private static readonly int[] PLATE_SLOTS = [0, 1, 2, 3, 4, 6, 7, 8, 9, 10, 11, 12];

    private static string[] JobFilterNames
    {
        get
        {
            if (field != null) return field;

            var names = new string[JobFilterClassJobIDs.Length];
            names[0] = Lang.Get("All");

            for (var i = 1; i < JobFilterClassJobIDs.Length; i++)
                names[i] = string.Join(" / ", JobFilterClassJobIDs[i].Select(LuminaWrapper.GetJobName));

            return field = names;
        }
    }

    private static readonly PlateSlotDefinition[] PlateSlotDefinitions =
    [
        new(11960, static x => x.MainHand != 0),
        new(11961, static x => x.OffHand != 0 && x.MainHand == 0),
        new(11962, static x => x.Head   != 0),
        new(11963, static x => x.Body   != 0),
        new(11964, static x => x.Gloves != 0),
        new(11965, static x => x.Legs   != 0),
        new(11966, static x => x.Feet   != 0),
        new(11968, static x => x.Ears   != 0),
        new(11967, static x => x.Neck   != 0),
        new(11969, static x => x.Wrists != 0),
        new(750, static x => x.FingerL != 0 || x.FingerR != 0),
        new(749, static x => x.FingerL != 0 || x.FingerR != 0)
    ];

    private static string[] SortModeNames
    {
        get
        {
            if (field != null) return field;

            var nameAsc   = $"{Lang.Get("Name")} ({Lang.Get("Ascending")})";
            var nameDesc  = $"{Lang.Get("Name")} ({Lang.Get("Descending")})";
            var levelAsc  = $"{LuminaWrapper.GetAddonText(335)} ({Lang.Get("Ascending")})";
            var levelDesc = $"{LuminaWrapper.GetAddonText(335)} ({Lang.Get("Descending")})";

            return field =
            [
                $"{Lang.Get("Favorite")} / {nameAsc}",
                nameAsc,
                nameDesc,
                levelAsc,
                levelDesc
            ];
        }
    }

    private static string[] CreateSetRelationFilterNames() =>
    [
        Lang.Get("All"),
        Lang.Get("UnifiedGlamourManager-SetRelatedOnly"),
        Lang.Get("UnifiedGlamourManager-NonSetOnly")
    ];

    private static readonly Vector4 TitleColor             = KnownColor.HotPink.ToVector4();
    private static readonly Vector4 SelectedColor          = KnownColor.MediumVioletRed.ToVector4() with { W = 0.65f };
    private static readonly Vector4 ButtonAccentColor      = KnownColor.PaleVioletRed.ToVector4() with { W = 0.4f };
    private static readonly Vector4 ButtonActiveColor      = KnownColor.HotPink.ToVector4() with { W = 0.78f };
    private static readonly Vector4 SoftAccentColor        = KnownColor.Plum.ToVector4();
    private static readonly Vector4 GoldColor              = KnownColor.Gold.ToVector4();
    private static readonly Vector4 ErrorColor             = KnownColor.Crimson.ToVector4();
    private static readonly Vector4 FrameBGColor           = KnownColor.DimGray.ToVector4() with { W = 0.48f };
    private static readonly Vector4 NormalCardColor        = KnownColor.Black.ToVector4() with { W = 0.34f };
    private static readonly Vector4 NormalCardHoverColor   = KnownColor.Maroon.ToVector4() with { W = 0.26f };
    private static readonly Vector4 FavoriteCardColor      = KnownColor.Gold.ToVector4() with { W = 0.4f };
    private static readonly Vector4 FavoriteCardHoverColor = KnownColor.Goldenrod.ToVector4() with { W = 0.68f };
    private static readonly Vector4 SelectedBorderColor    = KnownColor.Khaki.ToVector4();
    private static readonly Vector4 MutedBorderColor       = KnownColor.DarkGray.ToVector4();
    private static readonly Vector4 StarOffColor           = KnownColor.Gray.ToVector4();
}
