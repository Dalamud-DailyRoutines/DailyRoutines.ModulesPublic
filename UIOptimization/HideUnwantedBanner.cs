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
    
    private static readonly Dictionary<uint, string> PredefinedBanners = new()
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
    public class Config : ModuleConfiguration
    {
        public HashSet<uint> HiddenBanners = [];
    }

    protected override unsafe void Init()
    {
        ModuleConfig = LoadConfig<Config>() ?? new Config();
        SetImageTextureHook ??= SetImageTextureSig.GetHook<SetImageTextureDelegate>(SetImageTextureDetour);
        SetImageTextureHook.Enable();
    }

    protected override void ConfigUI()
    {
        ImGui.TextWrapped(GetLoc("HideUnwantedBanner-HelpText"));
        ImGui.Separator();
        ImGui.Spacing();
        using var child = ImRaii.Child("BannerListChild", new Vector2(-1, 300 * GlobalFontScale), true);
        if (child)
        {
            using var table = ImRaii.Table("BannerList", 2, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingFixedFit);
            if (table)
            {
                ImGui.TableSetupColumn(GetLoc("Enable"), ImGuiTableColumnFlags.WidthFixed, 20 * GlobalFontScale);
                ImGui.TableSetupColumn(GetLoc("Name"), ImGuiTableColumnFlags.WidthFixed, 200 * GlobalFontScale);
                ImGui.TableHeadersRow();

                foreach (var banner in PredefinedBanners)
                {
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();

                    var isHidden = ModuleConfig.HiddenBanners.Contains(banner.Key);
                    if (ImGui.Checkbox($"##{banner.Key}", ref isHidden))
                    {
                        if (isHidden)
                            ModuleConfig.HiddenBanners.Add(banner.Key);
                        else
                            ModuleConfig.HiddenBanners.Remove(banner.Key);

                        SaveConfig(ModuleConfig);
                    }

                    ImGui.TableNextColumn();
                    ImGui.Text(banner.Value);
                }
            }
        }
    }

    private unsafe void SetImageTextureDetour(AtkUnitBase* addon, uint bannerID, uint a3, int soundEffectID)
    {
        var shouldHide = false;
        if (ModuleConfig != null && bannerID > 0)
            shouldHide = ModuleConfig.HiddenBanners.Contains(bannerID);
        SetImageTextureHook?.Original(addon, shouldHide ? 0 : bannerID, a3, soundEffectID);
    }
}

