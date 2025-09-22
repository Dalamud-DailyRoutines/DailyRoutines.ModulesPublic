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

    private static Hook<SetImageTextureDelegate>? setImageTextureHook;
    private static Config? moduleConfig;

    private unsafe delegate void SetImageTextureDelegate(AtkUnitBase* addon, int bannerID, int a3, int soundEffectID);
    
    private record BannerSetting(int ID, string Label, bool IsCustom = false);

    private readonly List<BannerSetting> predefinedBanners =
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

    private readonly HashSet<int> seenBanners = [];

    public class Config : ModuleConfiguration
    {
        public HashSet<int> HiddenBanners = [];
    }

    protected override unsafe void Init()
    {
        moduleConfig = LoadConfig<Config>();

        try
        {
            setImageTextureHook ??= SetImageTextureSig.GetHook<SetImageTextureDelegate>(OnSetImageTexture);
            setImageTextureHook.Enable();
        }
        catch (Exception ex)
        {
            DService.Log.Error(ex, "创建隐藏横幅Hook失败！");
        }
    }

    protected override void ConfigUI()
    {
        if (moduleConfig == null) 
            moduleConfig = LoadConfig<Config>() ?? new Config();

        ImGui.TextWrapped(GetLoc("HideUnwantedBanner-HelpText"));
        ImGui.Separator();

        if (seenBanners.Any())
        {
            ImGui.TextWrapped(GetLoc("HideUnwantedBanner-NewlyDetectedBannersHeader"));
            ImGui.Spacing();

            using var seenTable = ImRaii.Table("SeenBannersList", 2, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg);
            if (seenTable.Success)
            {
                ImGui.TableSetupColumn(GetLoc("HideUnwantedBanner-TableAdd"), ImGuiTableColumnFlags.WidthFixed);
                ImGui.TableSetupColumn(GetLoc("HideUnwantedBanner-TablePreview"));
                ImGui.TableHeadersRow();

                foreach (var bannerId in seenBanners.ToArray())
                {
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();

                    if (ImGui.Button($"{GetLoc("HideUnwantedBanner-ButtonAdd")}##{bannerId}"))
                    {
                        moduleConfig.HiddenBanners.Add(bannerId);
                        SaveConfig(moduleConfig);
                        seenBanners.Remove(bannerId);
                    }

                    ImGui.TableNextColumn();
                    if (DService.Texture.TryGetFromGameIcon((uint)bannerId, out var icon) && icon.GetWrapOrDefault() is { } wrap)
                        ImGui.Image(wrap.Handle, new Vector2(wrap.Width * 80f / wrap.Height, 80));
                    else
                        ImGui.Text(string.Format(GetLoc("HideUnwantedBanner-CustomBannerLabel"), bannerId));
                }
            }
            ImGui.Separator();
        }

        ImGui.Spacing();
        
        var allBanners = new List<BannerSetting>(predefinedBanners);
        foreach (var hiddenID in moduleConfig.HiddenBanners)
        {
            if (predefinedBanners.All(b => b.ID != hiddenID))
            {
                var customLabel = string.Format(GetLoc("HideUnwantedBanner-CustomBannerLabel"), hiddenID);
                allBanners.Add(new BannerSetting(hiddenID, customLabel, true));
            }
        }
        
        var sortedBanners = allBanners.OrderBy(b => b.IsCustom).ThenBy(b => b.Label);
        
        using var table = ImRaii.Table("BannerList", 2, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY);
        if (table.Success)
        {
            ImGui.TableSetupColumn(GetLoc("HideUnwantedBanner-TableEnable"), ImGuiTableColumnFlags.WidthFixed);
            ImGui.TableSetupColumn(GetLoc("HideUnwantedBanner-TableBannerName"));
            ImGui.TableHeadersRow();

            foreach (var banner in sortedBanners)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();

                var isHidden = moduleConfig.HiddenBanners.Contains(banner.ID);
                if (ImGui.Checkbox($"##{banner.ID}", ref isHidden))
                {
                    if (isHidden)
                        moduleConfig.HiddenBanners.Add(banner.ID);
                    else
                        moduleConfig.HiddenBanners.Remove(banner.ID);

                    SaveConfig(moduleConfig);
                }

                ImGui.TableNextColumn();
                ImGui.Text(banner.Label);
            }
        }
    }
    
    private unsafe void OnSetImageTexture(AtkUnitBase* addon, int bannerID, int a3, int soundEffectID)
    {
        var shouldHide = false;
        if (moduleConfig != null && bannerID > 0)
        {
            shouldHide = moduleConfig.HiddenBanners.Contains(bannerID);

            if (!shouldHide && predefinedBanners.All(b => b.ID != bannerID))
                seenBanners.Add(bannerID);
        }

        setImageTextureHook?.Original(addon, shouldHide ? 0 : bannerID, a3, shouldHide ? 0 : soundEffectID);
    }
}

