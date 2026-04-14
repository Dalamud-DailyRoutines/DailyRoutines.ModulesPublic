using System.Collections.Frozen;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using DailyRoutines.Common.Interface.Windows;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using DailyRoutines.Manager;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using Lumina.Excel.Sheets;
using OmenTools.Info.Game.Data;
using OmenTools.Interop.Game.Helpers;
using OmenTools.Interop.Game.Lumina;
using OmenTools.OmenService;
using OmenTools.Threading;
using Aetheryte = Lumina.Excel.Sheets.Aetheryte;
using Control = FFXIVClientStructs.FFXIV.Client.Game.Control.Control;
using ObjectKind = Dalamud.Game.ClientState.Objects.Enums.ObjectKind;
using Treasure = FFXIVClientStructs.FFXIV.Client.Game.Object.Treasure;

namespace DailyRoutines.ModulesPublic;

public unsafe partial class FastObjectInteract : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title               = Lang.Get("FastObjectInteractTitle"),
        Description         = Lang.Get("FastObjectInteractDescription"),
        Category            = ModuleCategory.UIOptimization,
        ModulesPrerequisite = ["FastWorldTravel", "FastInstanceZoneChange"]
    };

    public override ModulePermission Permission { get; } = new() { NeedAuth = true };

    private Config config = null!;

    private Dictionary<uint, string> dcWorlds = [];

    private string blacklistKeyInput = string.Empty;
    private float  windowWidth;
    private bool   isUpdatingObjects;
    private bool   isOnWorldTraveling;

    private readonly List<InteractableObject> currentObjects = new(20);
    private          bool                     forceObjectUpdate;

    protected override void Init()
    {
        config = Config.Load(this) ??
                       new()
                       {
                           SelectedKinds =
                           [
                               ObjectKind.EventNpc, ObjectKind.EventObj, ObjectKind.Treasure, ObjectKind.Aetheryte,
                               ObjectKind.GatheringPoint
                           ]
                       };

        TaskHelper ??= new() { TimeoutMS = 5_000, ShowDebug = true };

        Overlay = new Overlay(this, $"##{nameof(FastObjectInteract)}")
        {
            Flags = ImGuiWindowFlags.NoScrollbar           |
                    ImGuiWindowFlags.AlwaysAutoResize      |
                    ImGuiWindowFlags.NoBringToFrontOnFocus |
                    ImGuiWindowFlags.NoDecoration          |
                    ImGuiWindowFlags.NoFocusOnAppearing    |
                    ImGuiWindowFlags.NoDocking
        };

        UpdateWindowFlags();

        DService.Instance().ClientState.Login            += OnLogin;
        DService.Instance().ClientState.TerritoryChanged += OnTerritoryChanged;

        FrameworkManager.Instance().Reg(OnUpdate, 250);

        LoadWorldData();
    }

    protected override void Uninit()
    {
        FrameworkManager.Instance().Unreg(OnUpdate);
        DService.Instance().ClientState.Login            -= OnLogin;
        DService.Instance().ClientState.TerritoryChanged -= OnTerritoryChanged;

        currentObjects.Clear();
    }
    
    #region UI

    protected override void ConfigUI()
    {
        var changed = false;

        using var width = ImRaii.ItemWidth(300f * GlobalUIScale);

        changed |= ImGui.Checkbox(Lang.Get("FastObjectInteract-WindowInvisibleWhenInteract"), ref config.WindowInvisibleWhenInteract);
        changed |= ImGui.Checkbox(Lang.Get("FastObjectInteract-WindowInvisibleWhenCombat"),   ref config.WindowInvisibleWhenCombat);

        if (ImGui.Checkbox(Lang.Get("FastObjectInteract-LockWindow"), ref config.LockWindow))
        {
            changed = true;
            UpdateWindowFlags();
        }

        if (ImGui.Checkbox(Lang.Get("FastObjectInteract-OnlyDisplayInViewRange"), ref config.OnlyDisplayInViewRange))
        {
            changed           = true;
            forceObjectUpdate = true;
        }

        changed |= ImGui.Checkbox(Lang.Get("FastObjectInteract-AllowClickToTarget"), ref config.AllowClickToTarget);

        ImGui.NewLine();

        ImGui.InputFloat($"{Lang.Get("FontScale")}##FontScaleInput", ref config.FontScale, format: "%.1f");

        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            changed = true;

            config.FontScale = Math.Max(0.1f, config.FontScale);
        }

        ImGui.InputFloat($"{Lang.Get("FastObjectInteract-MinButtonWidth")}##MinButtonWidthInput", ref config.MinButtonWidth, format: "%.1f");

        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            changed = true;

            ValidateButtonWidthSettings();
        }

        ImGui.InputFloat($"{Lang.Get("FastObjectInteract-MaxButtonWidth")}##MaxButtonWidthInput", ref config.MaxButtonWidth, format: "%.1f");

        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            changed = true;

            ValidateButtonWidthSettings();
        }

        ImGui.InputInt($"{Lang.Get("FastObjectInteract-MaxDisplayAmount")}##MaxDisplayAmountInput", ref config.MaxDisplayAmount);

        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            changed = true;

            config.MaxDisplayAmount = Math.Max(1, config.MaxDisplayAmount);
        }

        using (var combo = ImRaii.Combo
               (
                   $"{Lang.Get("FastObjectInteract-SelectedObjectKinds")}##ObjectKindsSelection",
                   Lang.Get("FastObjectInteract-SelectedObjectKindsAmount", config.SelectedKinds.Count),
                   ImGuiComboFlags.HeightLarge
               ))
        {
            if (combo)
            {
                foreach (var kind in Enum.GetValues<ObjectKind>())
                {
                    var state = config.SelectedKinds.Contains(kind);

                    if (ImGui.Checkbox(kind.ToString(), ref state))
                    {
                        changed = true;

                        if (state)
                            config.SelectedKinds.Add(kind);
                        else
                            config.SelectedKinds.Remove(kind);

                        forceObjectUpdate = true;
                    }
                }
            }
        }

        using (var combo = ImRaii.Combo
               (
                   $"{Lang.Get("FastObjectInteract-BlacklistKeysList")}##BlacklistObjectsSelection",
                   Lang.Get("FastObjectInteract-BlacklistKeysListAmount", config.BlacklistKeys.Count),
                   ImGuiComboFlags.HeightLarge
               ))
        {
            if (combo)
            {
                if (ImGuiOm.ButtonIcon("###BlacklistKeyInputAdd", FontAwesomeIcon.Plus, Lang.Get("Add")))
                {
                    if (config.BlacklistKeys.Add(blacklistKeyInput))
                    {
                        config.Save(this);
                        forceObjectUpdate = true;
                    }
                }

                ImGui.SameLine();
                ImGui.SetNextItemWidth(-1);
                ImGui.InputTextWithHint("###BlacklistKeyInput", $"{Lang.Get("FastObjectInteract-BlacklistKeysListInputHelp")}", ref blacklistKeyInput, 100);

                ImGui.Spacing();
                ImGui.Separator();
                ImGui.Spacing();

                var listToRemove = new List<string>();

                foreach (var key in config.BlacklistKeys.ToList())
                {
                    if (ImGuiOm.ButtonIcon(key, FontAwesomeIcon.TrashAlt, Lang.Get("Delete")))
                    {
                        changed = true;

                        listToRemove.Add(key);
                        forceObjectUpdate = true;
                    }

                    ImGui.SameLine();
                    ImGui.TextUnformatted(key);
                }

                if (listToRemove.Count > 0)
                    config.BlacklistKeys.RemoveRange(listToRemove);
            }
        }

        if (changed)
            config.Save(this);
    }

    protected override void OverlayUI()
    {
        using var fontPush = FontManager.Instance().GetUIFont(config.FontScale).Push();

        RenderObjectButtons(out var instanceChangeObj, out var worldTravelObj);

        if (instanceChangeObj.HasValue || worldTravelObj.HasValue)
        {
            ImGui.SameLine();

            using (ImRaii.Group())
            {
                if (instanceChangeObj.HasValue) RenderInstanceZoneChangeButtons();
                if (worldTravelObj.HasValue) RenderWorldChangeButtons();
            }
        }

        windowWidth = Math.Clamp(ImGui.GetItemRectSize().X, config.MinButtonWidth, config.MaxButtonWidth);
    }

    private void ValidateButtonWidthSettings()
    {
        if (config.MinButtonWidth >= config.MaxButtonWidth)
        {
            config.MinButtonWidth = 300f;
            config.MaxButtonWidth = 350f;
        }

        config.MinButtonWidth = Math.Max(1, config.MinButtonWidth);
        config.MaxButtonWidth = Math.Max(1, config.MaxButtonWidth);
    }

    private void RenderObjectButtons(out InteractableObject? instanceChangeObject, out InteractableObject? worldTravelObject)
    {
        instanceChangeObject = null;
        worldTravelObject    = null;

        using var group = ImRaii.Group();


        foreach (var obj in currentObjects)
        {
            if (obj.Pointer == null) continue;


            if (InstancesManager.IsInstancedArea && obj.Kind == ObjectKind.Aetheryte)
            {
                if (LuminaGetter.GetRow<Aetheryte>(obj.Pointer->BaseId) is { IsAetheryte: true })
                    instanceChangeObject = obj;
            }

            if (!isOnWorldTraveling                                     &&
                WorldTravelValidZones.Contains(GameState.TerritoryType) &&
                obj.Kind == ObjectKind.Aetheryte)
            {
                if (LuminaGetter.GetRow<Aetheryte>(obj.Pointer->BaseId) is { IsAetheryte: true })
                    worldTravelObject = obj;
            }


            RenderSingleObjectButton(obj);
        }
    }

    private void RenderSingleObjectButton(InteractableObject obj)
    {
        var isReachable   = obj.Pointer->IsReachable();
        var clickToTarget = config.AllowClickToTarget;

        using var alpha = ImRaii.PushStyle(ImGuiStyleVar.Alpha, 0.5f, !isReachable);

        if (clickToTarget)
        {

            using var colorActive = ImRaii.PushColor(ImGuiCol.ButtonActive,  ImGui.GetStyle().Colors[(int)ImGuiCol.HeaderActive],  !isReachable);
            using var colorHover  = ImRaii.PushColor(ImGuiCol.ButtonHovered, ImGui.GetStyle().Colors[(int)ImGuiCol.HeaderHovered], !isReachable);

            if (ButtonCenterText(obj.ID.ToString(), obj.Name))
            {
                if (isReachable) InteractWithObject(obj.Pointer, obj.Kind);
                else TargetSystem.Instance()->Target = obj.Pointer;
            }
        }
        else
        {

            using var disabled = ImRaii.Disabled(!isReachable);
            if (ButtonCenterText(obj.ID.ToString(), obj.Name) && isReachable)
                InteractWithObject(obj.Pointer, obj.Kind);
        }

        using (var popup = ImRaii.ContextPopupItem($"{obj.ID}_{obj.Name}"))
        {
            if (popup)
            {
                if (ImGui.MenuItem(Lang.Get("FastObjectInteract-AddToBlacklist")))
                {
                    var cleanName = FastObjectInteractTitleRegex().Replace(obj.Name, string.Empty).Trim();

                    if (config.BlacklistKeys.Add(cleanName))
                    {
                        config.Save(this);
                        forceObjectUpdate = true;
                    }
                }
            }
        }
    }

    private void RenderInstanceZoneChangeButtons()
    {

        for (var i = 1; i <= InstancesManager.Instance().GetInstancesCount(); i++)
        {
            if (i == InstancesManager.CurrentInstance) continue;
            if (ButtonCenterText($"InstanceChangeWidget_{i}", Lang.Get("FastObjectInteract-InstanceAreaChange", i)))
                ChatManager.Instance().SendMessage($"/pdr insc {i}");
        }
    }

    private void RenderWorldChangeButtons()
    {
        using var disabled = ImRaii.Disabled(isOnWorldTraveling);

        foreach (var worldPair in dcWorlds)
        {
            if (worldPair.Key == GameState.CurrentWorld) continue;
            if (ButtonCenterText($"WorldTravelWidget_{worldPair.Key}", $"{worldPair.Value}{(worldPair.Key == GameState.HomeWorld ? " (★)" : "")}"))
                ChatManager.Instance().SendMessage($"/pdr worldtravel {worldPair.Key}");
        }
    }

    public bool ButtonCenterText(string id, string text)
    {
        using var idPush = ImRaii.PushId($"{id}_{text}");

        var textSize    = ImGui.CalcTextSize(text);
        var cursorPos   = ImGui.GetCursorScreenPos();
        var padding     = ImGui.GetStyle().FramePadding;
        var buttonWidth = Math.Clamp(textSize.X + padding.X * 2, windowWidth, config.MaxButtonWidth);
        var result      = ImGui.Button(string.Empty, new Vector2(buttonWidth, textSize.Y + padding.Y * 2));

        ImGuiOm.TooltipHover(text);

        ImGui.GetWindowDrawList()
             .AddText(new(cursorPos.X + (buttonWidth - textSize.X) / 2, cursorPos.Y + padding.Y), ImGui.GetColorU32(ImGuiCol.Text), text);

        return result;
    }

    #endregion

    private void OnUpdate(IFramework framework)
    {
        if (isUpdatingObjects) return;

        var localPlayer    = Control.GetLocalPlayer();
        var canShowOverlay = !DService.Instance().Condition.IsBetweenAreas && localPlayer != null;

        if (!canShowOverlay)
        {
            if (Overlay.IsOpen)
            {
                currentObjects.Clear();
                windowWidth    = 0f;
                Overlay.IsOpen = false;
            }

            return;
        }

        if (forceObjectUpdate || Throttler.Shared.Throttle("FastObjectInteract-Monitor"))
        {
            isUpdatingObjects = true;
            forceObjectUpdate = false;

            UpdateObjectsList((GameObject*)localPlayer);

            isUpdatingObjects = false;
        }

        var shouldShowWindow = currentObjects.Count > 0 && IsWindowShouldBeOpen();

        if (Overlay != null)
        {
            Overlay.IsOpen = shouldShowWindow;
            if (!shouldShowWindow) windowWidth = 0f;
        }
    }

    private void OnLogin() =>
        LoadWorldData();

    private void OnTerritoryChanged(ushort zoneID) =>
        forceObjectUpdate = true;

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void UpdateObjectsList(GameObject* localPlayer)
    {
        currentObjects.Clear();

        var mgr = GameObjectManager.Instance();
        if (mgr == null) return;

        isOnWorldTraveling = DService.Instance().Condition.Any
        (
            ConditionFlag.ReadyingVisitOtherWorld,
            ConditionFlag.WaitingToVisitOtherWorld
        );

        var playerPos = localPlayer->Position;
        var maxAmount = config.MaxDisplayAmount;

        for (var i = 200; i < mgr->Objects.IndexSorted.Length; i++)
        {
            var objPtr = mgr->Objects.IndexSorted[i];
            if (objPtr == null) continue;

            var obj = objPtr.Value;
            if (obj == null) continue;

            if (!obj->GetIsTargetable()) continue;

            var kind = (ObjectKind)obj->ObjectKind;
            if (!config.SelectedKinds.Contains(kind)) continue;

            var distSq = Vector2.DistanceSquared(playerPos.ToVector2(), obj->Position.ToVector2());

            var limit = 400f;
            if (IncludeDistance.TryGetValue(kind, out var l))
                limit = l;

            if (distSq > limit) continue;

            if (!DService.Instance().Condition[ConditionFlag.InFlight] && MathF.Abs(obj->Position.Y - playerPos.Y) >= 4) continue;

            if (kind == ObjectKind.Treasure)
            {
                var treasure = (Treasure*)obj;
                if (treasure->Flags.IsSetAny(Treasure.TreasureFlags.FadedOut, Treasure.TreasureFlags.Opened))
                    continue;
            }

            var name = obj->NameString;
            if (string.IsNullOrEmpty(name)) continue;

            if (config.BlacklistKeys.Contains(name)) continue;

            if (kind == ObjectKind.EventNpc)
            {
                if (!ImportantENPC.Contains(obj->BaseId) && obj->NamePlateIconId == 0)
                    continue;

                if (ImportantENPC.Contains(obj->BaseId))
                {
                    if (ENPCTitle.TryGetValue(obj->BaseId, out var title))
                        name = string.Format(ENPC_TITLE_FORMAT, title, name);
                }
            }

            if (config.OnlyDisplayInViewRange)
            {
                if (!GameViewHelper.WorldToScreen(obj->Position, out _, out var view) || !view)
                    continue;
            }

            currentObjects.Add(new(obj, name, kind, distSq));
        }

        currentObjects.Sort(InteractableObjectComparer.Instance);

        if (currentObjects.Count > maxAmount)
            currentObjects.RemoveRange(maxAmount, currentObjects.Count - maxAmount);
    }

    private void UpdateWindowFlags()
    {
        if (Overlay == null) return;
        if (config.LockWindow)
            Overlay.Flags |= ImGuiWindowFlags.NoMove;
        else
            Overlay.Flags &= ~ImGuiWindowFlags.NoMove;
    }

    private bool IsWindowShouldBeOpen()
    {
        if (currentObjects.Count == 0) return false;

        if (config.WindowInvisibleWhenInteract && DService.Instance().Condition.IsOccupiedInEvent)
            return false;

        if (config.WindowInvisibleWhenCombat && DService.Instance().Condition[ConditionFlag.InCombat])
            return false;

        return true;
    }

    private void InteractWithObject(GameObject* obj, ObjectKind kind)
    {
        TaskHelper.RemoveQueueTasks(2);

        if (DService.Instance().Condition.IsOnMount)
        {
            TaskHelper.Enqueue(() => MovementManager.Instance().Dismount(), "DismountInteract", weight: 2);
            TaskHelper.DelayNext(500, weight: 2);
        }

        TaskHelper.Enqueue(IsAbleToInteract, "等待可交互状态", weight: 2);

        TaskHelper.Enqueue
        (
            () =>
            {
                if (!IsAbleToInteract()) return false;

                TargetSystem.Instance()->Target = obj;
                return TargetSystem.Instance()->InteractWithObject(obj, false) != 0;
            },
            "Interact",
            weight: 2
        );

        if (kind is ObjectKind.EventObj)
            TaskHelper.Enqueue(() => TargetSystem.Instance()->OpenObjectInteraction(obj), "OpenInteraction", weight: 2);

        return;

        static bool IsAbleToInteract()
        {
            return !DService.Instance().Condition.IsOnMount                                          &&
                   !DService.Instance().Condition.Any(ConditionFlag.Jumping, ConditionFlag.InFlight) &&
                   !MovementManager.Instance().IsManagerBusy;
        }
    }

    private void LoadWorldData()
    {
        if (!GameState.IsLoggedIn) return;

        dcWorlds = Sheets.Worlds
                         .Where(x => x.Value.DataCenter.RowId == GameState.CurrentDataCenter)
                         .OrderBy(x => x.Key                  == GameState.HomeWorld)
                         .ThenBy(x => x.Value.Name.ToString())
                         .ToDictionary(x => x.Key, x => x.Value.Name.ToString());
    }

    [GeneratedRegex(@"\[.*?\]")]
    private static partial Regex FastObjectInteractTitleRegex();

    private sealed class Config : ModuleConfig
    {
        public bool                AllowClickToTarget;
        public HashSet<string>     BlacklistKeys = [];
        public float               FontScale     = 1f;
        public bool                LockWindow;
        public float               MaxButtonWidth   = 400f;
        public int                 MaxDisplayAmount = 5;
        public float               MinButtonWidth   = 300f;
        public bool                OnlyDisplayInViewRange;
        public HashSet<ObjectKind> SelectedKinds               = [];
        public bool                WindowInvisibleWhenCombat   = true;
        public bool                WindowInvisibleWhenInteract = true;
    }

    private readonly struct InteractableObject
    (
        GameObject* ptr,
        string      name,
        ObjectKind  kind,
        float       distSq
    )
    {
        public readonly GameObject* Pointer    = ptr;
        public readonly string      Name       = name;
        public readonly ObjectKind  Kind       = kind;
        public readonly float       DistanceSq = distSq;


        public nint ID => (nint)Pointer;
    }

    private class InteractableObjectComparer : IComparer<InteractableObject>
    {
        public static readonly InteractableObjectComparer Instance = new();

        public int Compare(InteractableObject x, InteractableObject y)
        {
            var c = x.DistanceSq.CompareTo(y.DistanceSq);
            if (c != 0) return c;

            return GetPriority(x.Kind).CompareTo(GetPriority(y.Kind));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int GetPriority(ObjectKind kind) => kind switch
        {
            ObjectKind.Aetheryte      => 1,
            ObjectKind.EventNpc       => 2,
            ObjectKind.EventObj       => 3,
            ObjectKind.Treasure       => 4,
            ObjectKind.GatheringPoint => 5,
            _                         => 10
        };
    }
    
    #region 常量

    private const string ENPC_TITLE_FORMAT = "[{0}] {1}";

    private static readonly FrozenSet<uint> WorldTravelValidZones = [132U, 129U, 130U];

    private static readonly FrozenDictionary<ObjectKind, float> IncludeDistance = new Dictionary<ObjectKind, float>
    {
        [ObjectKind.Aetheryte]      = 400,
        [ObjectKind.GatheringPoint] = 100,
        [ObjectKind.CardStand]      = 150,
        [ObjectKind.EventObj]       = 100,
        [ObjectKind.Housing]        = 30,
        [ObjectKind.Treasure]       = 100
    }.ToFrozenDictionary();
    
    private static FrozenDictionary<uint, string> ENPCTitle { get; } =
        LuminaGetter.Get<ENpcResident>()
                    .Where(x => x.Unknown1 && !string.IsNullOrWhiteSpace(x.Title.ToString()))
                    .ToDictionary(x => x.RowId, x => x.Title.ToString())
                    .ToFrozenDictionary();

    private static FrozenSet<uint> ImportantENPC { get; } =
        LuminaGetter.Get<ENpcResident>()
                    .Where(x => x.Unknown1)
                    .Select(x => x.RowId)
                    .ToFrozenSet();

    #endregion
}
