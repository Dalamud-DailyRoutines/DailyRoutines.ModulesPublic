using System.Collections.Generic;
using System.Numerics;
using DailyRoutines.Abstracts;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Hooking;
using Dalamud.Interface;
using Dalamud.Utility.Numerics;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace DailyRoutines.ModulesPublic;

public unsafe class AutoHideBanners : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("AutoHideBannersTitle"),
        Description = GetLoc("AutoHideBannersDescription"),
        Category    = ModuleCategories.System,
        Author      = ["XSZYYS"]
    };
    
    public override ModulePermission Permission { get; } = new() { AllDefaultEnabled = true };

    private static readonly CompSig                        SetImageTextureSig = new("48 89 5C 24 ?? 57 48 83 EC 30 48 8B D9 89 91");
    private delegate        void*                          SetImageTextureDelegate(AtkUnitBase* addon, uint bannerID, uint a3, int soundEffectID);
    private static          Hook<SetImageTextureDelegate>? SetImageTextureHook;
    
    private static Config ModuleConfig = null!;
    private static readonly HashSet<uint> WKSMissionChainBannerIDs = [128527, 128528, 128529, 128530, 128531, 128532];

    protected override void Init()
    {
        ModuleConfig = LoadConfig<Config>() ?? new();

        var isAnyAdded = false;
        foreach (var bannerID in BannersData)
        {
            if (!ModuleConfig.HiddenBanners.TryAdd(bannerID, DefaultEnabledBanners.Contains(bannerID))) continue;
            isAnyAdded = true;
        }
        
        if (isAnyAdded)
            ModuleConfig.Save(this);
        
        SetImageTextureHook ??= SetImageTextureSig.GetHook<SetImageTextureDelegate>(SetImageTextureDetour);
        SetImageTextureHook.Enable();
        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PreDraw, "_WKSMissionChain", OnAddon);
    }

    protected override void Uninit() =>
        DService.Instance().AddonLifecycle.UnregisterListener(OnAddon);

    protected override void ConfigUI()
    {
        var tableSize = new Vector2(ImGui.GetContentRegionAvail().X - (2 * ImGui.GetStyle().ItemSpacing.X), 400f * GlobalFontScale);
        
        using var table = ImRaii.Table("BannerList", 2, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY, tableSize);
        if (!table) return;

        ImGui.TableSetupColumn("LeftColumn",  ImGuiTableColumnFlags.WidthStretch, 50);
        ImGui.TableSetupColumn("RightColumn", ImGuiTableColumnFlags.WidthStretch, 50);
        
        var bannersPerColumn = (BannersData.Count + 1) / 2;
        
        for (var i = 0; i < bannersPerColumn; i++)
        {
            ImGui.TableNextRow();
            
            // 左列
            ImGui.TableNextColumn();
            if (i < BannersData.Count)
            {
                var bannerID = BannersData[i];
                RenderBannerButton(bannerID, tableSize);
            }
            
            // 右列
            ImGui.TableNextColumn();
            var rightIndex = i + bannersPerColumn;
            if (rightIndex < BannersData.Count)
            {
                var bannerID = BannersData[rightIndex];
                RenderBannerButton(bannerID, tableSize);
            }
        }
    }
    
    private void RenderBannerButton(uint bannerID, Vector2 tableSize)
    {
        if (!ImageHelper.Instance().TryGetGameLangIcon(bannerID, out var texture)) return;
        
        var size      = (texture.Size * 0.2f).WithX(tableSize.X / 2);
        var cursorPos = ImGui.GetCursorPos();
            
        ImGui.Image(texture.Handle, size);
            
        ImGui.SetCursorPos(cursorPos);
        using (ImRaii.PushColor(ImGuiCol.Button, ButtonNormalColor))
        using (ImRaii.PushColor(ImGuiCol.ButtonActive, ButtonActiveColor))
        using (ImRaii.PushColor(ImGuiCol.ButtonHovered, ButtonHoveredColor))
        using (ImRaii.PushColor(ImGuiCol.Button, ButtonSelectedColor, ModuleConfig.HiddenBanners.GetValueOrDefault(bannerID)))
        {
            if (ImGui.Button($"##{bannerID}_{cursorPos}", size))
            {
                ModuleConfig.HiddenBanners[bannerID] ^= true;
                SaveConfig(ModuleConfig);
            }
        }
    }
    
    private static void OnAddon(AddonEvent type, AddonArgs args)
    {
        var addon = args.Addon.ToStruct();
        if (addon == null) return;

        var shouldHide = ShouldHideWKSMissionChain(addon);
        addon->RootNode->ToggleVisibility(!shouldHide);
    }

    private static bool ShouldHideWKSMissionChain(AtkUnitBase* addon)
    {
        for (var i = 0; i < addon->UldManager.NodeListCount; i++)
        {
            var node = addon->UldManager.NodeList[i];
            if (node == null || node->Type != NodeType.Image) continue;

            var iconID = GetImageNodeIconID(node->GetAsAtkImageNode());
            if (iconID == 0) continue;
            if (IsWKSMissionChainBannerSelected(iconID)) return true;
        }

        return false;
    }

    private static bool IsWKSMissionChainBannerSelected(uint iconID) =>
        WKSMissionChainBannerIDs.Contains(iconID) && ModuleConfig.HiddenBanners.GetValueOrDefault(iconID);

    private static uint GetImageNodeIconID(AtkImageNode* imageNode)
    {
        var parts = imageNode->PartsList->Parts;
        var asset = parts[imageNode->PartId].UldAsset;
        return asset->AtkTexture.Resource->IconId;
    }

    private static void* SetImageTextureDetour(AtkUnitBase* addon, uint bannerID, uint a3, int soundEffectID)
    {
        if (IsWKSMissionChainBannerSelected(bannerID))
            return SetImageTextureHook.Original(addon, bannerID, a3, soundEffectID);

        return ModuleConfig.HiddenBanners.GetValueOrDefault(bannerID)
            ? null
            : SetImageTextureHook.Original(addon, bannerID, a3, soundEffectID);
    }

    private class Config : ModuleConfiguration
    {
        // true - 隐藏; false - 维持
        public Dictionary<uint, bool> HiddenBanners = [];
    }
    
    private static readonly List<uint> BannersData =
    [
        120031, 120032, 120055, 120081, 120082, 120083, 120084, 120085, 120086,
        120093, 120094, 120095, 120096, 120141, 120142, 121081, 121082, 121561,
        121562, 121563, 128370, 128371, 128372, 128373, 128525, 128526,
        128527, 128528, 128529, 128530, 128531, 128532
    ];

    private static readonly HashSet<uint> DefaultEnabledBanners = [120031, 120032, 120055, 120095, 120096, 120141, 120142];

    private static readonly Vector4 ButtonNormalColor   = ImGui.ColorConvertU32ToFloat4(ImGui.GetColorU32(ImGuiCol.Button)).WithAlpha(0f);
    private static readonly Vector4 ButtonActiveColor   = ImGui.ColorConvertU32ToFloat4(ImGui.GetColorU32(ImGuiCol.ButtonActive)).WithAlpha(0.8f);
    private static readonly Vector4 ButtonHoveredColor  = ImGui.ColorConvertU32ToFloat4(ImGui.GetColorU32(ImGuiCol.ButtonHovered)).WithAlpha(0.4f);
    private static readonly Vector4 ButtonSelectedColor = ImGui.ColorConvertU32ToFloat4(ImGui.GetColorU32(ImGuiCol.Button)).WithAlpha(0.6f);
}
