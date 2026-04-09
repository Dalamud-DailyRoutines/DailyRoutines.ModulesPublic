using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using OmenTools.Interop.Game.Models;
using OmenTools.OmenService;
using GameObjectStruct = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;

namespace DailyRoutines.ModulesPublic;

public unsafe class CustomizeGameObject : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("CustomizeGameObjectTitle"),
        Description = Lang.Get("CustomizeGameObjectDescription"),
        Category    = ModuleCategory.System,
        Author      = ["HSS"]
    };

    public override ModulePermission Permission { get; } = new() { NeedAuth = true };

    private static readonly CompSig CharacterUpdateSig = new("4C 8B DC 53 48 81 EC ?? ?? ?? ?? 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 84 24 ?? ?? ?? ?? 80 89");
    private delegate        void*   CharacterUpdateDelegate(Character* character);
    private                 Hook<CharacterUpdateDelegate>? CharacterUpdateHook;

    private Config config = null!;

    private readonly Dictionary<uint, CustomizePreset>                lookupDataID          = [];
    private readonly Dictionary<ulong, CustomizePreset>               lookupObjectID        = [];
    private readonly Dictionary<int, CustomizePreset>                 lookupModelCharaID    = [];
    private readonly Dictionary<int, CustomizePreset>                 lookupModelSkeletonID = [];
    private readonly List<(byte[] NameBytes, CustomizePreset Preset)> lookupName            = [];

    private readonly Dictionary<nint, CustomizeHistoryEntry> customizeHistory = [];

    private readonly Dictionary<nint, (ulong GameObjectID, int LastCheckTick)> failureCache = [];

    private CustomizeType typeInput  = CustomizeType.Name;
    private string        noteInput  = string.Empty;
    private float         scaleInput = 1f;
    private string        valueInput = string.Empty;
    private bool          scaleVFXInput;

    private CustomizeType typeEditInput  = CustomizeType.Name;
    private string        noteEditInput  = string.Empty;
    private float         scaleEditInput = 1f;
    private string        valueEditInput = string.Empty;
    private bool          scaleVFXEditInput;

    protected override void Init()
    {
        config = Config.Load(this) ?? new();
        RebuildLookupCache();

        DService.Instance().ClientState.TerritoryChanged += OnZoneChanged;

        CharacterUpdateHook ??= CharacterUpdateSig.GetHook<CharacterUpdateDelegate>(UpdateCharacterDetour);
        CharacterUpdateHook.Enable();
    }

    protected override void Uninit()
    {
        DService.Instance().ClientState.TerritoryChanged -= OnZoneChanged;
        CharacterUpdateHook?.Disable();

        if (DService.Instance().ObjectTable.LocalPlayer != null)
            ResetAllCustomizeFromHistory();

        customizeHistory.Clear();
        failureCache.Clear();
        ClearLookupCache();
    }
    
    #region 事件

    private void* UpdateCharacterDetour(Character* character)
    {
        if (config.CustomizePresets.Count == 0)
            return CharacterUpdateHook.Original(character);

        var ret = CharacterUpdateHook.Original(character);

        ProcessCharacter(character);
        return ret;
    }

    private void OnZoneChanged(ushort zone)
    {
        customizeHistory.Clear();
        failureCache.Clear();
    }

    #endregion

    #region 工具

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ProcessCharacter(Character* character)
    {
        if (character == null) return;

        var addr = (nint)character;

        ref var historyEntry = ref CollectionsMarshal.GetValueRefOrNullRef(customizeHistory, addr);

        if (!Unsafe.IsNullRef(ref historyEntry))
        {
            if (MathF.Abs(character->GameObject.Scale - historyEntry.CurrentScale) > 0.1f)
                customizeHistory.Remove(addr);

            return;
        }

        ref var failureEntry = ref CollectionsMarshal.GetValueRefOrNullRef(failureCache, addr);

        if (!Unsafe.IsNullRef(ref failureEntry))
        {
            var currentID = character->GameObject.GetGameObjectId();

            if (failureEntry.GameObjectID == (ulong)currentID)
            {
                if (unchecked(Environment.TickCount64 - failureEntry.LastCheckTick) < THROTTLE_INTERVAL_MS)
                    return;
            }
        }

        CustomizePreset? match = null;

        if (lookupObjectID.Count > 0)
        {
            var objectID = character->GameObject.GetGameObjectId();
            if (lookupObjectID.TryGetValue(objectID, out match)) goto Apply;
        }

        // DataID (Base ID / NPC ID)
        if (lookupDataID.Count > 0)
        {
            if (lookupDataID.TryGetValue(character->BaseId, out match))
                goto Apply;
        }

        // ModelCharaID
        if (lookupModelCharaID.Count > 0)
        {
            if (lookupModelCharaID.TryGetValue(character->ModelContainer.ModelCharaId, out match))
                goto Apply;
        }

        // ModelSkeletonID
        if (lookupModelSkeletonID.Count > 0)
        {
            if (lookupModelSkeletonID.TryGetValue(character->ModelContainer.ModelSkeletonId, out match))
                goto Apply;
        }

        // Name
        if (lookupName.Count > 0)
        {
            if (character->GameObject.ObjectKind == ObjectKind.Pc)
            {
                // 空名字跳过
                if (character->GameObject.Name[0] == 0)
                    goto NoMatch;
            }

            var charNamePtr = character->GameObject.Name;

            foreach (var (nameBytes, preset) in lookupName)
            {
                if (IsNameEqual(charNamePtr, nameBytes))
                {
                    match = preset;
                    goto Apply;
                }
            }
        }

        NoMatch:

        if (Unsafe.IsNullRef(ref failureEntry))
            failureCache[addr] = (character->GameObject.GetGameObjectId(), Environment.TickCount);
        else
        {
            failureEntry.GameObjectID  = character->GameObject.GetGameObjectId();
            failureEntry.LastCheckTick = Environment.TickCount;
        }

        return;

        Apply:
        if (!Unsafe.IsNullRef(ref failureEntry))
            failureCache.Remove(addr);
        ApplyPreset(character, addr, match);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsNameEqual(Span<byte> gameObjectName, byte[] presetNameBytes)
    {
        if (gameObjectName[presetNameBytes.Length] != 0) return false;
        return gameObjectName.SequenceEqual(presetNameBytes);
    }

    private void ApplyPreset(Character* chara, nint addr, CustomizePreset preset)
    {
        var currentScale  = chara->GameObject.Scale;
        var modifiedScale = currentScale * preset.Scale;

        var entry = new CustomizeHistoryEntry(preset, currentScale, modifiedScale);

        if (customizeHistory.TryAdd(addr, entry))
        {
            chara->GameObject.Scale = modifiedScale;

            if (preset.ScaleVFX)
                chara->GameObject.VfxScale = modifiedScale;

            if (preset.Type is CustomizeType.ModelCharaID or CustomizeType.ModelSkeletonID)
                chara->CharacterData.ModelScale = modifiedScale;

            if (chara->IsReadyToDraw())
            {
                chara->GameObject.DisableDraw();
                chara->GameObject.EnableDraw();
            }
        }
    }

    private void ResetCustomizeFromHistory(nint address)
    {
        if (customizeHistory.Count == 0) return;
        if (!customizeHistory.TryGetValue(address, out var data)) return;

        var gameObj = (GameObjectStruct*)address;
        if (gameObj == null) return;

        gameObj->Scale    = data.OrigScale;
        gameObj->VfxScale = data.OrigScale;

        if (gameObj->IsReadyToDraw())
        {
            gameObj->DisableDraw();
            gameObj->EnableDraw();
        }
    }

    private void ResetAllCustomizeFromHistory()
    {
        if (customizeHistory.Count == 0) return;

        foreach (var (objectPtr, data) in customizeHistory)
        {
            var gameObj = (GameObjectStruct*)objectPtr;
            if (gameObj == null) continue;

            gameObj->Scale    = data.OrigScale;
            gameObj->VfxScale = data.OrigScale;

            if (gameObj->IsReadyToDraw())
            {
                gameObj->DisableDraw();
                gameObj->EnableDraw();
            }
        }
    }

    private void RemovePresetHistory(CustomizePreset? preset)
    {
        var keysToRemove = customizeHistory
                           .Where(x => x.Value.Preset == preset)
                           .Select(x => x.Key)
                           .ToList();

        foreach (var key in keysToRemove)
        {
            ResetCustomizeFromHistory(key);
            customizeHistory.Remove(key);
        }
    }

    private void RebuildLookupCache()
    {
        ClearLookupCache();
        // 重建缓存时，清除所有运行时状态，以确保新规则能立即生效
        failureCache.Clear();

        foreach (var preset in config.CustomizePresets)
        {
            if (!preset.Enabled) continue;

            try
            {
                switch (preset.Type)
                {
                    case CustomizeType.Name:
                        if (!string.IsNullOrEmpty(preset.Value))
                            lookupName.Add((Encoding.UTF8.GetBytes(preset.Value), preset));
                        break;
                    case CustomizeType.DataID:
                        if (uint.TryParse(preset.Value, out var dataId))
                            lookupDataID.TryAdd(dataId, preset);
                        break;
                    case CustomizeType.ObjectID:
                        var valStr = preset.Value.Trim();
                        var val = valStr.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                                      ? ulong.Parse(valStr.AsSpan(2), NumberStyles.HexNumber)
                                      : ulong.Parse(valStr);
                        lookupObjectID.TryAdd(val, preset);
                        break;
                    case CustomizeType.ModelCharaID:
                        if (int.TryParse(preset.Value, out var charaId))
                            lookupModelCharaID.TryAdd(charaId, preset);
                        break;
                    case CustomizeType.ModelSkeletonID:
                        if (int.TryParse(preset.Value, out var skelId))
                            lookupModelSkeletonID.TryAdd(skelId, preset);
                        break;
                }
            }
            catch
            {
                // ignored
            }
        }
    }

    private void ClearLookupCache()
    {
        lookupDataID.Clear();
        lookupObjectID.Clear();
        lookupModelCharaID.Clear();
        lookupModelSkeletonID.Clear();
        lookupName.Clear();
    }

    #endregion

    #region UI

    protected override void ConfigUI()
    {
        TargetInfoPreviewUI(TargetManager.Target);

        var       tableSize = (ImGui.GetContentRegionAvail() - ScaledVector2(100f)) with { Y = 0 };
        using var table     = ImRaii.Table("###ConfigTable", 7, ImGuiTableFlags.BordersInner, tableSize);
        if (!table) return;

        ImGui.TableSetupColumn("启用",   ImGuiTableColumnFlags.WidthFixed, ImGui.GetTextLineHeightWithSpacing());
        ImGui.TableSetupColumn("备注",   ImGuiTableColumnFlags.None,       20);
        ImGui.TableSetupColumn("模式",   ImGuiTableColumnFlags.WidthFixed, ImGui.CalcTextSize("ModelSkeletonID").X);
        ImGui.TableSetupColumn("值",    ImGuiTableColumnFlags.None,       30);
        ImGui.TableSetupColumn("缩放比例", ImGuiTableColumnFlags.WidthFixed, ImGui.CalcTextSize("99.99").X);
        ImGui.TableSetupColumn("缩放特效", ImGuiTableColumnFlags.WidthFixed, ImGui.GetTextLineHeightWithSpacing());
        ImGui.TableSetupColumn("操作",   ImGuiTableColumnFlags.WidthFixed, 6 * ImGui.GetTextLineHeightWithSpacing());

        ImGui.TableNextRow(ImGuiTableRowFlags.Headers);

        DrawAddPresetSection();
        ImGui.TableNextColumn();
        ImGuiOm.Text(Lang.Get("Note"));
        ImGui.TableNextColumn();
        ImGuiOm.Text(Lang.Get("CustomizeGameObject-CustomizeType"));
        ImGui.TableNextColumn();
        ImGuiOm.Text(Lang.Get("Value"));
        ImGui.TableNextColumn();
        ImGuiOm.Text(Lang.Get("CustomizeGameObject-Scale"));
        ImGui.TableNextColumn();
        ImGui.Dummy(new(32f));
        ImGuiOm.TooltipHover(Lang.Get("CustomizeGameObject-ScaleVFX"));
        ImGui.TableNextColumn();
        ImGuiOm.Text(Lang.Get("Operation"));

        var array = config.CustomizePresets.ToArray();

        for (var i = 0; i < array.Length; i++)
        {
            var       preset = array[i];
            using var id     = ImRaii.PushId($"Preset_{i}");

            DrawPresetRow(i, preset);
        }
    }

    private void DrawAddPresetSection()
    {
        ImGui.TableNextColumn();
        if (ImGuiOm.SelectableIconCentered("AddNewPreset", FontAwesomeIcon.Plus))
            ImGui.OpenPopup("AddNewPresetPopup");

        using var popup = ImRaii.Popup("AddNewPresetPopup");

        if (popup)
        {
            CustomizePresetEditorUI(ref typeInput, ref valueInput, ref scaleInput, ref scaleVFXInput, ref noteInput);
            ImGui.Spacing();

            var buttonSize = new Vector2(ImGui.GetContentRegionAvail().X, 24f * GlobalUIScale);

            if (ImGui.Button(Lang.Get("Add"), buttonSize))
            {
                if (scaleInput > 0 && !string.IsNullOrWhiteSpace(valueInput))
                {
                    var newPreset = new CustomizePreset
                    {
                        Enabled  = true,
                        Scale    = scaleInput,
                        Type     = typeInput,
                        Value    = valueInput,
                        ScaleVFX = scaleVFXInput,
                        Note     = noteInput
                    };

                    config.CustomizePresets.Add(newPreset);
                    SaveAndRebuild();
                    ImGui.CloseCurrentPopup();
                }
            }
        }
    }

    private void DrawPresetRow(int i, CustomizePreset preset)
    {
        ImGui.TableNextRow();

        // Enabled
        ImGui.TableNextColumn();
        var isEnabled = preset.Enabled;

        if (ImGui.Checkbox("###IsEnabled", ref isEnabled))
        {
            preset.Enabled = isEnabled;
            RemovePresetHistory(preset);
            SaveAndRebuild();
        }

        // Note
        ImGui.TableNextColumn();
        ImGuiOm.Text(preset.Note);

        // Type
        ImGui.TableNextColumn();
        ImGuiOm.Text(preset.Type.ToString());

        // Value
        ImGui.TableNextColumn();
        ImGuiOm.Text(preset.Value);

        // Scale
        ImGui.TableNextColumn();
        ImGuiOm.Text(preset.Scale.ToString(CultureInfo.InvariantCulture));

        // ScaleVFX
        ImGui.TableNextColumn();
        var isScaleVFX = preset.ScaleVFX;

        if (ImGui.Checkbox("###IsScaleVFX", ref isScaleVFX))
        {
            preset.ScaleVFX = isScaleVFX;
            RemovePresetHistory(preset);
            SaveAndRebuild();
        }

        // Operations
        ImGui.TableNextColumn();

        // Edit
        if (ImGuiOm.ButtonIcon($"EditPreset_{i}", FontAwesomeIcon.Edit))
            ImGui.OpenPopup($"EditNewPresetPopup_{i}");

        using (var popup = ImRaii.Popup($"EditNewPresetPopup_{i}"))
        {
            if (popup)
            {
                if (ImGui.IsWindowAppearing())
                {
                    typeEditInput     = preset.Type;
                    noteEditInput     = preset.Note;
                    scaleEditInput    = preset.Scale;
                    valueEditInput    = preset.Value;
                    scaleVFXEditInput = preset.ScaleVFX;
                }

                if (CustomizePresetEditorUI(ref typeEditInput, ref valueEditInput, ref scaleEditInput, ref scaleVFXEditInput, ref noteEditInput))
                {
                    preset.Type     = typeEditInput;
                    preset.Value    = valueEditInput;
                    preset.Scale    = scaleEditInput;
                    preset.ScaleVFX = scaleVFXEditInput;
                    preset.Note     = noteEditInput;

                    RemovePresetHistory(preset);
                    SaveAndRebuild();
                }
            }
        }

        ImGui.SameLine();

        // Delete
        if (ImGuiOm.ButtonIcon($"DeletePreset_{i}", FontAwesomeIcon.TrashAlt, Lang.Get("HoldCtrlToDelete")) &&
            ImGui.IsKeyDown(ImGuiKey.LeftCtrl))
        {
            RemovePresetHistory(preset);
            config.CustomizePresets.Remove(preset);
            SaveAndRebuild();
        }

        ImGui.SameLine();
        // Export
        if (ImGuiOm.ButtonIcon($"ExportPreset_{i}", FontAwesomeIcon.FileExport, Lang.Get("ExportToClipboard")))
            ExportToClipboard(preset);

        ImGui.SameLine();

        // Import
        if (ImGuiOm.ButtonIcon($"ImportPreset_{i}", FontAwesomeIcon.FileImport, Lang.Get("ImportFromClipboard")))
        {
            var presetImport = ImportFromClipboard<CustomizePreset>();

            if (presetImport != null && !config.CustomizePresets.Contains(presetImport))
            {
                config.CustomizePresets.Add(presetImport);
                SaveAndRebuild();
            }
        }
    }

    private void SaveAndRebuild()
    {
        config.Save(this);
        RebuildLookupCache();
    }

    private static bool CustomizePresetEditorUI
        (ref CustomizeType typeInput, ref string valueInput, ref float scaleInput, ref bool scaleVFXInput, ref string noteInput)
    {
        var       state = false;
        using var table = ImRaii.Table("CustomizeTable", 2, ImGuiTableFlags.None);
        if (!table) return false;

        ImGui.TableSetupColumn("Label", ImGuiTableColumnFlags.WidthFixed, ImGui.CalcTextSize("真得五个字").X);
        ImGui.TableSetupColumn("Input", ImGuiTableColumnFlags.WidthFixed, 300f * GlobalUIScale);

        // Type
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        ImGui.TextUnformatted($"{Lang.Get("CustomizeGameObject-CustomizeType")}:");
        ImGui.TableNextColumn();

        using (var combo = ImRaii.Combo("###CustomizeTypeSelectCombo", typeInput.ToString()))
        {
            if (combo)
            {
                foreach (var mode in Enum.GetValues<CustomizeType>())
                {
                    if (ImGui.Selectable(mode.ToString(), mode == typeInput))
                    {
                        typeInput = mode;
                        state     = true;
                    }
                }
            }
        }

        // Value
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        ImGui.TextUnformatted($"{Lang.Get("Value")}:");
        ImGui.TableNextColumn();
        ImGui.InputText("###CustomizeValueInput", ref valueInput, 128);
        if (ImGui.IsItemDeactivatedAfterEdit()) state = true;
        ImGuiOm.TooltipHover(valueInput);

        // Scale
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        ImGui.TextUnformatted($"{Lang.Get("CustomizeGameObject-Scale")}:");
        ImGui.TableNextColumn();
        ImGui.SliderFloat("###CustomizeScaleSilder", ref scaleInput, 0.1f, 10f, "%.1f");
        if (ImGui.IsItemDeactivatedAfterEdit()) state = true;
        ImGui.SameLine();
        if (ImGui.Checkbox(Lang.Get("CustomizeGameObject-ScaleVFX"), ref scaleVFXInput)) state = true;

        // Note
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        ImGui.TextUnformatted($"{Lang.Get("Note")}:");
        ImGui.TableNextColumn();
        ImGui.InputText("###CustomizeNoteInput", ref noteInput, 128);
        if (ImGui.IsItemDeactivatedAfterEdit()) state = true;
        ImGuiOm.TooltipHover(noteInput);

        return state;
    }

    private static void TargetInfoPreviewUI(IGameObject? gameObject)
    {
        if (gameObject is not ICharacter chara)
        {
            ImGui.TextUnformatted(Lang.Get("CustomizeGameObject-NoTaretNotice"));
            return;
        }

        var       tableSize = new Vector2(350f * GlobalUIScale, 0f);
        using var table     = ImRaii.Table("TargetInfoPreviewTable", 2, ImGuiTableFlags.BordersInner, tableSize);
        if (!table) return;

        ImGui.TableSetupColumn("Lable", ImGuiTableColumnFlags.WidthFixed,   ImGui.CalcTextSize("--Model Skeleton ID--").X);
        ImGui.TableSetupColumn("Input", ImGuiTableColumnFlags.WidthStretch, 50);

        void DrawRow(string label, string value)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted(label);
            ImGui.TableNextColumn();
            ImGui.SetNextItemWidth(-1f);
            ImGui.InputText($"###{label}Preview", ref value, 128, ImGuiInputTextFlags.ReadOnly);
        }

        DrawRow(Lang.Get("Name"),    chara.Name.ToString());
        DrawRow("Data ID",           chara.DataID.ToString());
        DrawRow("Object ID",         chara.GameObjectID.ToString()); // Use hex for clarity? or keep decimal
        DrawRow("Model Chara ID",    chara.ModelCharaID.ToString());
        DrawRow("Model Skeleton ID", chara.ModelSkeletonID.ToString());

        if (chara is IPlayerCharacter { CurrentMount: not null } pc)
        {
            // Accessing mount object safely
            // Using pointer logic similar to original but safer if possible
            var mountObj = pc.ToStruct()->Mount.MountObject;
            if (mountObj != null)
                DrawRow("Mount Object ID", mountObj->GetGameObjectId().ToString());
        }
    }

    #endregion
    
    private class CustomizePreset : IEquatable<CustomizePreset>
    {
        public string        Note     { get; set; } = string.Empty;
        public CustomizeType Type     { get; set; }
        public string        Value    { get; set; } = string.Empty;
        public float         Scale    { get; set; }
        public bool          ScaleVFX { get; set; }
        public bool          Enabled  { get; set; }

        public bool Equals(CustomizePreset? other)
        {
            if (other == null) return false;
            return Type == other.Type && string.Equals(Value, other.Value, StringComparison.Ordinal);
        }

        public override bool Equals(object? obj) => obj is CustomizePreset other && Equals(other);

        public override int GetHashCode() => HashCode.Combine(Type, Value);

        public static bool operator ==(CustomizePreset left, CustomizePreset right) => Equals(left, right);

        public static bool operator !=(CustomizePreset left, CustomizePreset right) => !Equals(left, right);
    }

    private sealed record CustomizeHistoryEntry
    (
        CustomizePreset Preset,
        float           OrigScale,
        float           CurrentScale
    );

    private enum CustomizeType
    {
        Name,
        ModelCharaID,
        ModelSkeletonID,
        DataID,
        ObjectID
    }

    private class Config : ModuleConfig
    {
        public List<CustomizePreset> CustomizePresets = [];
    }
    
    #region 常量

    private const int THROTTLE_INTERVAL_MS = 2_000;

    #endregion
}
