using System.Collections.Frozen;
using System.Numerics;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Component.GUI;
using OmenTools.Interop.Game.Models;
using OmenTools.OmenService;

namespace DailyRoutines.ModulesPublic;

public unsafe class AutoHideBanners : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoHideBannersTitle"),
        Description = Lang.Get("AutoHideBannersDescription"),
        Category    = ModuleCategory.System,
        Author      = ["XSZYYS"]
    };

    public override ModulePermission Permission { get; } = new() { AllDefaultEnabled = true };
    
    private static readonly CompSig                        SetImageTextureSig = new("48 89 5C 24 ?? 57 48 83 EC 30 48 8B D9 89 91");
    private delegate        void*                          SetImageTextureDelegate(AtkUnitBase* addon, uint bannerID, uint a3, int soundEffectID);
    private                 Hook<SetImageTextureDelegate>? SetImageTextureHook;

    private Config config = null!;

    protected override void Init()
    {
        config = Config.Load(this) ?? new();

        var isAnyAdded = false;

        foreach (var bannerID in BannersData)
        {
            if (!config.HiddenBanners.TryAdd(bannerID, DefaultEnabledBanners.Contains(bannerID))) continue;
            isAnyAdded = true;
        }

        if (isAnyAdded)
            config.Save(this);

        SetImageTextureHook ??= SetImageTextureSig.GetHook<SetImageTextureDelegate>(SetImageTextureDetour);
        SetImageTextureHook.Enable();
        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PreDraw, "_WKSMissionChain", OnAddon);
    }

    protected override void Uninit() =>
        DService.Instance().AddonLifecycle.UnregisterListener(OnAddon);

    protected override void ConfigUI()
    {
        var tableSize = new Vector2(ImGui.GetContentRegionAvail().X - 2 * ImGui.GetStyle().ItemSpacing.X, 400f * GlobalUIScale);

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

        var size      = new Vector2(tableSize.X / 2, 128f * GlobalUIScale);
        var cursorPos = ImGui.GetCursorPos();

        ImGui.Image(texture.Handle, size);

        ImGui.SetCursorPos(cursorPos);

        using (ImRaii.PushColor(ImGuiCol.Button, ButtonNormalColor))
        using (ImRaii.PushColor(ImGuiCol.ButtonActive, ButtonActiveColor))
        using (ImRaii.PushColor(ImGuiCol.ButtonHovered, ButtonHoveredColor))
        using (ImRaii.PushColor(ImGuiCol.Button, ButtonSelectedColor, config.HiddenBanners.GetValueOrDefault(bannerID)))
        {
            if (ImGui.Button($"##{bannerID}_{cursorPos}", size))
            {
                config.HiddenBanners[bannerID] ^= true;
                config.Save(this);
            }
        }
    }

    private void OnAddon(AddonEvent type, AddonArgs args)
    {
        var addon = args.Addon.ToStruct();
        if (addon == null) return;

        addon->RootNode->ToggleVisibility(!ShouldHideWKSMissionChain(addon));
        addon->Hide(true, true, 1);
    }

    private void* SetImageTextureDetour(AtkUnitBase* addon, uint bannerID, uint a3, int soundEffectID)
    {
        if (IsWKSMissionChainBannerSelected(bannerID))
            return SetImageTextureHook.Original(addon, bannerID, a3, soundEffectID);

        return config.HiddenBanners.GetValueOrDefault(bannerID)
                   ? null
                   : SetImageTextureHook.Original(addon, bannerID, a3, soundEffectID);
    }

    private bool ShouldHideWKSMissionChain(AtkUnitBase* addon)
    {
        var imageNode = addon->UldManager.SearchSimpleNodeByType<AtkImageNode>(NodeType.Image);
        if (imageNode == null) return false;

        var iconID = imageNode->GetIconID();
        if (iconID == 0) return false;

        return IsWKSMissionChainBannerSelected(iconID);
    }

    private bool IsWKSMissionChainBannerSelected(uint iconID) =>
        WKSMissionChainBannerIDs.Contains(iconID) && config.HiddenBanners.GetValueOrDefault(iconID);
    
    private class Config : ModuleConfig
    {
        // true - 隐藏; false - 维持
        public Dictionary<uint, bool> HiddenBanners = [];
    }
    
    #region 常量

    private static readonly List<uint> BannersData =
    [
        120031, 120032, 120055, 120081, 120082, 120083, 120084, 120085, 120086,
        120093, 120094, 120095, 120096, 120141, 120142, 121081, 121082, 121561,
        121562, 121563, 128370, 128371, 128372, 128373, 128525, 128526,
        128527, 128528, 128529, 128530, 128531, 128532
    ];
    
    private static readonly FrozenSet<uint> WKSMissionChainBannerIDs = [128527, 128528, 128529, 128530, 128531, 128532];

    private static readonly FrozenSet<uint> DefaultEnabledBanners = [120031, 120032, 120055, 120095, 120096, 120141, 120142];

    private static readonly Vector4 ButtonNormalColor   = ImGui.GetColorU32(ImGuiCol.Button).ToVector4().WithAlpha(0f);
    private static readonly Vector4 ButtonActiveColor   = ImGui.GetColorU32(ImGuiCol.ButtonActive).ToVector4().WithAlpha(0.8f);
    private static readonly Vector4 ButtonHoveredColor  = ImGui.GetColorU32(ImGuiCol.ButtonHovered).ToVector4().WithAlpha(0.4f);
    private static readonly Vector4 ButtonSelectedColor = ImGui.GetColorU32(ImGuiCol.Button).ToVector4().WithAlpha(0.6f);

    #endregion
}
