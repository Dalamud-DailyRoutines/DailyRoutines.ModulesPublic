using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Gui.Dtr;
using Dalamud.Interface.Utility.Raii;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using DailyRoutines.Abstracts;
using DailyRoutines.Managers;

namespace DailyRoutines.Modules;

public class MitigationCounter : DailyModuleBase
{
    #region Core

    public override ModuleInfo Info => new()
    {
        Title       = GetLoc("MitigationCounterTitle"),
        Description = GetLoc("MitigationCounterDescription"),
        Category    = ModuleCategories.Combat,
        Author      = ["HaKu"]
    };

    private static Config?       ModuleConfig;
    private static IDtrBarEntry? BarEntry;

    public override void Init()
    {
        ModuleConfig = LoadConfig<Config>() ?? new();

        // init hash map
        MitigationStatusMap = MitigationStatuses.ToDictionary(s => s.Id);

        // status bar
        BarEntry         ??= DService.DtrBar.Get("DailyRoutines-MitigationCounter");
        BarEntry.OnClick =   () => ChatHelper.Instance.SendMessage($"/pdr search {GetType().Name}");

        RefreshBarEntry();

        // life cycle hooks
        FrameworkManager.Register(false, OnFrameworkUpdate);
    }

    public override unsafe void Uninit()
    {
        FrameworkManager.Unregister(OnFrameworkUpdate);

        base.Uninit();
    }

    public override void ConfigUI()
    {
        ImGui.TextColored(LightSkyBlue, GetLoc("MitigationCounter-AccumulationMethod"));

        ImGui.Spacing();

        using (ImRaii.PushIndent())
        {
            if (ImGui.RadioButton($"{GetLoc("MitigationCounter-AdditiveTitle")} ({GetLoc("MitigationCounter-AdditiveDescription")})",
                                  ModuleConfig.AccumulationMethod == AccumulationMethodSet.Additive))
            {
                ModuleConfig.AccumulationMethod = AccumulationMethodSet.Additive;
                SaveConfig(ModuleConfig);
            }

            if (ImGui.RadioButton($"{GetLoc("MitigationCounter-MultiplicativeTitle")} ({GetLoc("MitigationCounter-MultiplicativeDescription")})",
                                  ModuleConfig.AccumulationMethod == AccumulationMethodSet.Multiplicative))
            {
                ModuleConfig.AccumulationMethod = AccumulationMethodSet.Multiplicative;
                SaveConfig(ModuleConfig);
            }
        }

        ImGui.NewLine();

        using (ImRaii.PushIndent())
        {
            if (ImGui.Checkbox(GetLoc("MitigationCounter-OnlyInCombat"), ref ModuleConfig.OnlyInCombat))
                SaveConfig(ModuleConfig);
        }
    }

    #endregion

    #region Hooks

    public static Dictionary<uint, MitigationStatus>? MitigationStatusMap;

    public static unsafe void OnFrameworkUpdate(IFramework _)
    {
        if (!Throttler.Throttle("MitigationCounter-OnFrameworkUpdate", 200)) return;

        // only available in combat and not in pvp
        if (DService.ClientState.IsPvP || (ModuleConfig.OnlyInCombat && !DService.Condition[ConditionFlag.InCombat]))
        {
            BarEntry.Shown = false;
            return;
        }

        // fetch local player (null when zone changed)
        if (DService.ClientState.LocalPlayer is not { } localPlayer)
        {
            BarEntry.Shown = false;
            return;
        }

        // count mitigation on local player
        var                    localPlayerStatus = localPlayer.StatusList;
        List<MitigationStatus> activeMitigation  = [];

        foreach (var status in localPlayerStatus)
            if (MitigationStatusMap.TryGetValue(status.StatusId, out var mitigation))
                activeMitigation.Add(mitigation);

        // count mitigation on battle npc
        var currentTarget = DService.Targets.Target;

        if (currentTarget is IBattleNpc battleNpc)
        {
            var statusList = battleNpc.ToBCStruct()->StatusManager.Status;
            foreach (var status in statusList)
                if (MitigationStatusMap.TryGetValue(status.StatusId, out var mitigation))
                    activeMitigation.Add(mitigation);
        }

        // count mitigation on party members
        var setActiveMitigation = activeMitigation.DistinctBy(m => m.Id).ToList();

        var physical = MitigationReduction(setActiveMitigation.Select(m => m.Mitigation.Physical).ToList());
        var magical  = MitigationReduction(setActiveMitigation.Select(m => m.Mitigation.Magical).ToList());
        var special  = MitigationReduction(setActiveMitigation.Select(m => m.Mitigation.Special).ToList());

        // update status bar
        RefreshBarEntry(physical * 100, magical * 100, special * 100);
    }

