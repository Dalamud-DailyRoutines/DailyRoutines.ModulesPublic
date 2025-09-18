using System;
using System.Collections.Generic;
using DailyRoutines.Abstracts;
using Dalamud.Hooking;
using Dalamud.Interface;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.System.Framework;

namespace DailyRoutines.ModulesPublic;

public unsafe class CustomizeSightDistance : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("CustomizeSightDistanceTitle"),
        Description = GetLoc("CustomizeSightDistanceDescription"),
        Category    = ModuleCategories.System,
    };

    private static readonly CompSig                     CameraUpdateSig = new("8B 81 ?? ?? ?? ?? 33 D2 F3 0F 10 05");
    private delegate        nint                        CameraUpdateDelegate(Camera* camera);
    private static          Hook<CameraUpdateDelegate>? CameraUpdateHook;

    private static readonly CompSig CameraCurrentSightDistanceSig = new("48 83 EC ?? 48 8B 15 ?? ?? ?? ?? 0F 29 74 24");
    private delegate float CameraCurrentSightDistanceDelegate(
        nint  a1,
        float minValue,
        float maxValue,
        float upperBound,
        float lowerBound,
        int   mode,
        float currentValue,
        float targetValue);
    private static Hook<CameraCurrentSightDistanceDelegate>? CameraCurrentSightDistanceHook;

    private static readonly CompSig     CameraCollisionBaseSig = new("84 C0 0F 84 ?? ?? ?? ?? F3 0F 10 44 24 ?? 41 B7");
    private static readonly MemoryPatch CameraCollisionPatch   = new(CameraCollisionBaseSig.Get(), [0x90, 0x90, 0xE9, 0xA7, 0x01, 0x00, 0x00, 0x90]);

    private static readonly Dictionary<string, float> OriginalData = new()
    {
        ["CustomizeSightDistance-MaxDistanceInput"] = 20f,
        ["CustomizeSightDistance-MinDistanceInput"] = 1.5f,
        ["CustomizeSightDistance-MaxRotationInput"] = 0.785398f,
        ["CustomizeSightDistance-MinRotationInput"] = -1.483530f,
        ["CustomizeSightDistance-MaxFoVInput"]      = 0.78f,
        ["CustomizeSightDistance-MinFoVInput"]      = 0.69f,
        ["CustomizeSightDistance-ManualFoVInput"]   = 0.78f
    };

    private static Config ModuleConfig = null!;

    protected override void Init()
    {
        ModuleConfig = LoadConfig<Config>() ?? new();

        CameraUpdateHook ??= CameraUpdateSig.GetHook<CameraUpdateDelegate>(CameraUpdateDetour);
        CameraUpdateHook.Enable();

        CameraCurrentSightDistanceHook ??= CameraCurrentSightDistanceSig.GetHook<CameraCurrentSightDistanceDelegate>(CameraCurrentSightDistanceDetour);
        CameraCurrentSightDistanceHook.Enable();

        if (ModuleConfig.IgnoreCollision)
            CameraCollisionPatch.Enable();

        UpdateCamera(CameraManager.Instance()->Camera, ModuleConfig.MaxDistance, ModuleConfig.MinDistance, ModuleConfig.MaxRotation, ModuleConfig.MinRotation, ModuleConfig.MaxFoV, ModuleConfig.MinFoV, ModuleConfig.FoV);
    }

    protected override void ConfigUI()
    {
        using var table = ImRaii.Table("##SightTable", 2, ImGuiTableFlags.NoBordersInBody);
        if (!table) return;

        ImGui.TableSetupColumn("Parameter", ImGuiTableColumnFlags.WidthFixed);
        ImGui.TableSetupColumn("Value", ImGuiTableColumnFlags.WidthStretch);

        AddSlider("CustomizeSightDistance-MaxDistanceInput", ref ModuleConfig.MaxDistance,
                  ModuleConfig.MinDistance > 1 ? ModuleConfig.MinDistance : 1, 80, "%.1f");
        AddSlider("CustomizeSightDistance-MinDistanceInput", ref ModuleConfig.MinDistance, 0, ModuleConfig.MaxDistance, "%.1f");
        AddSlider("CustomizeSightDistance-MaxRotationInput", ref ModuleConfig.MaxRotation, ModuleConfig.MinRotation, 1.569f, "%.3f");
        AddSlider("CustomizeSightDistance-MinRotationInput", ref ModuleConfig.MinRotation, -1.569f, ModuleConfig.MaxRotation, "%.3f");
        AddSlider("CustomizeSightDistance-MaxFoVInput", ref ModuleConfig.MaxFoV, ModuleConfig.MinFoV, 3f, "%.3f");
        AddSlider("CustomizeSightDistance-MinFoVInput", ref ModuleConfig.MinFoV, 0.01f, ModuleConfig.MaxFoV, "%.3f");
        AddSlider("CustomizeSightDistance-ManualFoVInput", ref ModuleConfig.FoV, ModuleConfig.MinFoV, ModuleConfig.MaxFoV, "%.3f");

        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        ImGui.Text($"{GetLoc("CustomizeSightDistance-IgnoreCollision")}: ");

        ImGui.TableNextColumn();
        if (ImGui.Checkbox("###IgnoreCollision", ref ModuleConfig.IgnoreCollision))
        {
            SaveConfig(ModuleConfig);
            if (ModuleConfig.IgnoreCollision)
                CameraCollisionPatch.Enable();
            else
                CameraCollisionPatch.Disable();
        }
    }

    private void AddSlider(string label, ref float value, float min, float max, string format)
    {
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        ImGui.Text($"{GetLoc(label)}:");

        ImGui.TableNextColumn();
        ImGui.SetNextItemWidth(200f * GlobalFontScale);
        ImGui.SliderFloat($"##{label}", ref value, min, max, format);

        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            SaveConfig(ModuleConfig);
            UpdateCamera(CameraManager.Instance()->Camera, ModuleConfig.MaxDistance, ModuleConfig.MinDistance, ModuleConfig.MaxRotation, ModuleConfig.MinRotation, ModuleConfig.MaxFoV, ModuleConfig.MinFoV, ModuleConfig.FoV);
        }
        ImGui.SameLine();
        if (ImGuiOm.ButtonIcon($"##reset{label}", FontAwesomeIcon.UndoAlt, GetLoc("Reset")))
        {
            value = OriginalData[label];
            SaveConfig(ModuleConfig);
            UpdateCamera(CameraManager.Instance()->Camera, ModuleConfig.MaxDistance, ModuleConfig.MinDistance, ModuleConfig.MaxRotation, ModuleConfig.MinRotation, ModuleConfig.MaxFoV, ModuleConfig.MinFoV, ModuleConfig.FoV);
        }
    }


    private static nint CameraUpdateDetour(Camera* camera)
    {
        var original = CameraUpdateHook.Original(camera);
        UpdateCamera(camera, ModuleConfig.MaxDistance, ModuleConfig.MinDistance, ModuleConfig.MaxRotation, ModuleConfig.MinRotation, ModuleConfig.MaxFoV, ModuleConfig.MinFoV, ModuleConfig.FoV);
        return original;
    }

    private static float CameraCurrentSightDistanceDetour(nint a1, float minValue, float maxValue, float upperBound, float lowerBound, int mode, float currentValue, float targetValue)
    {
        const float Epsilon = 0.001f;

        var framework = Framework.Instance();
        var adjustedUpperBound = Math.Min(upperBound - Epsilon, maxValue);
        var adjustedLowerBound = Math.Min(lowerBound - Epsilon, maxValue);

        var newValue = mode switch
        {
            1 => Math.Min(adjustedUpperBound, Interpolate(adjustedLowerBound, 0.3f)),
            2 => Interpolate(adjustedUpperBound, 0.3f),
            3 => adjustedUpperBound,
            0 or 4 or 5 => Interpolate(adjustedUpperBound, 0.07f),
            _ => currentValue
        };

        return Math.Max(Math.Min(targetValue, newValue), ModuleConfig.MinDistance);

        float Interpolate(float target, float multiplier)
        {
            if (Math.Abs(target - currentValue) < Epsilon)
                return target;

            var delta = Math.Min(framework->FrameDeltaTime * 60.0f * multiplier, 1.0f);
            if (currentValue < target && target > targetValue)
                return Math.Min(currentValue + (delta * (target - currentValue)), targetValue);
            return currentValue + (delta * (target - currentValue));
        }
    }

    private static void UpdateCamera(Camera* camera, float maxDistance, float minDistance, float maxRotation, float minRotation, float maxFoV, float minFoV, float FoV)
    {
        camera->MinDistance = minDistance;
        camera->MaxDistance = maxDistance;
        *(float*)((byte*)camera + 328) = minRotation;
        *(float*)((byte*)camera + 332) = maxRotation;
        camera->MinFoV = minFoV;
        camera->MaxFoV = maxFoV;
        camera->FoV = FoV;
    }

    protected override void Uninit()
    {
        if (!Initialized) return;
        CameraCollisionPatch.Disable();
        UpdateCamera(CameraManager.Instance()->Camera, 20f, 1.5f, 0.785398f, -1.483530f, 0.78f, 0.69f, 0.78f);
    }

    private class Config : ModuleConfiguration
    {
        public float MinDistance;
        public float MaxDistance = 80;
        public float MinRotation = -1.569f;
        public float MaxRotation = 1.569f;
        public float MinFoV = 0.69f;
        public float MaxFoV = 0.78f;
        public float FoV = 0.78f;
        public bool IgnoreCollision = true;
    }
}
