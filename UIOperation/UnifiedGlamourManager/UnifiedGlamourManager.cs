using System.Numerics;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace DailyRoutines.ModulesPublic;

public unsafe partial class UnifiedGlamourManager : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("UnifiedGlamourManagerTitle"),
        Description = Lang.Get("UnifiedGlamourManagerDescription"),
        Category    = ModuleCategory.UIOperation,
        Author      = ["ErxCharlotte"]
    };

    public override ModulePermission Permission { get; } = new() { AllDefaultEnabled = true };

    private Config config = null!;

    private bool isOpen;
    private bool isPrismBoxOpen;
    private bool isRestoringItem;
    private bool requestFocusNextOpen;
    private bool autoOpenedByPlateEditor;
    private string searchText = string.Empty;
    private SourceFilter sourceFilter = SourceFilter.All;
    private SortMode sortMode = SortMode.FavoriteThenNameAsc;
    private SetRelationFilter setRelationFilter = SetRelationFilter.All;
    private bool filterByCurrentPlateSlot = true;
    private bool enableLevelFilter;
    private int minEquipLevel = DEFAULT_MIN_EQUIP_LEVEL;
    private int maxEquipLevel = DEFAULT_MAX_EQUIP_LEVEL;
    private int selectedJobFilterIndex;
    private readonly List<UnifiedItem> items = [];
    private readonly List<UnifiedItem> filteredItems = [];
    private readonly HashSet<uint> favoriteItemIDs = [];
    private readonly Dictionary<ulong, bool> jobFilterCache = [];
    private readonly Dictionary<ulong, bool> plateSlotFilterCache = [];
    private readonly HashSet<uint> ownedConcreteItemIDs = [];
    private bool useGridView = true;
    private string lastInventorySnapshotFingerprint = string.Empty;
    private string lastRetainerSnapshotFingerprint = string.Empty;
    private string lastRetainerSourceKey = string.Empty;
    private int prismBoxItemCount;
    private int cabinetItemCount;
    private UnifiedItem? selectedItem;
    private bool requestClearFavoritesConfirm;
    private bool requestRestoreItemConfirm;
    private bool filteredItemsDirty = true;
    private bool isRefreshingItems;
    private uint lastFilterPlateSlot = uint.MaxValue;
    private int iconLoadCountThisFrame;

    protected override void Init()
    {
        config = Config.Load(this) ?? new();
        NormalizeConfig();
        TaskHelper ??= new() { TimeoutMS = TASK_TIMEOUT_MS };

        Overlay = new(this);
        Overlay.IsOpen = false;

        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PostSetup,   PRISM_BOX_ADDON_NAME,    OnPrismBoxAddon);
        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, PRISM_BOX_ADDON_NAME,    OnPrismBoxAddon);
        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PostSetup,   PLATE_EDITOR_ADDON_NAME, OnPlateEditorAddon);
        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, PLATE_EDITOR_ADDON_NAME, OnPlateEditorAddon);
    }

    protected override void Uninit()
    {
        CloseWindow();
        TaskHelper?.Abort();
        ClearIconCache();

        DService.Instance().AddonLifecycle.UnregisterListener(OnPrismBoxAddon);
        DService.Instance().AddonLifecycle.UnregisterListener(OnPlateEditorAddon);
    }

    protected override void ConfigUI()
    {
        if (ImGui.Button(Lang.Get("UnifiedGlamourManager-Open")))
            OpenWindow(false);

        ImGui.SameLine();

        using (ImRaii.Disabled(isRefreshingItems))
        {
            if (ImGui.Button(Lang.Get("UnifiedGlamourManager-Refresh")))
                StartRefreshAll();
        }

        ImGui.TextDisabled(Lang.Get("UnifiedGlamourManager-LoadedStatus", items.Count, GetLoadedFavoriteCount()));
    }

    protected override void OverlayPreDraw()
    {
        if (isOpen && autoOpenedByPlateEditor && !IsPlateEditorReady())
            CloseWindow();

        if (Overlay != null && Overlay.IsOpen != isOpen)
            SetOverlayOpen(isOpen);
    }

    protected override void OverlayUI()
    {
        if (!isOpen)
            return;

        iconLoadCountThisFrame = 0;
        DrawWindow();
    }

    private void OnPrismBoxAddon(AddonEvent type, AddonArgs args)
    {
        switch (type)
        {
            case AddonEvent.PostSetup:
                isPrismBoxOpen = true;
                break;

            case AddonEvent.PreFinalize:
                isPrismBoxOpen = false;

                if (autoOpenedByPlateEditor)
                    CloseWindow();

                break;
        }
    }

    private void OnPlateEditorAddon(AddonEvent type, AddonArgs args)
    {
        switch (type)
        {
            case AddonEvent.PostSetup when isPrismBoxOpen:
                OpenWindow(true);
                break;

            case AddonEvent.PreFinalize:
                if (autoOpenedByPlateEditor)
                    CloseWindow();

                break;
        }
    }

    private void OpenWindow(bool openedByPlateEditor)
    {
        isOpen = true;
        autoOpenedByPlateEditor = openedByPlateEditor;
        requestFocusNextOpen = true;

        SetOverlayOpen(true);

        StartRefreshAll();
    }

    private void CloseWindow()
    {
        isOpen = false;
        autoOpenedByPlateEditor = false;

        SetOverlayOpen(false);
    }

    private void SetOverlayOpen(bool open)
    {
        if (Overlay != null)
            Overlay.IsOpen = open;
    }

    private static bool TryGetLoadedMirageManager(out MirageManager* manager)
    {
        manager = MirageManager.Instance();
        return manager != null && manager->PrismBoxRequested && manager->PrismBoxLoaded;
    }

    #region 预定义

    private const string PRISM_BOX_ADDON_NAME = "MiragePrismPrismBox";
    private const string PLATE_EDITOR_ADDON_NAME = "MiragePrismMiragePlate";

    private const int TASK_TIMEOUT_MS = 30_000;
    private const int REFRESH_STEP_DELAY_MS = 1;
    private const int CABINET_APPLY_RETRY_DELAY_MS = 50;
    private const uint ITEM_ID_NORMALIZE_MODULO = 100_0000;
    private const uint MIN_VALID_ITEM_ID = 1;
    private const int DEFAULT_MIN_EQUIP_LEVEL = 1;
    private const int DEFAULT_MAX_EQUIP_LEVEL = 100;
    private const int MAX_EQUIP_LEVEL_INPUT = 999;

    private const float WINDOW_DEFAULT_WIDTH = 1420f;
    private const float WINDOW_DEFAULT_HEIGHT = 860f;
    private const float WINDOW_MIN_WIDTH = 1120f;
    private const float WINDOW_MIN_HEIGHT = 680f;
    private const float WINDOW_MAX_SIZE = 9999f;
    private const float LEFT_PANEL_WIDTH = 292f;
    private const float RIGHT_PANEL_WIDTH = 352f;
    private const float TOP_BAR_HEIGHT = 82f;
    private const float PANEL_PADDING_X = 12f;
    private const float PANEL_PADDING_Y = 10f;
    private const float POPUP_BUTTON_WIDTH = 132f;
    private const float TOP_BAR_BUTTON_WIDTH = 112f;
    private const float TOP_BAR_CLEAR_BUTTON_WIDTH = 88f;
    private const float SEARCH_MIN_WIDTH = 240f;
    private const float SEARCH_MAX_WIDTH = 520f;
    private const float TOP_BAR_RESERVED_WIDTH = 900f;
    private const float MAIN_LAYOUT_MIN_HEIGHT = 420f;
    private const float ICON_SIZE_LIST = 54f;
    private const float ICON_SIZE_SELECTED = 82f;
    private const float CARD_MIN_HEIGHT = 88f;
    private const float ITEM_SPACING_Y = 8f;
    private const float CARD_ROUNDING = 8f;
    private const float CARD_BORDER_THICKNESS_SELECTED = 2.0f;
    private const float CARD_BORDER_THICKNESS_FAVORITE = 1.8f;
    private const float CARD_BORDER_THICKNESS_HOVERED = 1.2f;
    private const float CONTROL_HEIGHT = 36f;
    private const float RESTORE_BUTTON_HEIGHT = 38f;
    private const float WINDOW_FONT_SCALE = 1.00f;
    private const int ICON_TEXTURE_CACHE_LIMIT = 1024;
    private const int ICON_LOADS_PER_FRAME = 8;
    private const int VIRTUALIZED_LIST_BUFFER_ROWS = 3;
    private const int VIRTUALIZED_GRID_BUFFER_ROWS = 2;
    private const int SEARCH_INPUT_MAX_LENGTH = 128;

    private const float VIEW_MODE_BUTTON_WIDTH = 72f;
    private const float VIEW_MODE_BUTTON_HEIGHT = 30f;
    private const float GRID_MIN_CELL_SIZE = 58f;
    private const float GRID_MAX_CELL_SIZE = 68f;
    private const float GRID_CELL_SPACING = 4f;
    private const float GRID_ICON_MIN_SIZE = 42f;
    private const float GRID_ICON_PADDING = 10f;
    private const float GRID_CELL_ROUNDING = 6f;

    private const string FAVORITE_ICON_ON = "★";
    private const string FAVORITE_ICON_OFF = "☆";

    private static readonly Vector4 ACCENT_COLOR = new(1.00f, 0.48f, 0.72f, 1.00f);
    private static readonly Vector4 ACCENT_SOFT_COLOR = new(1.00f, 0.70f, 0.84f, 1.00f);
    private static readonly Vector4 SELECTED_COLOR = new(0.54f, 0.16f, 0.42f, 0.96f);
    private static readonly Vector4 SELECTED_HOVER_COLOR = new(0.66f, 0.22f, 0.50f, 1.00f);
    private static readonly Vector4 FAVORITE_COLOR = new(1.00f, 0.88f, 0.46f, 0.42f);
    private static readonly Vector4 FAVORITE_HOVER_COLOR = new(1.00f, 0.92f, 0.58f, 0.60f);
    private static readonly Vector4 FAVORITE_BORDER_COLOR = new(1.00f, 0.86f, 0.36f, 0.88f);
    private static readonly Vector4 HOVER_COLOR = new(0.24f, 0.13f, 0.24f, 0.96f);
    private static readonly Vector4 NORMAL_CARD_COLOR = new(0.08f, 0.06f, 0.09f, 0.76f);
    private static readonly Vector4 CYAN_COLOR = new(1.00f, 0.78f, 0.90f, 1.00f);
    private static readonly Vector4 MUTED_COLOR = new(0.72f, 0.66f, 0.70f, 1.00f);
    private static readonly Vector4 WARNING_COLOR = new(1.00f, 0.82f, 0.20f, 1.00f);
    private static readonly Vector4 ERROR_COLOR = new(1.00f, 0.30f, 0.42f, 1.00f);
    private static readonly Vector4 STAR_ON_COLOR = new(0.00f, 0.58f, 1.00f, 1.00f);
    private static readonly Vector4 STAR_OFF_COLOR = new(0.86f, 0.78f, 0.84f, 1.00f);

    #endregion
}
