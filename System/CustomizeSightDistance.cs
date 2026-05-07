using System.Collections.Frozen;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
using OmenTools.Interop.Game;
using OmenTools.Interop.Game.Models;

namespace DailyRoutines.ModulesPublic;

public unsafe class CustomizeSightDistance : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("CustomizeSightDistanceTitle"),
        Description = Lang.Get("CustomizeSightDistanceDescription"),
        Category    = ModuleCategory.System
    };

    private static readonly CompSig                       SetActiveCameraSig = new("40 57 41 54 41 57 48 83 EC ?? 4C 63 FA");
    private delegate        void                          SetActiveCameraDelegate(CameraManager* manager, int cameraIndex, void* a3);
    private                 Hook<SetActiveCameraDelegate> SetActiveCameraHook;

    private static readonly CompSig CameraCurrentSightDistanceSig = new("40 53 48 83 EC ?? 48 8B 15 ?? ?? ?? ?? 48 8B D9 0F 29 74 24");
    private delegate float CameraCurrentSightDistanceDelegate
    (
        nint  a1,
        float minValue,
        float maxValue,
        float upperBound,
        float lowerBound,
        int   mode,
        float currentValue,
        float targetValue
    );
    private Hook<CameraCurrentSightDistanceDelegate>? CameraCurrentSightDistanceHook;

    private static readonly CompSig     CameraCollisionBaseSig = new("84 C0 0F 84 ?? ?? ?? ?? F3 0F 10 44 24 ?? 41 B7");
    private readonly        MemoryPatch cameraCollisionPatch   = new(CameraCollisionBaseSig.Get(), [0x90, 0x90, 0xE9, 0xA7, 0x01, 0x00, 0x00, 0x90]);

    private Config config = null!;

    protected override void Init()
    {
        config = Config.Load(this) ?? new();

        SetActiveCameraHook = SetActiveCameraSig.GetHook<SetActiveCameraDelegate>(SetActiveCameraDetour);
        SetActiveCameraHook.Enable();

        CameraCurrentSightDistanceHook ??= CameraCurrentSightDistanceSig.GetHook<CameraCurrentSightDistanceDelegate>(CameraCurrentSightDistanceDetour);
        CameraCurrentSightDistanceHook.Enable();

        if (config.IgnoreCollision)
            cameraCollisionPatch.Enable();
        
        UpdateCamera
        (
            CameraManager.Instance()->Camera,
            config.MaxDistance,
            config.MinDistance,
            config.MaxRotation,
            config.MinRotation,
            config.MaxFoV,
            config.MinFoV,
            config.FoV
        );
    }

    protected override void Uninit()
    {
        if (!IsEnabled) return;
        cameraCollisionPatch.Disable();

        UpdateCamera(CameraManager.Instance()->Camera, 20f, 1.5f, 0.785398f, -1.483530f, 0.78f, 0.69f, 0.78f);
    }

    protected override void ConfigUI()
    {
        using var table = ImRaii.Table("##SightTable", 2, ImGuiTableFlags.NoBordersInBody);
        if (!table) return;

        ImGui.TableSetupColumn("Parameter", ImGuiTableColumnFlags.WidthFixed);
        ImGui.TableSetupColumn("Value",     ImGuiTableColumnFlags.WidthStretch);

        AddSlider("CustomizeSightDistance-MaxDistanceInput", ref config.MaxDistance, config.MinDistance > 1 ? config.MinDistance : 1, 80, "%.1f");
        AddSlider("CustomizeSightDistance-MinDistanceInput", ref config.MinDistance, 0, config.MaxDistance, "%.1f");
        AddSlider("CustomizeSightDistance-MaxRotationInput", ref config.MaxRotation, config.MinRotation, 1.569f, "%.3f");
        AddSlider("CustomizeSightDistance-MinRotationInput", ref config.MinRotation, -1.569f, config.MaxRotation, "%.3f");
        AddSlider("CustomizeSightDistance-MaxFoVInput",      ref config.MaxFoV,      config.MinFoV, 3f, "%.3f");
        AddSlider("CustomizeSightDistance-MinFoVInput",      ref config.MinFoV,      0.01f, config.MaxFoV, "%.3f");
        AddSlider("CustomizeSightDistance-ManualFoVInput",   ref config.FoV,         config.MinFoV, config.MaxFoV, "%.3f");

        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        ImGui.TextUnformatted($"{Lang.Get("CustomizeSightDistance-IgnoreCollision")}: ");

        ImGui.TableNextColumn();

        if (ImGui.Checkbox("###IgnoreCollision", ref config.IgnoreCollision))
        {
            config.Save(this);
            if (config.IgnoreCollision)
                cameraCollisionPatch.Enable();
            else
                cameraCollisionPatch.Disable();
        }
    }

    private void AddSlider(string label, ref float value, float min, float max, string format)
    {
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        ImGui.TextUnformatted($"{Lang.Get(label)}:");

        ImGui.TableNextColumn();
        ImGui.SetNextItemWidth(200f * GlobalUIScale);
        ImGui.SliderFloat($"##{label}", ref value, min, max, format);

        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            config.Save(this);
            UpdateCamera
            (
                CameraManager.Instance()->Camera,
                config.MaxDistance,
                config.MinDistance,
                config.MaxRotation,
                config.MinRotation,
                config.MaxFoV,
                config.MinFoV,
                config.FoV
            );
        }

        ImGui.SameLine();

        if (ImGuiOm.ButtonIcon($"##reset{label}", FontAwesomeIcon.UndoAlt, Lang.Get("Reset")))
        {
            value = OriginalData[label];
            config.Save(this);
            UpdateCamera
            (
                CameraManager.Instance()->Camera,
                config.MaxDistance,
                config.MinDistance,
                config.MaxRotation,
                config.MinRotation,
                config.MaxFoV,
                config.MinFoV,
                config.FoV
            );
        }
    }
    
    private void SetActiveCameraDetour(CameraManager* manager, int cameraIndex, void* a3)
    {
        SetActiveCameraHook.Original(manager, cameraIndex, a3);
        
        UpdateCamera
        (
            manager->Camera,
            config.MaxDistance,
            config.MinDistance,
            config.MaxRotation,
            config.MinRotation,
            config.MaxFoV,
            config.MinFoV,
            config.FoV
        );
    }

    private float CameraCurrentSightDistanceDetour
    (
        nint  a1,
        float minValue,
        float maxValue,
        float upperBound,
        float lowerBound,
        int   mode,
        float currentValue,
        float targetValue
    )
    {
        const float EPSILON = 0.001f;

        var framework          = Framework.Instance();
        var adjustedUpperBound = Math.Min(upperBound - EPSILON, maxValue);
        var adjustedLowerBound = Math.Min(lowerBound - EPSILON, maxValue);

        var newValue = mode switch
        {
            1           => Math.Min(adjustedUpperBound, Interpolate(adjustedLowerBound, 0.3f)),
            2           => Interpolate(adjustedUpperBound, 0.3f),
            3           => adjustedUpperBound,
            0 or 4 or 5 => Interpolate(adjustedUpperBound, 0.07f),
            _           => currentValue
        };

        return Math.Max(Math.Min(targetValue, newValue), config.MinDistance);

        float Interpolate(float target, float multiplier)
        {
            if (Math.Abs(target - currentValue) < EPSILON)
                return target;

            var delta = Math.Min(framework->FrameDeltaTime * 60.0f * multiplier, 1.0f);
            if (currentValue < target && target > targetValue)
                return Math.Min(currentValue + delta * (target - currentValue), targetValue);
            return currentValue + delta * (target - currentValue);
        }
    }

    private static void UpdateCamera
    (
        Camera* camera,
        float   maxDistance,
        float   minDistance,
        float   maxRotation,
        float   minRotation,
        float   maxFoV,
        float   minFoV,
        float   FoV
    )
    {
        camera->MinDistance            = minDistance;
        camera->MaxDistance            = maxDistance;
        *(float*)((byte*)camera + 344) = minRotation;
        *(float*)((byte*)camera + 348) = maxRotation;
        camera->MinFoV                 = minFoV;
        camera->MaxFoV                 = maxFoV;
        camera->FoV                    = FoV;
    }
    
    private class Config : ModuleConfig
    {
        public float FoV             = 0.78f;
        public bool  IgnoreCollision = true;
        public float MaxDistance     = 80;
        public float MaxFoV          = 0.78f;
        public float MaxRotation     = 1.569f;
        public float MinDistance;
        public float MinFoV      = 0.69f;
        public float MinRotation = -1.569f;
    }
    
    #region 常量

    private static readonly FrozenDictionary<string, float> OriginalData = new Dictionary<string, float>()
    {
        ["CustomizeSightDistance-MaxDistanceInput"] = 20f,
        ["CustomizeSightDistance-MinDistanceInput"] = 1.5f,
        ["CustomizeSightDistance-MaxRotationInput"] = 0.785398f,
        ["CustomizeSightDistance-MinRotationInput"] = -1.483530f,
        ["CustomizeSightDistance-MaxFoVInput"]      = 0.78f,
        ["CustomizeSightDistance-MinFoVInput"]      = 0.69f,
        ["CustomizeSightDistance-ManualFoVInput"]   = 0.78f
    }.ToFrozenDictionary();

    #endregion
}
