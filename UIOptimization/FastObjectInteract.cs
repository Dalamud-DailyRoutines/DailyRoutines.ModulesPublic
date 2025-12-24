using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Text.RegularExpressions;
using DailyRoutines.Abstracts;
using DailyRoutines.Infos;
using DailyRoutines.Managers;
using DailyRoutines.Windows;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Lumina.Excel.Sheets;
using ObjectKind = Dalamud.Game.ClientState.Objects.Enums.ObjectKind;
using Treasure = FFXIVClientStructs.FFXIV.Client.Game.Object.Treasure;

namespace DailyRoutines.Modules;

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

    private const string EnpcTitleFormat        = "[{0}] {1}";
    private const int    WorldTravelingStatusID = 25;
    private const int    InteractCheckType      = 23;

    private static readonly Dictionary<uint, string> EnpcTitles;
    private static readonly HashSet<uint>            ImportantEnpcs;
    private static readonly HashSet<uint>            WorldTravelValidZones = [132, 129, 130];
    private static readonly string                   AethernetShardName;

    private static readonly Dictionary<ObjectKind, float> IncludeDistance = new()
    {
        [ObjectKind.Aetheryte]      = 400,
        [ObjectKind.GatheringPoint] = 100,
        [ObjectKind.CardStand]      = 150,
        [ObjectKind.EventObj]       = 100,
        [ObjectKind.Housing]        = 30,
        [ObjectKind.Treasure]       = 100
    };

    private static          Config                   ModuleConfig      = null!;
    private static          Dictionary<uint, string> DCWorlds          = [];
    private static readonly Throttler<string>        MonitorThrottler  = new();
    private static          string                   BlacklistKeyInput = string.Empty;
    private static          float                    WindowWidth;
    private static          bool                     IsUpdatingObjects;
    private static          bool                     IsOnWorldTraveling;
    private static          uint                     HomeWorld;
    
    private static readonly List<ObjectToSelect>             TempObjects     = new(100);
    private static readonly Dictionary<nint, ObjectToSelect> ObjectsToSelect = [];
    
    private static bool ForceObjectUpdate;

    static FastObjectInteract()
    {
        EnpcTitles = LuminaGetter.Get<ENpcResident>()
                                .Where(x => x.Unknown1 && !string.IsNullOrWhiteSpace(x.Title.ExtractText()))
                                .ToDictionary(x => x.RowId, x => x.Title.ExtractText());

        ImportantEnpcs = LuminaGetter.Get<ENpcResident>()
                                   .Where(x => x.Unknown1)
                                   .Select(x => x.RowId)
                                   .ToHashSet();

        AethernetShardName = LuminaGetter.GetRow<EObjName>(2000151)!.Value.Singular.ExtractText();
    }

    protected override void Init()
    {
        ModuleConfig = LoadConfig<Config>() ?? new()
        {
            SelectedKinds =
            [
                ObjectKind.EventNpc, ObjectKind.EventObj, ObjectKind.Treasure, ObjectKind.Aetheryte,
                ObjectKind.GatheringPoint,
            ],
        };

        TaskHelper ??= new() { TimeLimitMS = 5_000 };

        Overlay = new Overlay(this, $"{GetLoc("FastObjectInteractTitle")}")
        {
            Flags = ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.AlwaysAutoResize |
                    ImGuiWindowFlags.NoBringToFrontOnFocus
        };

        if (ModuleConfig.LockWindow) 
            Overlay.Flags |= ImGuiWindowFlags.NoMove;
        else 
            Overlay.Flags &= ~ImGuiWindowFlags.NoMove;

        DService.ClientState.Login += OnLogin;
        DService.ClientState.TerritoryChanged += OnTerritoryChanged;
        FrameworkManager.Reg(OnUpdate, throttleMS: 250);

        LoadWorldData();
    }

    private static void OnLogin() => 
        LoadWorldData();

    private static void OnTerritoryChanged(ushort zoneID) => 
        ForceObjectUpdate = true;

    private static void LoadWorldData()
    {
        var agent = AgentLobby.Instance();
        if (agent == null) return;

        HomeWorld = agent->LobbyData.HomeWorldId;
        var currentWorld = agent->LobbyData.CurrentWorldId;
        if (HomeWorld <= 0 || currentWorld <= 0) return;

        if (!LuminaGetter.TryGetRow<World>(currentWorld, out var worldRow)) return;
        var dataCenter = worldRow.DataCenter.RowId;
        if (dataCenter <= 0) return;

        DCWorlds = PresetSheet.Worlds
                              .Where(x => x.Value.DataCenter.RowId == dataCenter)
                              .OrderBy(x => x.Key                  == HomeWorld)
                              .ThenBy(x => x.Value.Name.ExtractText())
                              .ToDictionary(x => x.Key, x => x.Value.Name.ExtractText());
    }

    #region 界面

    protected override void ConfigUI()
    {
        RenderFontScaleSettings();
        RenderButtonWidthSettings();
        RenderMaxDisplayAmountSetting();
        RenderObjectKindsSelection();
        RenderBlacklistSettings();
        RenderWindowOptions();
    }
    
    private void RenderFontScaleSettings()
    {
        ImGui.AlignTextToFramePadding();
        ImGui.Text($"{GetLoc("FontScale")}:");

        ImGui.SameLine();
        ImGui.SetNextItemWidth(80f * GlobalFontScale);
        ImGui.InputFloat("###FontScaleInput", ref ModuleConfig.FontScale, 0f, 0f,
                         ModuleConfig.FontScale.ToString(CultureInfo.InvariantCulture));

        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            ModuleConfig.FontScale = Math.Max(0.1f, ModuleConfig.FontScale);
            SaveConfig(ModuleConfig);
        }
    }
    
    private void RenderButtonWidthSettings()
    {
        ImGui.AlignTextToFramePadding();
        ImGui.Text($"{GetLoc("FastObjectInteract-MinButtonWidth")}:");

        ImGui.SameLine();
        ImGui.SetNextItemWidth(80f * GlobalFontScale);
        ImGui.InputFloat("###MinButtonWidthInput", ref ModuleConfig.MinButtonWidth, 0, 0,
                         ModuleConfig.MinButtonWidth.ToString(CultureInfo.InvariantCulture));

        if (ImGui.IsItemDeactivatedAfterEdit())
            ValidateButtonWidthSettings();

        ImGui.AlignTextToFramePadding();
        ImGui.Text($"{GetLoc("FastObjectInteract-MaxButtonWidth")}:");

        ImGui.SameLine();
        ImGui.SetNextItemWidth(80f * GlobalFontScale);
        ImGui.InputFloat("###MaxButtonWidthInput", ref ModuleConfig.MaxButtonWidth, 0, 0,
                         ModuleConfig.MaxButtonWidth.ToString(CultureInfo.InvariantCulture));

        if (ImGui.IsItemDeactivatedAfterEdit())
            ValidateButtonWidthSettings();
    }
    
    private void ValidateButtonWidthSettings()
    {
        if (ModuleConfig.MinButtonWidth >= ModuleConfig.MaxButtonWidth)
        {
            ModuleConfig.MinButtonWidth = 300f;
            ModuleConfig.MaxButtonWidth = 350f;
        }
        
        ModuleConfig.MinButtonWidth = Math.Max(1, ModuleConfig.MinButtonWidth);
        ModuleConfig.MaxButtonWidth = Math.Max(1, ModuleConfig.MaxButtonWidth);
        SaveConfig(ModuleConfig);
    }
    
    private void RenderMaxDisplayAmountSetting()
    {
        ImGui.AlignTextToFramePadding();
        ImGui.Text($"{GetLoc("FastObjectInteract-MaxDisplayAmount")}:");

        ImGui.SameLine();
        ImGui.SetNextItemWidth(80f * GlobalFontScale);
        ImGui.InputInt("###MaxDisplayAmountInput", ref ModuleConfig.MaxDisplayAmount);
        
        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            ModuleConfig.MaxDisplayAmount = Math.Max(1, ModuleConfig.MaxDisplayAmount);
            SaveConfig(ModuleConfig);
        }
    }
    
    private void RenderObjectKindsSelection()
    {
        ImGui.AlignTextToFramePadding();
        ImGui.Text($"{GetLoc("FastObjectInteract-SelectedObjectKinds")}:");

        ImGui.SameLine();
        ImGui.SetNextItemWidth(300f * GlobalFontScale);
        using var combo = ImRaii.Combo("###ObjectKindsSelection", GetLoc("FastObjectInteract-SelectedObjectKindsAmount", ModuleConfig.SelectedKinds.Count),
                                       ImGuiComboFlags.HeightLarge);
        if (!combo) return;
        
        foreach (var kind in Enum.GetValues<ObjectKind>())
        {
            var state = ModuleConfig.SelectedKinds.Contains(kind);
            if (ImGui.Checkbox(kind.ToString(), ref state))
            {
                if (!ModuleConfig.SelectedKinds.Remove(kind))
                    ModuleConfig.SelectedKinds.Add(kind);

                SaveConfig(ModuleConfig);
                ForceObjectUpdate = true;
            }
        }
    }
    
    private void RenderBlacklistSettings()
    {
        ImGui.AlignTextToFramePadding();
        ImGui.Text($"{GetLoc("FastObjectInteract-BlacklistKeysList")}:");

        ImGui.SameLine();
        ImGui.SetNextItemWidth(300f * GlobalFontScale);
        using var combo = ImRaii.Combo("###BlacklistObjectsSelection", GetLoc("FastObjectInteract-BlacklistKeysListAmount", ModuleConfig.BlacklistKeys.Count),
                                       ImGuiComboFlags.HeightLarge);
        if (!combo) return;
        
        ImGui.SetNextItemWidth(250f * GlobalFontScale);
        ImGui.InputTextWithHint("###BlacklistKeyInput", $"{GetLoc("FastObjectInteract-BlacklistKeysListInputHelp")}", ref BlacklistKeyInput, 100);

        ImGui.SameLine();
        if (ImGuiOm.ButtonIcon("###BlacklistKeyInputAdd", FontAwesomeIcon.Plus, GetLoc("Add")))
        {
            if (!ModuleConfig.BlacklistKeys.Add(BlacklistKeyInput)) return;

            SaveConfig(ModuleConfig);
            ForceObjectUpdate = true;
        }

        ImGui.Separator();

        foreach (var key in ModuleConfig.BlacklistKeys)
        {
            if (ImGuiOm.ButtonIcon(key, FontAwesomeIcon.TrashAlt, GetLoc("Delete")))
            {
                ModuleConfig.BlacklistKeys.Remove(key);
                SaveConfig(ModuleConfig);
                ForceObjectUpdate = true;
            }

            ImGui.SameLine();
            ImGui.Text(key);
        }
    }

    private void RenderWindowOptions()
    {
        if (ImGui.Checkbox(GetLoc("FastObjectInteract-WindowInvisibleWhenInteract"), ref ModuleConfig.WindowInvisibleWhenInteract))
            SaveConfig(ModuleConfig);

        if (ImGui.Checkbox(GetLoc("FastObjectInteract-WindowInvisibleWhenCombat"), ref ModuleConfig.WindowVisibleWhenCombat))
            SaveConfig(ModuleConfig);

        if (ImGui.Checkbox(GetLoc("FastObjectInteract-LockWindow"), ref ModuleConfig.LockWindow))
        {
            SaveConfig(ModuleConfig);

            if (ModuleConfig.LockWindow)
                Overlay.Flags |= ImGuiWindowFlags.NoMove;
            else
                Overlay.Flags &= ~ImGuiWindowFlags.NoMove;
        }

        if (ImGui.Checkbox(GetLoc("FastObjectInteract-OnlyDisplayInViewRange"), ref ModuleConfig.OnlyDisplayInViewRange))
        {
            SaveConfig(ModuleConfig);
            ForceObjectUpdate = true;
        }

        if (ImGui.Checkbox(GetLoc("FastObjectInteract-AllowClickToTarget"), ref ModuleConfig.AllowClickToTarget))
            SaveConfig(ModuleConfig);
    }

    protected override void OverlayUI()
    {
        using var fontPush = FontManager.GetUIFont(ModuleConfig.FontScale).Push();
        
        RenderObjectButtons(out var instanceChangeObject, out var worldTravelObject);
        
        if (instanceChangeObject != null || worldTravelObject != null)
        {
            ImGui.SameLine();
            using (ImRaii.Group())
            {
                if (instanceChangeObject != null)
                    RenderInstanceZoneChangeButtons();

                if (worldTravelObject != null)
                    RenderWorldChangeButtons();
            }
        }

        WindowWidth = Math.Clamp(ImGui.GetItemRectSize().X, ModuleConfig.MinButtonWidth, ModuleConfig.MaxButtonWidth);
    }
    
    private void RenderObjectButtons(out ObjectToSelect? instanceChangeObject, out ObjectToSelect? worldTravelObject)
    {
        instanceChangeObject = null;
        worldTravelObject    = null;

        using var group = ImRaii.Group();
        
        foreach (var objectToSelect in ObjectsToSelect.Values.ToList())
        {
            if (objectToSelect.GameObject == nint.Zero) continue;

            if (InstancesManager.IsInstancedArea && objectToSelect.Kind == ObjectKind.Aetheryte)
            {
                var gameObj = (GameObject*)objectToSelect.GameObject;
                if (gameObj->NameString != AethernetShardName)
                    instanceChangeObject = objectToSelect;
            }

            if (!IsOnWorldTraveling && WorldTravelValidZones.Contains(DService.ClientState.TerritoryType) &&
                objectToSelect.Kind == ObjectKind.Aetheryte)
            {
                var gameObj = (GameObject*)objectToSelect.GameObject;
                if (gameObj->NameString != AethernetShardName)
                    worldTravelObject = objectToSelect;
            }

            bool configChanged;
            if (ModuleConfig.AllowClickToTarget)
                configChanged = objectToSelect.ButtonToTarget();
            else
                configChanged = objectToSelect.ButtonNoTarget();

            if (configChanged)
                SaveConfig(ModuleConfig);
        }
    }
    
    private void RenderInstanceZoneChangeButtons()
    {
        using var group = ImRaii.Group();
        
        for (var i = 1; i <= InstancesManager.GetInstancesCount(); i++)
        {
            if (i == InstancesManager.CurrentInstance) continue;

            if (ButtonCenterText($"InstanceChangeWidget_{i}", GetLoc("FastObjectInteract-InstanceAreaChange", i)))
                ChatManager.SendMessage($"/pdr insc {i}");
        }
    }

    private void RenderWorldChangeButtons()
    {
        var       lobbyData = AgentLobby.Instance()->LobbyData;
        using var group     = ImRaii.Group();
        using var disable   = ImRaii.Disabled(IsOnWorldTraveling);
        
        foreach (var worldPair in DCWorlds)
        {
            if (worldPair.Key == lobbyData.CurrentWorldId) continue;

            if (ButtonCenterText($"WorldTravelWidget_{worldPair.Key}", $"{worldPair.Value}{(worldPair.Key == HomeWorld ? " (★)" : "")}"))
                ChatManager.SendMessage($"/pdr worldtravel {worldPair.Key}");
        }
    }
    
    public static bool ButtonCenterText(string id, string text)
    {
        using var idPush = ImRaii.PushId($"{id}_{text}");

        var textSize    = ImGui.CalcTextSize(text);
        var cursorPos   = ImGui.GetCursorScreenPos();
        var padding     = ImGui.GetStyle().FramePadding;
        var buttonWidth = Math.Clamp(textSize.X + (padding.X * 2), WindowWidth, ModuleConfig.MaxButtonWidth);
        var result      = ImGui.Button(string.Empty, new Vector2(buttonWidth, textSize.Y + (padding.Y * 2)));
        
        ImGuiOm.TooltipHover(text);

        ImGui.GetWindowDrawList()
             .AddText(new(cursorPos.X + ((buttonWidth - textSize.X) / 2), cursorPos.Y + padding.Y), ImGui.GetColorU32(ImGuiCol.Text), text);
        
        return result;
    }

    #endregion
    
    private void InteractWithObject(GameObject* obj, ObjectKind kind)
    {
        TaskHelper.RemoveAllTasks(2);

        if (IsOnMount)
            TaskHelper.Enqueue(() => MovementManager.Dismount(), "DismountInteract", null, null, 2);

        TaskHelper.Enqueue(() =>
        {
            if (IsOnMount || DService.Condition[ConditionFlag.Jumping] || MovementManager.IsManagerBusy) return false;
            
            TargetSystem.Instance()->Target = obj;
            return TargetSystem.Instance()->InteractWithObject(obj) != 0;
        }, "Interact", null, null, 2);

        if (kind is ObjectKind.EventObj)
            TaskHelper.Enqueue(() => TargetSystem.Instance()->OpenObjectInteraction(obj), "OpenInteraction", null, null, 2);
    }
    
    private void OnUpdate(IFramework framework)
    {
        try
        {
            var shouldUpdateObjects = ForceObjectUpdate || MonitorThrottler.Throttle("Monitor");
            var localPlayer         = DService.ObjectTable.LocalPlayer;
            var canShowOverlay      = !BetweenAreas && localPlayer != null;
            
            if (!canShowOverlay)
            {
                if (Overlay.IsOpen)
                {
                    ObjectsToSelect.Clear();
                    WindowWidth = 0f;
                    Overlay.IsOpen = false;
                }
                
                return;
            }
            
            var shouldShowWindow = ObjectsToSelect.Count > 0 && IsWindowShouldBeOpen();
            
            if (Overlay != null)
            {
                Overlay.IsOpen = shouldShowWindow;
                if (!shouldShowWindow)
                    WindowWidth = 0f;
            }
            
            if (!shouldUpdateObjects || IsUpdatingObjects) return;
            
            IsUpdatingObjects = true;
            ForceObjectUpdate = false;

            try
            {
                UpdateObjectsList(localPlayer);
            }
            finally
            {
                IsUpdatingObjects = false;
            }
        }
        catch
        {
            IsUpdatingObjects = false;
        }

        return;

        bool IsWindowShouldBeOpen()
            => ObjectsToSelect.Count != 0                                      &&
               (!ModuleConfig.WindowInvisibleWhenInteract || !OccupiedInEvent) &&
               (!ModuleConfig.WindowVisibleWhenCombat     || DService.ClientState.IsPvPExcludingDen || !DService.Condition[ConditionFlag.InCombat]);
    }

    private static void UpdateObjectsList(IPlayerCharacter localPlayer)
    {
        TempObjects.Clear();
        
        if (TempObjects.Capacity < ModuleConfig.MaxDisplayAmount)
            TempObjects.Capacity = ModuleConfig.MaxDisplayAmount;
        
        IsOnWorldTraveling = localPlayer.OnlineStatus.RowId == WorldTravelingStatusID;
        
        var objectFilter = new GameObjectFilter(ModuleConfig.SelectedKinds, 
                                                ModuleConfig.BlacklistKeys, 
                                                ModuleConfig.OnlyDisplayInViewRange);

        var filteredObjects = DService.ObjectTable
                                      .Where(obj => objectFilter.ShouldIncludeObject(obj))
                                      .Take(ModuleConfig.MaxDisplayAmount * 2); // 预先多取一些以确保有足够的有效对象
        
        var furthestDistance = float.MaxValue;
        foreach (var obj in filteredObjects)
        {
            var gameObj = obj.ToStruct();
            if (gameObj == null) continue;
            
            var dataID = obj.DataID;
            var objName = obj.Name.TextValue;
            var objKind = obj.ObjectKind;
            
            if (objKind == ObjectKind.EventNpc && ImportantEnpcs.Contains(dataID))
            {
                if (EnpcTitles.TryGetValue(dataID, out var enpcTitle))
                    objName = string.Format(EnpcTitleFormat, enpcTitle, obj.Name);
            }
            
            var objDistance = Vector3.DistanceSquared(localPlayer.Position, obj.Position);
            var maxDistance = IncludeDistance.GetValueOrDefault(obj.ObjectKind, 400);
            if (objDistance >= maxDistance || MathF.Abs(obj.Position.Y - localPlayer.Position.Y) >= 4)
                continue;
            if (TempObjects.Count >= ModuleConfig.MaxDisplayAmount && objDistance >= furthestDistance)
                continue;
            
            var objectToSelect   = new ObjectToSelect((nint)gameObj, objName, objKind, objDistance);
            var index            = TempObjects.BinarySearch(objectToSelect, OptimizedObjectDistanceComparer.Instance);
            if (index < 0) 
                index = ~index;
            
            if (TempObjects.Count < ModuleConfig.MaxDisplayAmount)
            {
                TempObjects.Insert(index, objectToSelect);
                if (TempObjects.Count == ModuleConfig.MaxDisplayAmount)
                    furthestDistance = TempObjects[^1].Distance;
            }
            else if (index < TempObjects.Count)
            {
                TempObjects.RemoveAt(TempObjects.Count - 1);
                TempObjects.Insert(index, objectToSelect);

                furthestDistance = TempObjects[^1].Distance;
            }
        }
        
        ObjectsToSelect.Clear();
        foreach (var obj in TempObjects)
            ObjectsToSelect.Add(obj.GameObject, obj);
    }
    
    private class GameObjectFilter(
        HashSet<ObjectKind> SelectedKinds,
        HashSet<string>     BlacklistKeys,
        bool                CheckViewRange)
    {
        public bool ShouldIncludeObject(IGameObject obj)
        {
            if (!obj.IsTargetable || obj.IsDead || !obj.IsValid()) return false;
            
            var objKind = obj.ObjectKind;
            if (!SelectedKinds.Contains(objKind)) return false;
            
            var gameObj = obj.ToStruct();
            if (gameObj == null) return false;
            
            var objName = obj.Name.TextValue;
            if (BlacklistKeys.Contains(objName)) return false;
            
            if (objKind == ObjectKind.EventNpc && !ImportantEnpcs.Contains(obj.DataID) && gameObj->NamePlateIconId == 0)
                return false;

            if (objKind == ObjectKind.Treasure &&
                (((Treasure*)obj.ToStruct())->Flags.HasFlag(Treasure.TreasureFlags.FadedOut) ||
                 ((Treasure*)obj.ToStruct())->Flags.HasFlag(Treasure.TreasureFlags.Opened)))
                return false;
            
            if (CheckViewRange && !DService.Gui.WorldToScreen(gameObj->Position, out _))
                return false;
            
            return true;
        }
    }

    protected override void Uninit()
    {
        FrameworkManager.Unreg(OnUpdate);
        DService.ClientState.Login -= OnLogin;
        DService.ClientState.TerritoryChanged -= OnTerritoryChanged;
        
        ObjectsToSelect.Clear();
        TempObjects.Clear();
    }

    private sealed class ObjectToSelect(nint gameObject, string name, ObjectKind kind, float distance)
        : IEquatable<ObjectToSelect>
    {
        public nint       GameObject { get; } = gameObject;
        public string     Name       { get; } = name;
        public ObjectKind Kind       { get; } = kind;
        public float      Distance   { get; } = distance;

        public bool ButtonToTarget()
        {
            var colors      = ImGui.GetStyle().Colors;
            var isReachable = IsReachable();

            using (ImRaii.PushStyle(ImGuiStyleVar.Alpha, 0.5f, !isReachable))
            using (ImRaii.PushColor(ImGuiCol.ButtonActive, colors[(int)ImGuiCol.HeaderActive], !isReachable))
            using (ImRaii.PushColor(ImGuiCol.ButtonHovered, colors[(int)ImGuiCol.HeaderHovered], !isReachable))
                ButtonCenterText(GameObject.ToString(), Name);

            if (ImGui.IsItemClicked(ImGuiMouseButton.Left))
            {
                if (isReachable)
                    ModuleManager.GetModule<FastObjectInteract>()?.InteractWithObject((GameObject*)GameObject, Kind);
                else
                    TargetSystem.Instance()->Target = (GameObject*)GameObject;
            }

            return ShowContextMenu();
        }

        public bool ButtonNoTarget()
        {
            var isReachable = IsReachable();
            
            using (ImRaii.Disabled(!isReachable))
            {
                if (ButtonCenterText(GameObject.ToString(), Name) && isReachable)
                    ModuleManager.GetModule<FastObjectInteract>()?.InteractWithObject((GameObject*)GameObject, Kind);
            }

            return ShowContextMenu();
        }
        
        private bool ShowContextMenu()
        {
            var       state   = false;
            using var context = ImRaii.ContextPopupItem($"{GameObject}_{Name}");
            if (!context) return state;
            
            if (ImGui.MenuItem(GetLoc("FastObjectInteract-AddToBlacklist")))
            {
                var cleanName = FastObjectInteractTitleRegex().Replace(Name, string.Empty).Trim();
                if (ModuleConfig.BlacklistKeys.Add(cleanName))
                {
                    state             = true;
                    ForceObjectUpdate = true;
                }
            }

            return state;
        }
        
        public bool IsReachable()
        {
            var localPlayer   = (GameObject*)Control.GetLocalPlayer();
            var currentObject = (GameObject*)GameObject;
            if (localPlayer == null || currentObject == null) return false;
            
            return EventFramework.Instance()->CheckInteractRange(localPlayer, currentObject, InteractCheckType, false);
        }


        public bool Equals(ObjectToSelect? other)
        {
            if (other is null) return false;
            return GameObject == other.GameObject;
        }

        public override bool Equals(object? obj) => Equals(obj as ObjectToSelect);
        
        public override int GetHashCode() => GameObject.GetHashCode();
    }
    
    private readonly struct OptimizedObjectDistanceComparer : IComparer<ObjectToSelect>
    {
        public int Compare(ObjectToSelect? x, ObjectToSelect? y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (x is null) return -1;
            if (y is null) return 1;

            var xDistance = x.Distance;
            var yDistance = y.Distance;
            
            if (xDistance < yDistance) return -1;
            if (xDistance > yDistance) return 1;
            
            return GetObjectTypePriority(x.Kind).CompareTo(GetObjectTypePriority(y.Kind));
        }

        private static int GetObjectTypePriority(ObjectKind kind) => kind switch
        {
            ObjectKind.Aetheryte      => 1,
            ObjectKind.EventNpc       => 2,
            ObjectKind.EventObj       => 3,
            ObjectKind.Treasure       => 4,
            ObjectKind.GatheringPoint => 5,
            _                         => 10
        };

        public static readonly OptimizedObjectDistanceComparer Instance = new();
    }
    
    private sealed class Config : ModuleConfiguration
    {
        public HashSet<string> BlacklistKeys = [];
        public HashSet<ObjectKind> SelectedKinds  = [];

        public bool  AllowClickToTarget;
        public float FontScale = 1f;
        public bool  LockWindow;          
        public int   MaxDisplayAmount = 5;
        public float MinButtonWidth   = 300f;
        public float MaxButtonWidth   = 400f;
        public bool  OnlyDisplayInViewRange;
        public bool  WindowInvisibleWhenInteract = true;
        public bool  WindowVisibleWhenCombat     = true;
    }
    
    [GeneratedRegex("\\[.*?\\]")]
    private static partial Regex FastObjectInteractTitleRegex();
}
