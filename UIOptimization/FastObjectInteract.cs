using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using DailyRoutines.Abstracts;
using DailyRoutines.Managers;
using DailyRoutines.Windows;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using Lumina.Excel.Sheets;
using Aetheryte = Lumina.Excel.Sheets.Aetheryte;
using ObjectKind = Dalamud.Game.ClientState.Objects.Enums.ObjectKind;
using Treasure = FFXIVClientStructs.FFXIV.Client.Game.Object.Treasure;

namespace DailyRoutines.ModulesPublic;

public unsafe partial class FastObjectInteract : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title               = GetLoc("FastObjectInteractTitle"),
        Description         = GetLoc("FastObjectInteractDescription"),
        Category            = ModuleCategories.UIOptimization,
        ModulesPrerequisite = ["FastWorldTravel", "FastInstanceZoneChange"]
    };

    public override ModulePermission Permission { get; } = new() { NeedAuth = true };

    private const string ENPC_TITLE_FORMAT = "[{0}] {1}";

    private static FrozenDictionary<uint, string> ENPCTitle     { get; } = LoadEnpcTitles();
    private static FrozenSet<uint>                ImportantENPC { get; } = LoadImportantEnpcs();

    private static readonly FrozenSet<uint> WorldTravelValidZones = new HashSet<uint> { 132, 129, 130 }.ToFrozenSet();

    private static readonly FrozenDictionary<ObjectKind, float> IncludeDistance = new Dictionary<ObjectKind, float>
    {
        [ObjectKind.Aetheryte]      = 400,
        [ObjectKind.GatheringPoint] = 100,
        [ObjectKind.CardStand]      = 150,
        [ObjectKind.EventObj]       = 100,
        [ObjectKind.Housing]        = 30,
        [ObjectKind.Treasure]       = 100
    }.ToFrozenDictionary();

    private static Config ModuleConfig = null!;
    
    private static Dictionary<uint, string> DCWorlds = [];
    
    private static string BlacklistKeyInput = string.Empty;
    private static float  WindowWidth;
    private static bool   IsUpdatingObjects;
    private static bool   IsOnWorldTraveling;
    
    private static readonly List<InteractableObject> CurrentObjects = new(20);
    private static          bool                     ForceObjectUpdate;

    private static FrozenDictionary<uint, string> LoadEnpcTitles()
    {
        return LuminaGetter.Get<ENpcResident>()
                           .Where(x => x.Unknown1 && !string.IsNullOrWhiteSpace(x.Title.ToString()))
                           .ToDictionary(x => x.RowId, x => x.Title.ToString())
                           .ToFrozenDictionary();
    }

    private static FrozenSet<uint> LoadImportantEnpcs()
    {
        return LuminaGetter.Get<ENpcResident>()
                           .Where(x => x.Unknown1)
                           .Select(x => x.RowId)
                           .ToFrozenSet();
    }

    protected override void Init()
    {
        ModuleConfig = LoadConfig<Config>() ??
                       new()
                       {
                           SelectedKinds =
                           [
                               ObjectKind.EventNpc, ObjectKind.EventObj, ObjectKind.Treasure, ObjectKind.Aetheryte,
                               ObjectKind.GatheringPoint
                           ]
                       };

        TaskHelper ??= new() { TimeoutMS = 5_000 };

        Overlay = new Overlay(this, $"{GetLoc("FastObjectInteractTitle")}")
        {
            Flags = ImGuiWindowFlags.NoScrollbar      |
                    ImGuiWindowFlags.AlwaysAutoResize |
                    ImGuiWindowFlags.NoBringToFrontOnFocus
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

        CurrentObjects.Clear();
    }
    
    private void OnUpdate(IFramework framework)
    {
        if (IsUpdatingObjects) return;

        var localPlayer    = Control.GetLocalPlayer();
        var canShowOverlay = !BetweenAreas && localPlayer != null;

        if (!canShowOverlay)
        {
            if (Overlay.IsOpen)
            {
                CurrentObjects.Clear();
                WindowWidth    = 0f;
                Overlay.IsOpen = false;
            }

            return;
        }
        
        if (ForceObjectUpdate || Throttler.Throttle("FastObjectInteract-Monitor"))
        {
            IsUpdatingObjects = true;
            ForceObjectUpdate = false;

            UpdateObjectsList((GameObject*)localPlayer);

            IsUpdatingObjects = false;
        }

        var shouldShowWindow = CurrentObjects.Count > 0 && IsWindowShouldBeOpen();
        if (Overlay != null)
        {
            Overlay.IsOpen = shouldShowWindow;
            if (!shouldShowWindow) WindowWidth = 0f;
        }
    }
    
    private static void OnLogin() => 
        LoadWorldData();

    private static void OnTerritoryChanged(ushort zoneID) => 
        ForceObjectUpdate = true;
    
    #region UI

    protected override void ConfigUI()
    {
        var changed = false;

        using var width = ImRaii.ItemWidth(300f * GlobalFontScale);
        
        changed |= ImGui.Checkbox(GetLoc("FastObjectInteract-WindowInvisibleWhenInteract"), ref ModuleConfig.WindowInvisibleWhenInteract);
        changed |= ImGui.Checkbox(GetLoc("FastObjectInteract-WindowInvisibleWhenCombat"),   ref ModuleConfig.WindowInvisibleWhenCombat);

        if (ImGui.Checkbox(GetLoc("FastObjectInteract-LockWindow"), ref ModuleConfig.LockWindow))
        {
            changed = true;
            UpdateWindowFlags();
        }

        if (ImGui.Checkbox(GetLoc("FastObjectInteract-OnlyDisplayInViewRange"), ref ModuleConfig.OnlyDisplayInViewRange))
        {
            changed           = true;
            ForceObjectUpdate = true;
        }

        changed |= ImGui.Checkbox(GetLoc("FastObjectInteract-AllowClickToTarget"), ref ModuleConfig.AllowClickToTarget);
        
        ImGui.NewLine();
        
        ImGui.InputFloat($"{GetLoc("FontScale")}##FontScaleInput", ref ModuleConfig.FontScale, format: "%.1f");
        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            changed = true;
            
            ModuleConfig.FontScale = Math.Max(0.1f, ModuleConfig.FontScale);
        }

        ImGui.InputFloat($"{GetLoc("FastObjectInteract-MinButtonWidth")}##MinButtonWidthInput", ref ModuleConfig.MinButtonWidth, format: "%.1f");
        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            changed = true;

            ValidateButtonWidthSettings();
        }
        
        ImGui.InputFloat($"{GetLoc("FastObjectInteract-MaxButtonWidth")}##MaxButtonWidthInput", ref ModuleConfig.MaxButtonWidth, format: "%.1f");
        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            changed = true;

            ValidateButtonWidthSettings();
        }

        ImGui.InputInt($"{GetLoc("FastObjectInteract-MaxDisplayAmount")}##MaxDisplayAmountInput", ref ModuleConfig.MaxDisplayAmount);
        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            changed = true;
            
            ModuleConfig.MaxDisplayAmount = Math.Max(1, ModuleConfig.MaxDisplayAmount);
        }
        
        using (var combo = ImRaii.Combo
        (
            $"{GetLoc("FastObjectInteract-SelectedObjectKinds")}##ObjectKindsSelection",
            GetLoc("FastObjectInteract-SelectedObjectKindsAmount", ModuleConfig.SelectedKinds.Count),
            ImGuiComboFlags.HeightLarge
        ))
        {
            if (combo)
            {
                foreach (var kind in Enum.GetValues<ObjectKind>())
                {
                    var state = ModuleConfig.SelectedKinds.Contains(kind);

                    if (ImGui.Checkbox(kind.ToString(), ref state))
                    {
                        changed = true;
                        
                        if (state)
                            ModuleConfig.SelectedKinds.Add(kind);
                        else
                            ModuleConfig.SelectedKinds.Remove(kind);
                        
                        ForceObjectUpdate = true;
                    }
                }
            }
        }
        
        using (var combo = ImRaii.Combo
               (
                   $"{GetLoc("FastObjectInteract-BlacklistKeysList")}##BlacklistObjectsSelection",
                   GetLoc("FastObjectInteract-BlacklistKeysListAmount", ModuleConfig.BlacklistKeys.Count),
                   ImGuiComboFlags.HeightLarge
               ))
        {
            if (combo)
            {
                if (ImGuiOm.ButtonIcon("###BlacklistKeyInputAdd", FontAwesomeIcon.Plus, GetLoc("Add")))
                {
                    if (ModuleConfig.BlacklistKeys.Add(BlacklistKeyInput))
                    {
                        SaveConfig(ModuleConfig);
                        ForceObjectUpdate = true;
                    }
                }

                ImGui.SameLine();
                ImGui.SetNextItemWidth(-1);
                ImGui.InputTextWithHint("###BlacklistKeyInput", $"{GetLoc("FastObjectInteract-BlacklistKeysListInputHelp")}", ref BlacklistKeyInput, 100);

                ImGui.Spacing();
                ImGui.Separator();
                ImGui.Spacing();

                var listToRemove = new List<string>();
                foreach (var key in ModuleConfig.BlacklistKeys.ToList())
                {
                    if (ImGuiOm.ButtonIcon(key, FontAwesomeIcon.TrashAlt, GetLoc("Delete")))
                    {
                        changed = true;

                        listToRemove.Add(key);
                        ForceObjectUpdate = true;
                    }

                    ImGui.SameLine();
                    ImGui.TextUnformatted(key);
                }

                if (listToRemove.Count > 0)
                    ModuleConfig.BlacklistKeys.RemoveRange(listToRemove);
            }
        }

        if (changed) 
            SaveConfig(ModuleConfig);
    }
    
    protected override void OverlayUI()
    {
        using var fontPush = FontManager.Instance().GetUIFont(ModuleConfig.FontScale).Push();

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

        WindowWidth = Math.Clamp(ImGui.GetItemRectSize().X, ModuleConfig.MinButtonWidth, ModuleConfig.MaxButtonWidth);
    }

    private static void ValidateButtonWidthSettings()
    {
        if (ModuleConfig.MinButtonWidth >= ModuleConfig.MaxButtonWidth)
        {
            ModuleConfig.MinButtonWidth = 300f;
            ModuleConfig.MaxButtonWidth = 350f;
        }

        ModuleConfig.MinButtonWidth = Math.Max(1, ModuleConfig.MinButtonWidth);
        ModuleConfig.MaxButtonWidth = Math.Max(1, ModuleConfig.MaxButtonWidth);
    }
    
    private void RenderObjectButtons(out InteractableObject? instanceChangeObject, out InteractableObject? worldTravelObject)
    {
        instanceChangeObject = null;
        worldTravelObject    = null;

        using var group = ImRaii.Group();


        foreach (var obj in CurrentObjects)
        {
            if (obj.Pointer == null) continue;


            if (InstancesManager.IsInstancedArea && obj.Kind == ObjectKind.Aetheryte)
            {
                if (LuminaGetter.GetRow<Aetheryte>(obj.Pointer->BaseId) is { IsAetheryte: true })
                    instanceChangeObject = obj;
            }

            if (!IsOnWorldTraveling                                     &&
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
        var clickToTarget = ModuleConfig.AllowClickToTarget;
        
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
                if (ImGui.MenuItem(GetLoc("FastObjectInteract-AddToBlacklist")))
                {
                    var cleanName = FastObjectInteractTitleRegex().Replace(obj.Name, string.Empty).Trim();

                    if (ModuleConfig.BlacklistKeys.Add(cleanName))
                    {
                        SaveConfig(ModuleConfig);
                        ForceObjectUpdate = true;
                    }
                }
            }
        }
    }

    private static void RenderInstanceZoneChangeButtons()
    {

        for (var i = 1; i <= InstancesManager.Instance().GetInstancesCount(); i++)
        {
            if (i == InstancesManager.CurrentInstance) continue;
            if (ButtonCenterText($"InstanceChangeWidget_{i}", GetLoc("FastObjectInteract-InstanceAreaChange", i)))
                ChatManager.Instance().SendMessage($"/pdr insc {i}");
        }
    }

    private static void RenderWorldChangeButtons()
    {
        using var disabled = ImRaii.Disabled(IsOnWorldTraveling);

        foreach (var worldPair in DCWorlds)
        {
            if (worldPair.Key == GameState.CurrentWorld) continue;
            if (ButtonCenterText($"WorldTravelWidget_{worldPair.Key}", $"{worldPair.Value}{(worldPair.Key == GameState.HomeWorld ? " (★)" : "")}"))
                ChatManager.Instance().SendMessage($"/pdr worldtravel {worldPair.Key}");
        }
    }

    public static bool ButtonCenterText(string id, string text)
    {
        using var idPush = ImRaii.PushId($"{id}_{text}");

        var textSize    = ImGui.CalcTextSize(text);
        var cursorPos   = ImGui.GetCursorScreenPos();
        var padding     = ImGui.GetStyle().FramePadding;
        var buttonWidth = Math.Clamp(textSize.X + padding.X * 2, WindowWidth, ModuleConfig.MaxButtonWidth);
        var result      = ImGui.Button(string.Empty, new Vector2(buttonWidth, textSize.Y + padding.Y * 2));

        ImGuiOm.TooltipHover(text);

        ImGui.GetWindowDrawList()
             .AddText(new(cursorPos.X + (buttonWidth - textSize.X) / 2, cursorPos.Y + padding.Y), ImGui.GetColorU32(ImGuiCol.Text), text);

        return result;
    }

    #endregion
    
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void UpdateObjectsList(GameObject* localPlayer)
    {
        CurrentObjects.Clear();

        var mgr = GameObjectManager.Instance();
        if (mgr == null) return;

        IsOnWorldTraveling = DService.Instance().Condition.Any
        (
            ConditionFlag.ReadyingVisitOtherWorld,
            ConditionFlag.WaitingToVisitOtherWorld
        );

        var playerPos = localPlayer->Position;
        var maxAmount = ModuleConfig.MaxDisplayAmount;

        for (var i = 200; i < mgr->Objects.IndexSorted.Length; i++)
        {
            var objPtr = mgr->Objects.IndexSorted[i];
            if (objPtr == null) continue;

            var obj = objPtr.Value;
            if (obj == null) continue;

            if (!obj->GetIsTargetable()) continue;
            
            var kind = (ObjectKind)obj->ObjectKind;
            if (!ModuleConfig.SelectedKinds.Contains(kind)) continue;
            
            var distSq = Vector3.DistanceSquared(playerPos, obj->Position);
            
            var limit = 400f;
            if (IncludeDistance.TryGetValue(kind, out var l)) 
                limit = l;

            if (distSq > limit) continue;
            
            if (MathF.Abs(obj->Position.Y - playerPos.Y) >= 4) continue;

            if (kind == ObjectKind.Treasure)
            {
                var treasure = (Treasure*)obj;
                if (treasure->Flags.IsSetAny(Treasure.TreasureFlags.FadedOut, Treasure.TreasureFlags.Opened))
                    continue;
            }

            var name = obj->NameString;
            if (string.IsNullOrEmpty(name)) continue;

            if (ModuleConfig.BlacklistKeys.Contains(name)) continue;

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
            
            if (ModuleConfig.OnlyDisplayInViewRange)
            {
                if (!DService.Instance().GameGUI.WorldToScreen(obj->Position, out _))
                    continue;
            }
            
            CurrentObjects.Add(new(obj, name, kind, distSq));
        }
        
        CurrentObjects.Sort(InteractableObjectComparer.Instance);

        if (CurrentObjects.Count > maxAmount)
            CurrentObjects.RemoveRange(maxAmount, CurrentObjects.Count - maxAmount);
    }
    
    private void UpdateWindowFlags()
    {
        if (Overlay == null) return;
        if (ModuleConfig.LockWindow)
            Overlay.Flags |= ImGuiWindowFlags.NoMove;
        else
            Overlay.Flags &= ~ImGuiWindowFlags.NoMove;
    }
    
    private static bool IsWindowShouldBeOpen()
    {
        if (CurrentObjects.Count == 0) return false;
        
        if (ModuleConfig.WindowInvisibleWhenInteract && OccupiedInEvent) 
            return false;

        if (ModuleConfig.WindowInvisibleWhenCombat && DService.Instance().Condition[ConditionFlag.InCombat])
            return false;

        return true;
    }

    private void InteractWithObject(GameObject* obj, ObjectKind kind)
    {
        TaskHelper.RemoveQueueTasks(2);

        if (IsOnMount)
            TaskHelper.Enqueue(() => MovementManager.Dismount(), "DismountInteract", weight: 2);

        TaskHelper.Enqueue
        (
            () =>
            {
                if (IsOnMount || DService.Instance().Condition[ConditionFlag.Jumping] || MovementManager.IsManagerBusy) return false;

                TargetSystem.Instance()->Target = obj;
                return TargetSystem.Instance()->InteractWithObject(obj) != 0;
            },
            "Interact",
            weight: 2
        );

        if (kind is ObjectKind.EventObj)
            TaskHelper.Enqueue(() => TargetSystem.Instance()->OpenObjectInteraction(obj), "OpenInteraction", weight: 2);
    }

    private static void LoadWorldData()
    {
        if (!GameState.IsLoggedIn) return;

        DCWorlds = PresetSheet.Worlds
                              .Where(x => x.Value.DataCenter.RowId == GameState.CurrentDataCenter)
                              .OrderBy(x => x.Key                  == GameState.HomeWorld)
                              .ThenBy(x => x.Value.Name.ToString())
                              .ToDictionary(x => x.Key, x => x.Value.Name.ToString());
    }
    
    private sealed class Config : ModuleConfiguration
    {
        public HashSet<string>     BlacklistKeys = [];
        public HashSet<ObjectKind> SelectedKinds = [];

        public bool  AllowClickToTarget;
        public float FontScale = 1f;
        public bool  LockWindow;
        public int   MaxDisplayAmount = 5;
        public float MinButtonWidth   = 300f;
        public float MaxButtonWidth   = 400f;
        public bool  OnlyDisplayInViewRange;
        public bool  WindowInvisibleWhenInteract = true;
        public bool  WindowInvisibleWhenCombat   = true;
    }

    [GeneratedRegex(@"\[.*?\]")]
    private static partial Regex FastObjectInteractTitleRegex();
    
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
}
