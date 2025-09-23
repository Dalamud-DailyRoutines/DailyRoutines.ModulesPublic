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
    private unsafe delegate void SetImageTextureDelegate(AtkUnitBase* addon, uint bannerID, uint a3, int soundEffectID);
    private static Hook<SetImageTextureDelegate>? SetImageTextureHook;
    private static Config? ModuleConfig;
    
    private record BannerSetting(int ID, string Label);
    
    private static readonly Dictionary<int, string> PredefinedBanners = new()
    {
        [120031] = GetLoc("HideUnwantedBanner-LevequestAccepted"),
        [120032] = GetLoc("HideUnwantedBanner-LevequestComplete"),
        [120055] = GetLoc("HideUnwantedBanner-DeliveryComplete"),
        [120081] = GetLoc("HideUnwantedBanner-FATEJoined"),
        [120082] = GetLoc("HideUnwantedBanner-FATEComplete"),
        [120083] = GetLoc("HideUnwantedBanner-FATEFailed"),
        [120084] = GetLoc("HideUnwantedBanner-FATEJoinedEXPBonus"),
        [120085] = GetLoc("HideUnwantedBanner-FATECompleteEXPBonus"),
        [120086] = GetLoc("HideUnwantedBanner-FATEFailedEXPBonus"),
        [120093] = GetLoc("HideUnwantedBanner-TreasureObtained"),
        [120094] = GetLoc("HideUnwantedBanner-TreasureFound"),
        [120095] = GetLoc("HideUnwantedBanner-VentureCommenced"),
        [120096] = GetLoc("HideUnwantedBanner-VentureAccomplished"),
        [120141] = GetLoc("HideUnwantedBanner-VoyageCommenced"),
        [120142] = GetLoc("HideUnwantedBanner-VoyageComplete"),
        [121081] = GetLoc("HideUnwantedBanner-TribalQuestAccepted"),
        [121082] = GetLoc("HideUnwantedBanner-TribalQuestComplete"),
        [121561] = GetLoc("HideUnwantedBanner-GATEJoined"),
        [121562] = GetLoc("HideUnwantedBanner-GATEComplete"),
        [121563] = GetLoc("HideUnwantedBanner-GATEFailed"),
        [128370] = GetLoc("HideUnwantedBanner-StellarMissionCommenced"),
        [128371] = GetLoc("HideUnwantedBanner-StellarMissionAbandoned"),
        [128372] = GetLoc("HideUnwantedBanner-StellarMissionFailed"),
        [128373] = GetLoc("HideUnwantedBanner-StellarMissionComplete")
    };
    private static readonly Dictionary<int, bool> SeenBanners = [];
    private static List<BannerSetting> SortedPredefinedBanners = [];

    public class Config : ModuleConfiguration
    {
        public HashSet<int> HiddenBanners = [];
    }

    protected override unsafe void Init()
    {
        ModuleConfig = LoadConfig<Config>() ?? new Config();
        SetImageTextureHook ??= SetImageTextureSig.GetHook<SetImageTextureDelegate>(OnSetImageTextureDetour);
        SetImageTextureHook.Enable();
        
        SortedPredefinedBanners = new List<BannerSetting>();
        foreach (var kvp in PredefinedBanners)
            SortedPredefinedBanners.Add(new BannerSetting(kvp.Key, kvp.Value));
        SortedPredefinedBanners.Sort((b1, b2) => string.Compare(b1.Label, b2.Label, StringComparison.Ordinal));
    }

    protected override void ConfigUI()
    {
        ImGui.TextWrapped(GetLoc("HideUnwantedBanner-HelpText"));
        ImGui.Separator();

        if (SeenBanners.Count > 0)
        {
            ImGui.TextWrapped(GetLoc("HideUnwantedBanner-NewlyDetectedBannersHeader"));
            ImGui.Spacing();
            
            using var seenTable = ImRaii.Table("SeenBannersList", 2, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit);
            if (seenTable.Success)
            {
                ImGui.TableSetupColumn(GetLoc("Add"), ImGuiTableColumnFlags.WidthFixed, 50 * GlobalFontScale);
                ImGui.TableSetupColumn(GetLoc("Preview"), ImGuiTableColumnFlags.WidthFixed, 200 * GlobalFontScale);
                ImGui.TableHeadersRow();

                foreach (var bannerID in SeenBanners.Keys.ToList())
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
                    ImGui.Text(GetLoc("HideUnwantedBanner-CustomBannerLabel", bannerID));
                }
            }
            
            ImGui.Separator();
        }
        ImGui.Spacing();
        
        using var table = ImRaii.Table("BannerList", 2, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingFixedFit);
        if (table.Success)
        {
            ImGui.TableSetupColumn(GetLoc("Enable"), ImGuiTableColumnFlags.WidthFixed, 50 * GlobalFontScale);
            ImGui.TableSetupColumn(GetLoc("Name"), ImGuiTableColumnFlags.WidthFixed, 200 * GlobalFontScale);
            ImGui.TableHeadersRow();

            foreach (var banner in SortedPredefinedBanners)
                DrawBannerTableRow(banner);

            foreach (var hiddenID in ModuleConfig.HiddenBanners)
            {
                if (!PredefinedBanners.ContainsKey(hiddenID))
                {
                    var customLabel = GetLoc("HideUnwantedBanner-CustomBannerLabel", hiddenID);
                    DrawBannerTableRow(new BannerSetting(hiddenID, customLabel));
                }
            }
        }
    }
    
    private void DrawBannerTableRow(BannerSetting banner)
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
    
    private unsafe void OnSetImageTextureDetour(AtkUnitBase* addon, uint bannerID, uint a3, int soundEffectID)
    {
        var shouldHide = false;
        if (ModuleConfig != null && bannerID > 0)
        {
            shouldHide = ModuleConfig.HiddenBanners.Contains((int)bannerID);
            if (!shouldHide && !PredefinedBanners.ContainsKey((int)bannerID))
                SeenBanners.TryAdd((int)bannerID, true);
        }
        SetImageTextureHook?.Original(addon, shouldHide ? 0 : bannerID, a3, soundEffectID);
    }
}

