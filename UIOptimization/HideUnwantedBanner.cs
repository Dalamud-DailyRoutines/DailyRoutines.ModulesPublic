using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using DailyRoutines.Abstracts;
using DailyRoutines.Infos;
using static DailyRoutines.Infos.Widgets;
using Dalamud.Bindings.ImGui;
using Dalamud.Hooking;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using FFXIVClientStructs.FFXIV.Component.GUI;
using OmenTools;
using OmenTools.Helpers;
using OmenTools.Infos;

namespace DailyRoutines.Modules;

public class HideUnwantedBanner : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title = GetLoc("HideUnwantedBannerTitle"),
        Description = GetLoc("HideUnwantedBannerDescription"),
        Category = ModuleCategories.UIOptimization,
        Author = ["XSZYYS"]
    };

    private static readonly CompSig SetImageTextureSig = new("48 89 5C 24 ?? 57 48 83 EC 30 48 8B D9 89 91");
    private unsafe delegate void SetImageTextureDelegate(AtkUnitBase* addon, int bannerID, int a3, int soundEffectID);
    private static Hook<SetImageTextureDelegate>? SetImageTextureHook;
    private static Config? ModuleConfig;
    
    private record BannerSetting(int ID, string Label, bool IsCustom = false);

    private static readonly List<BannerSetting> predefinedBanners =
    [
        new BannerSetting(120031, GetLoc("HideUnwantedBanner-LevequestAccepted")),
        new BannerSetting(120032, GetLoc("HideUnwantedBanner-LevequestComplete")),
        new BannerSetting(120055, GetLoc("HideUnwantedBanner-DeliveryComplete")),
        new BannerSetting(120081, GetLoc("HideUnwantedBanner-FATEJoined")),
        new BannerSetting(120082, GetLoc("HideUnwantedBanner-FATEComplete")),
        new BannerSetting(120083, GetLoc("HideUnwantedBanner-FATEFailed")),
        new BannerSetting(120084, GetLoc("HideUnwantedBanner-FATEJoinedEXPBonus")),
        new BannerSetting(120085, GetLoc("HideUnwantedBanner-FATECompleteEXPBonus")),
        new BannerSetting(120086, GetLoc("HideUnwantedBanner-FATEFailedEXPBonus")),
        new BannerSetting(120093, GetLoc("HideUnwantedBanner-TreasureObtained")),
        new BannerSetting(120094, GetLoc("HideUnwantedBanner-TreasureFound")),
        new BannerSetting(120095, GetLoc("HideUnwantedBanner-VentureCommenced")),
        new BannerSetting(120096, GetLoc("HideUnwantedBanner-VentureAccomplished")),
        new BannerSetting(120141, GetLoc("HideUnwantedBanner-VoyageCommenced")),
        new BannerSetting(120142, GetLoc("HideUnwantedBanner-VoyageComplete")),
        new BannerSetting(121081, GetLoc("HideUnwantedBanner-TribalQuestAccepted")),
        new BannerSetting(121082, GetLoc("HideUnwantedBanner-TribalQuestComplete")),
        new BannerSetting(121561, GetLoc("HideUnwantedBanner-GATEJoined")),
        new BannerSetting(121562, GetLoc("HideUnwantedBanner-GATEComplete")),
        new BannerSetting(121563, GetLoc("HideUnwantedBanner-GATEFailed")),
        new BannerSetting(128370, GetLoc("HideUnwantedBanner-StellarMissionCommenced")),
        new BannerSetting(128371, GetLoc("HideUnwantedBanner-StellarMissionAbandoned")),
        new BannerSetting(128372, GetLoc("HideUnwantedBanner-StellarMissionFailed")),
        new BannerSetting(128373, GetLoc("HideUnwantedBanner-StellarMissionComplete"))
    ];
    private static readonly HashSet<int> PredefinedBannerIDs = predefinedBanners.Select(b => b.ID).ToHashSet();
    private static readonly HashSet<int> SeenBanners = [];

    public class Config : ModuleConfiguration
    {
        public HashSet<int> HiddenBanners = [];
    }

    protected override unsafe void Init()
    {
        ModuleConfig = LoadConfig<Config>() ?? new Config();
        SetImageTextureHook ??= SetImageTextureSig.GetHook<SetImageTextureDelegate>(OnSetImageTextureDetour);
        SetImageTextureHook.Enable();
    }

    protected override void ConfigUI()
    {
        ImGui.TextWrapped(GetLoc("HideUnwantedBanner-HelpText"));
        ImGui.Separator();

        if (SeenBanners.Count > 0)
        {
            ImGui.TextWrapped(GetLoc("HideUnwantedBanner-NewlyDetectedBannersHeader"));
            ImGui.Spacing();

            using var seenTable = ImRaii.Table("SeenBannersList", 2, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg);
            ImGui.TableSetupColumn(GetLoc("Add"), ImGuiTableColumnFlags.WidthFixed);
            ImGui.TableSetupColumn(GetLoc("Preview"));
            ImGui.TableHeadersRow();
            foreach (var bannerID in new List<int>(SeenBanners))
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();

                if (ImGui.Button($"{GetLoc("Add")}##{bannerID}"))
                {
                    ModuleConfig.HiddenBanners.Add(bannerID);
                    SaveConfig(ModuleConfig);
                    SeenBanners.Remove(bannerID);
                }
                ImGui.TableNextColumn();
                if (DService.Texture.TryGetFromGameIcon((uint)bannerID, out var icon))
                {
                    var wrap = icon.GetWrapOrDefault();
                    if (wrap != null)
                    {
                        var iconHeight = ImGui.GetTextLineHeight() * 4;
                        var iconWidth = wrap.Width * iconHeight / wrap.Height;
                        ImGui.Image(wrap.Handle, new Vector2(iconWidth, iconHeight));
                    }
                    else
                    {
                        ImGui.Text(GetLoc("HideUnwantedBanner-CustomBannerLabel", bannerID));
                        if (ImGui.IsItemHovered())
                            ImGui.SetTooltip($"Icon found for {bannerID}, but failed to create texture wrap.");
                    }
                }
                else
                {
                    ImGui.Text(GetLoc("HideUnwantedBanner-CustomBannerLabel", bannerID));
                    if (ImGui.IsItemHovered()) 
                        ImGui.SetTooltip($"Could not find icon for ID: {bannerID}");
                }
            }
            ImGui.Separator();
        }
        ImGui.Spacing();
        var allBanners = new List<BannerSetting>(predefinedBanners);
        foreach (var hiddenID in ModuleConfig.HiddenBanners)
        {
            bool isPredefined = false;
            foreach (var pBanner in predefinedBanners)
            {
                if (pBanner.ID == hiddenID)
                {
                    isPredefined = true;
                    break;
                }
            }

            if (!isPredefined)
            {
                var customLabel = GetLoc("HideUnwantedBanner-CustomBannerLabel", hiddenID);
                allBanners.Add(new BannerSetting(hiddenID, customLabel, true));
            }
        }
        
        allBanners.Sort((b1, b2) =>
        {
            int customCompare = b1.IsCustom.CompareTo(b2.IsCustom);
            if (customCompare != 0) return customCompare;
            return string.Compare(b1.Label, b2.Label, StringComparison.Ordinal);
        });
        
        using var table = ImRaii.Table("BannerList", 2, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY);
        ImGui.TableSetupColumn(GetLoc("Enable"), ImGuiTableColumnFlags.WidthFixed);
        ImGui.TableSetupColumn(GetLoc("Name"));
        ImGui.TableHeadersRow();

        foreach (var banner in allBanners)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();

            var isHidden = ModuleConfig.HiddenBanners.Contains(banner.ID);
            if (ImGui.Checkbox($"##{banner.ID}", ref isHidden))
            {
                if (isHidden)
                    ModuleConfig.HiddenBanners.Add(banner.ID);
                else
                    ModuleConfig.HiddenBanners.Remove(banner.ID);

                SaveConfig(ModuleConfig);
            }
            ImGui.TableNextColumn();
            ImGui.Text(banner.Label);
        }
    }
    
    private unsafe void OnSetImageTextureDetour(AtkUnitBase* addon, int bannerID, int a3, int soundEffectID)
    {
        var shouldHide = false;
        if (ModuleConfig != null && bannerID > 0)
        {
            shouldHide = ModuleConfig.HiddenBanners.Contains(bannerID);
            if (!shouldHide && !PredefinedBannerIDs.Contains(bannerID))
                SeenBanners.Add(bannerID);
        }
        SetImageTextureHook?.Original(addon, shouldHide ? 0 : bannerID, a3, shouldHide ? 0 : soundEffectID);
    }
}