    #endregion

    #region Logics

    private static void RefreshBarEntry(float physical = 0, float magical = 0, float special = 0)
    {
        if (BarEntry is null)
            return;

        // build mitigation description
        var builder = new SeStringBuilder();
        var values  = new[] { physical, magical, special };

        var firstElement = true;
        for (var idx = 0; idx < values.Length; idx++)
        {
            if (values[idx] <= 0) continue;

            var icon = idx switch
            {
                0 => BitmapFontIcon.DamagePhysical,
                1 => BitmapFontIcon.DamageMagical,
                2 => BitmapFontIcon.DamageSpecial,
                _ => BitmapFontIcon.None,
            };

            if (!firstElement)
                builder.AddText(" ");

            builder.AddIcon(icon);
            builder.AddText($"{values[idx]:0.0}%");
            firstElement = false;
        }

        // update status bar
        BarEntry.Text  = builder.Build();
        BarEntry.Shown = true;
    }

    private static float MitigationReduction(List<float> mitigations) =>
        1f - ModuleConfig.AccumulationMethod switch
        {
            AccumulationMethodSet.Multiplicative => mitigations.Aggregate(1f, (acc, m) => acc * (1f - (m / 100f))),
            AccumulationMethodSet.Additive       => 1f - Math.Clamp(mitigations.Sum(m => m / 100f), 0, 1),
            _                                    => 0
        };

    #endregion

    #region Structs

    private class Config : ModuleConfiguration
    {
        public AccumulationMethodSet AccumulationMethod = AccumulationMethodSet.Multiplicative;
        public bool                  OnlyInCombat       = true;
    }

    public enum AccumulationMethodSet
    {
        Additive,
        Multiplicative,
    }

    public struct MitigationDetail
    {
        public float Physical;
        public float Magical;
        public float Special;
    }

    public struct MitigationStatus
    {
        public uint             Id;
        public string           Name;
        public MitigationDetail Mitigation;
        public bool             OnMember;
    }

    #endregion

    #region Storage

