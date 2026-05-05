using System.Numerics;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;

namespace DailyRoutines.ModulesPublic;

public partial class UnifiedGlamourManager : ModuleBase
{
    #region 模块信息

    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("UnifiedGlamourManagerTitle"),
        Description = Lang.Get("UnifiedGlamourManagerDescription"),
        Category    = ModuleCategory.UIOperation,
        Author      = ["ErxCharlotte"]
    };

    public override ModulePermission Permission { get; } = new()
    {
        AllDefaultEnabled = true
    };

    #endregion

    #region 字段

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
    private int minEquipLevel = 1;
    private int maxEquipLevel = 100;
    private int selectedJobFilterIndex;
    private readonly List<UnifiedItem> items = [];
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

    #endregion

    #region 生命周期

    protected override void Init()
    {
        LoadModuleConfig();

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

        DService.Instance().AddonLifecycle.UnregisterListener(OnPrismBoxAddon);
        DService.Instance().AddonLifecycle.UnregisterListener(OnPlateEditorAddon);
    }


    #endregion

    #region 配置界面

    protected override void ConfigUI()
    {
        if (ImGui.Button(Lang.Get("UnifiedGlamourManager-Open")))
            OpenWindow(false);

        ImGui.SameLine();

        if (ImGui.Button(Lang.Get("UnifiedGlamourManager-Refresh")))
            RefreshAll();

        ImGui.TextDisabled(Lang.Get("UnifiedGlamourManager-LoadedStatus", items.Count, GetLoadedFavoriteCount()));
    }

    #endregion

    #region Overlay

    protected override void OverlayPreDraw()
    {
        if (isOpen && autoOpenedByPlateEditor && !IsPlateEditorReady())
            CloseWindow();

        if (Overlay != null && Overlay.IsOpen != isOpen)
            Overlay.IsOpen = isOpen;
    }


    protected override void OverlayUI()
    {
        if (!isOpen)
            return;

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

        if (Overlay != null)
            Overlay.IsOpen = true;

        RefreshAll();
    }

    private void CloseWindow()
    {
        isOpen = false;
        autoOpenedByPlateEditor = false;

        if (Overlay != null)
            Overlay.IsOpen = false;
    }

    #endregion

    #region 常量

    private const string PRISM_BOX_ADDON_NAME = "MiragePrismPrismBox";
    private const string PLATE_EDITOR_ADDON_NAME = "MiragePrismMiragePlate";

    private const float WINDOW_DEFAULT_WIDTH = 1420f;
    private const float WINDOW_DEFAULT_HEIGHT = 860f;
    private const float LEFT_PANEL_WIDTH = 292f;
    private const float RIGHT_PANEL_WIDTH = 352f;
    private const float TOP_BAR_HEIGHT = 82f;
    private const float ICON_SIZE_LIST = 54f;
    private const float ICON_SIZE_SELECTED = 82f;
    private const float CARD_MIN_HEIGHT = 88f;
    private const float ITEM_SPACING_Y = 8f;
    private const float CONTROL_HEIGHT = 36f;
    private const float WINDOW_FONT_SCALE = 1.00f;

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
