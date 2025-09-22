using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using DailyRoutines.Abstracts;
using DailyRoutines.Infos;
using Dalamud.Bindings.ImGui;
using Dalamud.Hooking;
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
        Title = GetLoc("隐藏横幅"),
        Description = GetLoc("隐藏游戏中不想要的横幅,比如“理符任务”,“危命任务”等"),
        Category = ModuleCategories.UIOptimization,
        Author = ["XSZYYS"]
    };

    private static readonly CompSig SetImageTextureSig = new("48 89 5C 24 ?? 57 48 83 EC 30 48 8B D9 89 91");

    private static Hook<SetImageTextureDelegate>? SetImageTextureHook;
    private static Config? ModuleConfig;

    private unsafe delegate void SetImageTextureDelegate(AtkUnitBase* addon, int bannerId, int a3, int soundEffectId);
    
    private record BannerSetting(int Id, string Label, bool IsCustom = false);

    private readonly List<BannerSetting> predefinedBanners = new()
    {
        new BannerSetting(120031, GetLoc("理符任务：接受")),
        new BannerSetting(120032, GetLoc("理符任务：完成")),
        new BannerSetting(120055, GetLoc("筹备任务：交付完成")),
        new BannerSetting(120081, GetLoc("危命任务：加入")),
        new BannerSetting(120082, GetLoc("危命任务：完成")),
        new BannerSetting(120083, GetLoc("危命任务：失败")),
        new BannerSetting(120084, GetLoc("危命任务：加入（经验奖励）")),
        new BannerSetting(120085, GetLoc("危命任务：完成（经验奖励）")),
        new BannerSetting(120086, GetLoc("危命任务：失败（经验奖励）")),
        new BannerSetting(120093, GetLoc("获得宝藏！")),
        new BannerSetting(120094, GetLoc("发现宝藏！")),
        new BannerSetting(120095, GetLoc("雇员探险：已派出")),
        new BannerSetting(120096, GetLoc("雇员探险：已归来")),
        new BannerSetting(120141, GetLoc("部队探险：已出发")),
        new BannerSetting(120142, GetLoc("部队探险：已归航")),
        new BannerSetting(121081, GetLoc("友好部族任务：接受")),
        new BannerSetting(121082, GetLoc("友好部族任务：完成")),
        new BannerSetting(121561, GetLoc("金碟游乐场：加入")),
        new BannerSetting(121562, GetLoc("金碟游乐场：完成")),
        new BannerSetting(121563, GetLoc("金碟游乐场：失败")),
        new BannerSetting(128370, GetLoc("宇宙探索：开始")),
        new BannerSetting(128371, GetLoc("宇宙探索：放弃")),
        new BannerSetting(128372, GetLoc("宇宙探索：失败")),
        new BannerSetting(128373, GetLoc("宇宙探索：完成")),
    };

    private readonly HashSet<int> seenBanners = new();

    public class Config : ModuleConfiguration
    {
        public HashSet<int> HiddenBanners = new();
    }

    protected override unsafe void Init()
    {
        ModuleConfig = LoadConfig<Config>();

        try
        {
            SetImageTextureHook ??= SetImageTextureSig.GetHook<SetImageTextureDelegate>(OnSetImageTexture);
            SetImageTextureHook.Enable();
        }
        catch (Exception ex)
        {
            DService.Log.Error(ex, "创建隐藏横幅Hook失败！");
        }
    }

    protected override void ConfigUI()
    {
        if (ModuleConfig == null) return;

        ImGui.TextWrapped(GetLoc("请勾选您希望隐藏的横幅。"));
        ImGui.Separator();

        ImGui.SetNextItemWidth(250 * ImGuiHelpers.GlobalScale);

        using (var combo = ImRaii.Combo("##customBannerPicker", GetLoc("添加其他横幅...")))
        {
            if (combo.Success)
            {
                if (seenBanners.Count == 0)
                    ImGui.TextWrapped(GetLoc("新检测到的横幅将显示在此处。"));

                foreach (var bannerId in seenBanners.ToArray())
                {
                    try
                    {
                        var icon = DService.Texture.GetFromGameIcon((uint)bannerId).GetWrapOrDefault();
                        if (icon != null)
                        {
                            if (ImGui.ImageButton(icon.Handle, new Vector2(icon.Width * 80f / icon.Height, 80)))
                            {
                                ModuleConfig.HiddenBanners.Add(bannerId);
                                SaveConfig(ModuleConfig);
                                seenBanners.Remove(bannerId);
                            }
                        }
                    }
                    catch
                    {
                        // 忽略无效的图标ID，防止插件UI崩溃
                    }
                }
            }
        }

        ImGui.Spacing();

        var allBanners = new List<BannerSetting>(predefinedBanners);
        foreach (var hiddenId in ModuleConfig.HiddenBanners )
        {
            if (predefinedBanners.All(b => b.Id != hiddenId))
            {
                var customLabel = string.Format(GetLoc("自定义横幅 #{0}"), hiddenId);
                allBanners.Add(new BannerSetting(hiddenId, customLabel, true));
            }
        }
        var sortedBanners = allBanners.OrderBy(b => b.IsCustom).ThenBy(b => b.Label);
        
        using (var table = ImRaii.Table("BannerList", 2, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY))
        {
            if (table.Success)
            {
                ImGui.TableSetupColumn(GetLoc("启用"), ImGuiTableColumnFlags.WidthFixed);
                ImGui.TableSetupColumn(GetLoc("横幅名称"));
                ImGui.TableHeadersRow();

                foreach (var banner in sortedBanners)
                {
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();

                    var isHidden = ModuleConfig.HiddenBanners.Contains(banner.Id);
                    if (ImGui.Checkbox($"##{banner.Id}", ref isHidden))
                    {
                        if (isHidden)
                            ModuleConfig.HiddenBanners.Add(banner.Id);
                        else
                            ModuleConfig.HiddenBanners.Remove(banner.Id);

                        SaveConfig(ModuleConfig);
                    }

                    ImGui.TableNextColumn();
                    ImGui.Text(banner.Label);
                }
            }
        }
    }

    protected override void Uninit()
    {
        SetImageTextureHook?.Dispose();
        SetImageTextureHook = null;
    }

    private unsafe void OnSetImageTexture(AtkUnitBase* addon, int bannerId, int a3, int soundEffectId)
    {
        var shouldHide = false;
        if (ModuleConfig != null && bannerId > 0)
        {
            shouldHide = ModuleConfig.HiddenBanners.Contains(bannerId);

            if (!shouldHide && predefinedBanners.All(b => b.Id != bannerId))
                seenBanners.Add(bannerId);
        }

        SetImageTextureHook?.Original(addon, shouldHide ? 0 : bannerId, a3, shouldHide ? 0 : soundEffectId);
    }
}