    public static readonly List<MitigationStatus> MitigationStatuses =
    [
        new()
        {
            Id         = 1191,
            Name       = "铁壁",
            Mitigation = new MitigationDetail { Physical = 10, Magical = 10, Special = 10 },
            OnMember   = true
        },
        new()
        {
            Id         = 1856,
            Name       = "盾阵",
            Mitigation = new MitigationDetail { Physical = 10, Magical = 10, Special = 10 },
            OnMember   = true
        },
        new()
        {
            Id         = 1174,
            Name       = "干预",
            Mitigation = new MitigationDetail { Physical = 10, Magical = 10, Special = 10 },
            OnMember   = true
        },
        new()
        {
            Id         = 74,
            Name       = "预警",
            Mitigation = new MitigationDetail { Physical = 10, Magical = 10, Special = 10 },
            OnMember   = true
        },
        new()
        {
            Id         = 1176,
            Name       = "武装",
            Mitigation = new MitigationDetail { Physical = 10, Magical = 10, Special = 10 },
            OnMember   = true
        },
        new()
        {
            Id         = 82,
            Name       = "神圣领域",
            Mitigation = new MitigationDetail { Physical = 10, Magical = 10, Special = 10 },
            OnMember   = true
        },
        new()
        {
            Id         = 1362,
            Name       = "圣光幕帘",
            Mitigation = new MitigationDetail { Physical = 10, Magical = 10, Special = 10 },
            OnMember   = true
        },
        new()
        {
            Id         = 2674,
            Name       = "圣盾阵",
            Mitigation = new MitigationDetail { Physical = 10, Magical = 10, Special = 10 },
            OnMember   = true
        },
        new()
        {
            Id         = 2675,
            Name       = "骑士的坚守",
            Mitigation = new MitigationDetail { Physical = 10, Magical = 10, Special = 10 },
            OnMember   = true
        },
        new()
        {
            Id         = 77,
            Name       = "壁垒",
            Mitigation = new MitigationDetail { Physical = 10, Magical = 10, Special = 10 },
            OnMember   = true
        },
        new()
        {
            Id         = 735,
            Name       = "原初的直觉",
            Mitigation = new MitigationDetail { Physical = 10, Magical = 10, Special = 10 },
            OnMember   = true
        },
        new()
        {
            Id         = 1857,
            Name       = "原初的勇猛",
            Mitigation = new MitigationDetail { Physical = 10, Magical = 10, Special = 10 },
            OnMember   = true
        },
        new()
        {
            Id         = 1858,
            Name       = "原初的武猛",
            Mitigation = new MitigationDetail { Physical = 10, Magical = 10, Special = 10 },
            OnMember   = true
        },
        new()
        {
            Id         = 87,
            Name       = "战栗",
            Mitigation = new MitigationDetail { Physical = 10, Magical = 10, Special = 10 },
            OnMember   = true
        },
        new()
        {
            Id         = 89,
            Name       = "复仇",
            Mitigation = new MitigationDetail { Physical = 10, Magical = 10, Special = 10 },
            OnMember   = true
        },
        new()
        {
            Id         = 1457,
            Name       = "摆脱",
            Mitigation = new MitigationDetail { Physical = 10, Magical = 10, Special = 10 },
            OnMember   = true
        },
        new()
        {
            Id         = 409,
            Name       = "死斗",
            Mitigation = new MitigationDetail { Physical = 10, Magical = 10, Special = 10 },
            OnMember   = true
        },
        new()
        {
            Id         = 2678,
            Name       = "原初的血气",
            Mitigation = new MitigationDetail { Physical = 10, Magical = 10, Special = 10 },
            OnMember   = true
        },
        new()
        {
            Id         = 2679,
            Name       = "原初的血潮",
            Mitigation = new MitigationDetail { Physical = 10, Magical = 10, Special = 10 },
            OnMember   = true
        },
        new()
        {
            Id         = 2680,
            Name       = "原初的血烟",
            Mitigation = new MitigationDetail { Physical = 10, Magical = 10, Special = 10 },
            OnMember   = true
        },
        new()
        {
            Id         = 1178,
            Name       = "至黑之夜",
            Mitigation = new MitigationDetail { Physical = 10, Magical = 10, Special = 10 },
            OnMember   = true
        },
        new()
        {
            Id         = 746,
            Name       = "弃明投暗",
            Mitigation = new MitigationDetail { Physical = 0, Magical = 10, Special = 0 },
            OnMember   = true
        },
        new()
        {
            Id         = 747,
            Name       = "暗影墙",
            Mitigation = new MitigationDetail { Physical = 10, Magical = 10, Special = 10 },
            OnMember   = true
        },
        new()
        {
            Id         = 1894,
            Name       = "暗黑布道",
            Mitigation = new MitigationDetail { Physical = 0, Magical = 10, Special = 0 },
            OnMember   = true
        },
        new()
        {
            Id         = 810,
            Name       = "行尸走肉",
            Mitigation = new MitigationDetail { Physical = 10, Magical = 10, Special = 10 },
            OnMember   = true
        },
        new()
        {
            Id         = 811,
            Name       = "死而不僵",
            Mitigation = new MitigationDetail { Physical = 10, Magical = 10, Special = 10 },
            OnMember   = true
        },
        new()
        {
            Id         = 3255,
            Name       = "出死入生",
            Mitigation = new MitigationDetail { Physical = 10, Magical = 10, Special = 10 },
            OnMember   = true
        },
        new()
        {
            Id         = 2682,
            Name       = "献奉",
            Mitigation = new MitigationDetail { Physical = 10, Magical = 10, Special = 10 },
            OnMember   = true
        },
        new()
        {
            Id         = 1840,
            Name       = "石之心",
            Mitigation = new MitigationDetail { Physical = 10, Magical = 10, Special = 10 },
            OnMember   = true
        },
        new()
        {
            Id         = 1898,
            Name       = "残暴弹",
            Mitigation = new MitigationDetail { Physical = 10, Magical = 10, Special = 10 },
            OnMember   = true
        },
        new()
        {
            Id         = 1832,
            Name       = "伪装",
            Mitigation = new MitigationDetail { Physical = 10, Magical = 10, Special = 10 },
            OnMember   = true
        },
        new()
        {
            Id         = 1834,
            Name       = "星云",
            Mitigation = new MitigationDetail { Physical = 10, Magical = 10, Special = 10 },
            OnMember   = true
        },
        new()
        {
            Id         = 1839,
            Name       = "光之心",
            Mitigation = new MitigationDetail { Physical = 0, Magical = 10, Special = 0 },
            OnMember   = true
        },
        new()
        {
            Id         = 1836,
            Name       = "超火流星",
            Mitigation = new MitigationDetail { Physical = 10, Magical = 10, Special = 10 },
            OnMember   = true
        },
        new()
        {
            Id         = 2683,
            Name       = "刚玉之心",
            Mitigation = new MitigationDetail { Physical = 10, Magical = 10, Special = 10 },
            OnMember   = true
        },
        new()
        {
            Id         = 2684,
            Name       = "刚玉之清",
            Mitigation = new MitigationDetail { Physical = 10, Magical = 10, Special = 10 },
            OnMember   = true
        },
        new()
        {
            Id         = 1218,
            Name       = "神祝祷",
            Mitigation = new MitigationDetail { Physical = 10, Magical = 10, Special = 10 },
            OnMember   = true
        },
        new()
        {
            Id         = 1873,
            Name       = "节制",
            Mitigation = new MitigationDetail { Physical = 10, Magical = 10, Special = 10 },
            OnMember   = true
        },
        new()
        {
            Id         = 2708,
            Name       = "水流幕",
            Mitigation = new MitigationDetail { Physical = 10, Magical = 10, Special = 10 },
            OnMember   = true
        },
        new()
        {
            Id         = 297,
            Name       = "鼓舞",
            Mitigation = new MitigationDetail { Physical = 10, Magical = 10, Special = 10 },
            OnMember   = true
        },
        new()
        {
            Id         = 1918,
            Name       = "激励",
            Mitigation = new MitigationDetail { Physical = 10, Magical = 10, Special = 10 },
            OnMember   = true
        },
        new()
        {
            Id         = 1917,
            Name       = "炽天的幕帘",
            Mitigation = new MitigationDetail { Physical = 10, Magical = 10, Special = 10 },
            OnMember   = true
        },
        new()
        {
            Id         = 299,
            Name       = "野战治疗阵",
            Mitigation = new MitigationDetail { Physical = 10, Magical = 10, Special = 10 },
            OnMember   = true
        },
        new()
        {
            Id         = 317,
            Name       = "异想的幻光",
            Mitigation = new MitigationDetail { Physical = 0, Magical = 10, Special = 0 },
            OnMember   = true
        },
        new()
        {
            Id         = 1875,
            Name       = "炽天的幻光",
            Mitigation = new MitigationDetail { Physical = 0, Magical = 10, Special = 0 },
            OnMember   = true
        },
        new()
        {
            Id         = 2710,
            Name       = "生命回生法",
            Mitigation = new MitigationDetail { Physical = 10, Magical = 10, Special = 10 },
            OnMember   = true
        },
        new()
        {
            Id         = 2711,
            Name       = "怒涛之计",
            Mitigation = new MitigationDetail { Physical = 10, Magical = 10, Special = 10 },
            OnMember   = true
        },
        new()
        {
            Id         = 849,
            Name       = "命运之轮",
            Mitigation = new MitigationDetail { Physical = 10, Magical = 10, Special = 10 },
            OnMember   = true
        },
        new()
        {
            Id         = 1889,
            Name       = "天星交错",
            Mitigation = new MitigationDetail { Physical = 10, Magical = 10, Special = 10 },
            OnMember   = true
        },
        new()
        {
            Id         = 2717,
            Name       = "擢升",
            Mitigation = new MitigationDetail { Physical = 10, Magical = 10, Special = 10 },
            OnMember   = true
        },
        new()
        {
            Id         = 1921,
            Name       = "中间学派",
            Mitigation = new MitigationDetail { Physical = 10, Magical = 10, Special = 10 },
            OnMember   = true
        },
        new()
        {
            Id         = 2607,
            Name       = "均衡诊断",
            Mitigation = new MitigationDetail { Physical = 10, Magical = 10, Special = 10 },
            OnMember   = true
        },
        new()
        {
            Id         = 2608,
            Name       = "齐衡诊断",
            Mitigation = new MitigationDetail { Physical = 10, Magical = 10, Special = 10 },
            OnMember   = true
        },
        new()
        {
            Id         = 2609,
            Name       = "均衡预后",
            Mitigation = new MitigationDetail { Physical = 10, Magical = 10, Special = 10 },
            OnMember   = true
        },
        new()
        {
            Id         = 2618,
            Name       = "坚角清汁",
            Mitigation = new MitigationDetail { Physical = 10, Magical = 10, Special = 10 },
            OnMember   = true
        },
        new()
        {
            Id         = 2619,
            Name       = "白牛清汁",
            Mitigation = new MitigationDetail { Physical = 10, Magical = 10, Special = 10 },
            OnMember   = true
        },
        new()
        {
            Id         = 3003,
            Name       = "整体论",
            Mitigation = new MitigationDetail { Physical = 10, Magical = 10, Special = 10 },
            OnMember   = true
        },
        new()
        {
            Id         = 3365,
            Name       = "整体盾",
            Mitigation = new MitigationDetail { Physical = 10, Magical = 10, Special = 10 },
            OnMember   = true
        },
        new()
        {
            Id         = 2612,
            Name       = "输血",
            Mitigation = new MitigationDetail { Physical = 10, Magical = 10, Special = 10 },
            OnMember   = true
        },
        new()
        {
            Id         = 2613,
            Name       = "泛输血",
            Mitigation = new MitigationDetail { Physical = 10, Magical = 10, Special = 10 },
            OnMember   = true
        },
        new()
        {
            Id         = 1232,
            Name       = "心眼",
            Mitigation = new MitigationDetail { Physical = 10, Magical = 10, Special = 10 },
            OnMember   = true
        },
        new()
        {
            Id         = 1179,
            Name       = "金刚极意",
            Mitigation = new MitigationDetail { Physical = 10, Magical = 10, Special = 10 },
            OnMember   = true
        },
        new()
        {
            Id         = 1934,
            Name       = "行吟",
            Mitigation = new MitigationDetail { Physical = 10, Magical = 10, Special = 10 },
            OnMember   = true
        },
        new()
        {
            Id         = 1951,
            Name       = "策动",
            Mitigation = new MitigationDetail { Physical = 10, Magical = 10, Special = 10 },
            OnMember   = true
        },
        new()
        {
            Id         = 1826,
            Name       = "防守之桑巴",
            Mitigation = new MitigationDetail { Physical = 10, Magical = 10, Special = 10 },
            OnMember   = true
        },
        new()
        {
            Id         = 2697,
            Name       = "即兴表演结束",
            Mitigation = new MitigationDetail { Physical = 10, Magical = 10, Special = 10 },
            OnMember   = true
        },
        new()
        {
            Id         = 488,
            Name       = "残影",
            Mitigation = new MitigationDetail { Physical = 10, Magical = 10, Special = 10 },
            OnMember   = true
        },
        new()
        {
            Id         = 168,
            Name       = "魔罩",
            Mitigation = new MitigationDetail { Physical = 10, Magical = 10, Special = 10 },
            OnMember   = true
        },
        new()
        {
            Id         = 2702,
            Name       = "守护之光",
            Mitigation = new MitigationDetail { Physical = 10, Magical = 10, Special = 10 },
            OnMember   = true
        },
        new()
        {
            Id         = 2597,
            Name       = "守护纹",
            Mitigation = new MitigationDetail { Physical = 10, Magical = 10, Special = 10 },
            OnMember   = true
        },
        new()
        {
            Id         = 2707,
            Name       = "抗死",
            Mitigation = new MitigationDetail { Physical = 0, Magical = 10, Special = 0 },
            OnMember   = true
        },
        new()
        {
            Id         = 1193,
            Name       = "雪仇",
            Mitigation = new MitigationDetail { Physical = 10, Magical = 10, Special = 10 },
            OnMember   = false
        },
        new()
        {
            Id         = 1195,
            Name       = "牵制",
            Mitigation = new MitigationDetail { Physical = 10, Magical = 5, Special = 0 },
            OnMember   = false
        },
        new()
        {
            Id         = 1203,
            Name       = "昏乱",
            Mitigation = new MitigationDetail { Physical = 5, Magical = 10, Special = 0 },
            OnMember   = false
        },
        new()
        {
            Id         = 860,
            Name       = "武装解除",
            Mitigation = new MitigationDetail { Physical = 10, Magical = 10, Special = 10 },
            OnMember   = false
        },
        new()
        {
            Id         = 9,
            Name       = "减速",
            Mitigation = new MitigationDetail { Physical = 10, Magical = 10, Special = 10 },
            OnMember   = false
        },
        new()
        {
            Id         = 2120,
            Name       = "体力增加",
            Mitigation = new MitigationDetail { Physical = 10, Magical = 10, Special = 10 },
            OnMember   = true
        },
        new()
        {
            Id         = 1715,
            Name       = "腐臭",
            Mitigation = new MitigationDetail { Physical = 10, Magical = 10, Special = 10 },
            OnMember   = false
        },
        new()
        {
            Id         = 2115,
            Name       = "智力精神降低",
            Mitigation = new MitigationDetail { Physical = 0, Magical = 10, Special = 0 },
            OnMember   = false
        },
        new()
        {
            Id         = 2500,
            Name       = "龙之力",
            Mitigation = new MitigationDetail { Physical = 10, Magical = 10, Special = 10 },
            OnMember   = true
        },
        new()
        {
            Id         = 1722,
            Name       = "超硬化",
            Mitigation = new MitigationDetail { Physical = 10, Magical = 10, Special = 10 },
            OnMember   = true
        },
        new()
        {
            Id         = 2496,
            Name       = "玄结界",
            Mitigation = new MitigationDetail { Physical = 10, Magical = 10, Special = 10 },
            OnMember   = true
        },
        new()
        {
            Id         = 2119,
            Name       = "仙人盾",
            Mitigation = new MitigationDetail { Physical = 10, Magical = 10, Special = 10 },
            OnMember   = true
        },
        new()
        {
            Id         = 1719,
            Name       = "强力守护",
            Mitigation = new MitigationDetail { Physical = 10, Magical = 10, Special = 10 },
            OnMember   = true
        },
        new()
        {
            Id         = 2114,
            Name       = "哥布防御",
            Mitigation = new MitigationDetail { Physical = 10, Magical = 10, Special = 10 },
            OnMember   = true
        },
        new()
        {
            Id         = 194,
            Name       = "铜墙铁盾",
            Mitigation = new MitigationDetail { Physical = 10, Magical = 10, Special = 10 },
            OnMember   = true
        },
        new()
        {
            Id         = 195,
            Name       = "坚守要塞",
            Mitigation = new MitigationDetail { Physical = 10, Magical = 10, Special = 10 },
            OnMember   = true
        },
        new()
        {
            Id         = 196,
            Name       = "终极堡垒",
            Mitigation = new MitigationDetail { Physical = 10, Magical = 10, Special = 10 },
            OnMember   = true
        },
        new()
        {
            Id         = 863,
            Name       = "原初大地",
            Mitigation = new MitigationDetail { Physical = 10, Magical = 10, Special = 10 },
            OnMember   = true
        },
        new()
        {
            Id         = 864,
            Name       = "暗黑之力",
            Mitigation = new MitigationDetail { Physical = 10, Magical = 10, Special = 10 },
            OnMember   = true
        },
        new()
        {
            Id         = 1931,
            Name       = "灵魂之青",
            Mitigation = new MitigationDetail { Physical = 10, Magical = 10, Special = 10 },
            OnMember   = true
        },
        new()
        {
            Id         = 3829,
            Name       = "极致防御",
            Mitigation = new MitigationDetail { Physical = 10, Magical = 10, Special = 10 },
            OnMember   = true
        },
        new()
        {
            Id         = 3830,
            Name       = "极致护盾",
            Mitigation = new MitigationDetail { Physical = 10, Magical = 10, Special = 10 },
            OnMember   = true
        },
        new()
        {
            Id         = 3832,
            Name       = "戮罪",
            Mitigation = new MitigationDetail { Physical = 10, Magical = 10, Special = 10 },
            OnMember   = true
        },
        new()
        {
            Id         = 3835,
            Name       = "暗影卫",
            Mitigation = new MitigationDetail { Physical = 10, Magical = 10, Special = 10 },
            OnMember   = true
        },
        new()
        {
            Id         = 3838,
            Name       = "大星云",
            Mitigation = new MitigationDetail { Physical = 10, Magical = 10, Special = 10 },
            OnMember   = true
        },
        new()
        {
            Id         = 3686,
            Name       = "坦培拉涂层",
            Mitigation = new MitigationDetail { Physical = 10, Magical = 10, Special = 10 },
            OnMember   = true
        },
        new()
        {
            Id         = 3687,
            Name       = "油性坦培拉涂层",
            Mitigation = new MitigationDetail { Physical = 10, Magical = 10, Special = 10 },
            OnMember   = true
        },
        new()
        {
            Id         = 3890,
            Name       = "世界树之干",
            Mitigation = new MitigationDetail { Physical = 10, Magical = 10, Special = 10 },
            OnMember   = true
        },
        new()
        {
            Id         = 3892,
            Name       = "建筑神之塔",
            Mitigation = new MitigationDetail { Physical = 10, Magical = 10, Special = 10 },
            OnMember   = true
        },
        new()
        {
            Id         = 3896,
            Name       = "太阳星座",
            Mitigation = new MitigationDetail { Physical = 10, Magical = 10, Special = 10 },
            OnMember   = true
        },
        new()
        {
            Id         = 3903,
            Name       = "神爱抚",
            Mitigation = new MitigationDetail { Physical = 10, Magical = 10, Special = 10 },
            OnMember   = true
        }
    ];

    #endregion
}
