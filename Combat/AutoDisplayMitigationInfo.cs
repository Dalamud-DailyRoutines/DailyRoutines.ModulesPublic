using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Gui.Dtr;
using Dalamud.Interface.Utility.Raii;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using DailyRoutines.Abstracts;
using DailyRoutines.Managers;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Interface.Utility;
using Dalamud.Plugin.Services;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using Lumina.Excel.Sheets;

namespace DailyRoutines.Modules;

public class AutoDisplayMitigationInfo : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("AutoDisplayMitigationInfoTitle"),
        Description = GetLoc("AutoDisplayMitigationInfoDescription"),
        Category    = ModuleCategories.Combat,
        Author      = ["HaKu"]
    };
    
    private static readonly Dictionary<uint, MitigationStatus> MitigationStatusMap;
    private static readonly byte[]                             DamagePhysicalStr;
    private static readonly byte[]                             DamageMagicalStr;

    private static Config        ModuleConfig = null!;
    private static IDtrBarEntry? BarEntry;

    private static Dictionary<MitigationStatus, float> LastMitigationStatus = [];

    static AutoDisplayMitigationInfo()
    {
        MitigationStatusMap = MitigationStatuses.ToDictionary(s => s.Id);
        
        DamagePhysicalStr = new SeString(new IconPayload(BitmapFontIcon.DamagePhysical)).Encode();
        DamageMagicalStr  = new SeString(new IconPayload(BitmapFontIcon.DamageMagical)).Encode();
    }

    public override void Init()
    {
        ModuleConfig = LoadConfig<Config>() ?? new();

        Overlay            ??= new(this);
        Overlay.WindowName =   GetLoc("AutoDisplayMitigationInfoTitle");
        Overlay.Flags      &=  ~ImGuiWindowFlags.NoTitleBar;
        Overlay.Flags      &=  ~ImGuiWindowFlags.NoResize;
        Overlay.Flags      &=  ~ImGuiWindowFlags.AlwaysAutoResize;
        
        BarEntry         ??= DService.DtrBar.Get("DailyRoutines-AutoDisplayMitigationInfo");
        BarEntry.OnClick = () =>
        {
            if (Overlay == null) return;
            Overlay.IsOpen ^= true;
        };
        
        FrameworkManager.Register(false, OnFrameworkUpdate);
    }
    
    public override void ConfigUI()
    {
        if (ImGui.Checkbox(GetLoc("OnlyInCombat"), ref ModuleConfig.OnlyInCombat))
            SaveConfig(ModuleConfig);
    }

    public override unsafe void OverlayUI()
    {
        var localPlayer = Control.GetLocalPlayer();
        if (localPlayer == null) return;
        
        ImGuiHelpers.SeStringWrapped(BarEntry?.Text?.Encode() ?? []);

        ImGui.Separator();

        using (var table = ImRaii.Table("StatusTable", 3))
        {
            if (!table) return;
            
            ImGui.TableSetupColumn("图标", ImGuiTableColumnFlags.WidthFixed,   24f * GlobalFontScale);
            ImGui.TableSetupColumn("名字", ImGuiTableColumnFlags.WidthStretch, 20);
            ImGui.TableSetupColumn("数字", ImGuiTableColumnFlags.WidthStretch, 20);
            
            if (!DService.Texture.TryGetFromGameIcon(new(210405), out var barrierIcon)) return;
            
            foreach (var status in LastMitigationStatus)
            {
                if (!LuminaGetter.TryGetRow<Status>(status.Key.Id, out var row)) continue;
                if (!DService.Texture.TryGetFromGameIcon(new(row.Icon), out var icon)) continue;
                
                ImGui.TableNextRow();

                ImGui.TableNextColumn();
                ImGui.Image(icon.GetWrapOrEmpty().ImGuiHandle, ScaledVector2(24f));
                
                ImGui.TableNextColumn();
                ImGui.AlignTextToFramePadding();
                ImGui.Text($"{row.Name} ({status.Value:F1}s)");
                ImGuiOm.TooltipHover($"{status.Key.Id}");
                
                ImGui.TableNextColumn();
                ImGuiHelpers.SeStringWrapped(DamagePhysicalStr);
                
                ImGui.SameLine();
                ImGui.Text($"{status.Key.Mitigation.Physical}% ");
                
                ImGui.SameLine();
                ImGuiHelpers.SeStringWrapped(DamageMagicalStr);
                
                ImGui.SameLine();
                ImGui.Text($"{status.Key.Mitigation.Magical}% ");
            }

            var shieldValue = localPlayer->ShieldValue;
            if (shieldValue > 0)
            {
                if (LastMitigationStatus.Count > 0)
                    ImGui.TableNextRow();

                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.Image(barrierIcon.GetWrapOrEmpty().ImGuiHandle, ScaledVector2(24f));
                
                ImGui.TableNextColumn();
                ImGui.AlignTextToFramePadding();
                ImGui.Text($"{GetLoc("Shield")}");
                
                ImGui.TableNextColumn();
                var shieldPercentage = (double)shieldValue / 100;
                ImGui.Text($"{shieldValue}%% ({localPlayer->Health * shieldPercentage:F0})");
            }
        }
    }

    public override void Uninit()
    {
        FrameworkManager.Unregister(OnFrameworkUpdate);
        
        BarEntry?.Remove();
        BarEntry = null;

        base.Uninit();
    }

    public static unsafe void OnFrameworkUpdate(IFramework _)
    {
        if (!Throttler.Throttle("AutoDisplayMitigationInfo-OnFrameworkUpdate")) return;

        if (DService.ClientState.IsPvP || (ModuleConfig.OnlyInCombat && !DService.Condition[ConditionFlag.InCombat]))
        {
            ClearAndCloseBarEntry();
            return;
        }

        if (DService.ClientState.LocalPlayer is not { } localPlayer)
        {
            ClearAndCloseBarEntry();
            return;
        }

        Dictionary<MitigationStatus, float> lastMitigationStatus = [];

        var localPlayerStatus = localPlayer.StatusList;
        foreach (var status in localPlayerStatus)
            if (MitigationStatusMap.TryGetValue(status.StatusId, out var mitigation))
                lastMitigationStatus.Add(mitigation, status.RemainingTime);

        var currentTarget = DService.Targets.Target;
        if (currentTarget is IBattleNpc battleNpc)
        {
            var statusList = battleNpc.ToBCStruct()->StatusManager.Status;
            foreach (var status in statusList)
                if (MitigationStatusMap.TryGetValue(status.StatusId, out var mitigation))
                    lastMitigationStatus.Add(mitigation, status.RemainingTime);
        }

        LastMitigationStatus = lastMitigationStatus.ToDictionary(x => x.Key, x => x.Value);
        if (LastMitigationStatus.Count == 0 && localPlayer.ShieldPercentage == 0)
        {
            ClearAndCloseBarEntry();
            return;
        }
        
        RefreshBarEntry(LastMitigationStatus);
    }

    private static unsafe void RefreshBarEntry(Dictionary<MitigationStatus, float> statuses)
    {
        if (BarEntry == null) return;
        
        var localPlayer = Control.GetLocalPlayer();
        if (localPlayer == null) return;

        var textBuildr = new SeStringBuilder();
        var values = new[]
        {
            MitigationReduction(statuses.Keys.Select(x => x.Mitigation.Physical)),
            MitigationReduction(statuses.Keys.Select(x => x.Mitigation.Magical)),
            localPlayer->Character.ShieldValue,
        };

        for (var i = 0; i < values.Length; i++)
        {
            if (values[i] <= 0) continue;

            var icon = i switch
            {
                0 => BitmapFontIcon.DamagePhysical,
                1 => BitmapFontIcon.DamageMagical,
                2 => BitmapFontIcon.Tank,
                _ => BitmapFontIcon.None,
            };

            textBuildr.AddIcon(icon);
            textBuildr.Append($"{values[i]:0.0}%");
            if (i != 0) textBuildr.Append(" ");
        }
        textBuildr.Append($" ({statuses.Count})");
        BarEntry.Text = textBuildr.Build();

        var tooltipBuilder = new SeStringBuilder();
        foreach (var (status, _) in LastMitigationStatus)
        {
            tooltipBuilder.Append($"{LuminaWrapper.GetStatusName(status.Id)} ({status.Id}):");
            tooltipBuilder.AddIcon(BitmapFontIcon.DamagePhysical);
            tooltipBuilder.Append($"{status.Mitigation.Physical}% ");
            tooltipBuilder.AddIcon(BitmapFontIcon.DamageMagical);
            tooltipBuilder.Append($"{status.Mitigation.Magical}% ");
            tooltipBuilder.Append("\n");
        }
        
        var shieldPercentage = (double)localPlayer->ShieldValue / 100;
        if (localPlayer->ShieldValue > 0)
        {
            if (statuses.Count > 0)
                tooltipBuilder.Append("\n");
            
            tooltipBuilder.AddIcon(BitmapFontIcon.Tank);
            tooltipBuilder.Append($"{GetLoc("Shield")}: {values[2]}% ({localPlayer->Health * shieldPercentage:F0})");
        }

        var built = tooltipBuilder.Build();
        // 无盾则移除最后一个换行符
        if (localPlayer->ShieldValue == 0)
            built.Payloads.RemoveAt(built.Payloads.Count - 1);
        
        BarEntry.Tooltip = tooltipBuilder.Build();
        
        BarEntry.Shown = true;
    }

    private static void ClearAndCloseBarEntry()
    {
        if (BarEntry == null) return;
        
        BarEntry.Shown   = false;
        BarEntry.Tooltip = null;
        BarEntry.Text    = null;
    }

    private static float MitigationReduction(IEnumerable<float> mitigations) =>
        (1f - mitigations.Aggregate(1f, (acc, m) => acc * (1f - (m / 100f)))) * 100f;

    private class Config : ModuleConfiguration
    {
        public bool OnlyInCombat = true;
    }

    public struct MitigationDetail
    {
        public float Physical;
        public float Magical;
        public float Special;
    }

    public struct MitigationStatus : IEquatable<MitigationStatus>
    {
        public uint             Id;
        public string           Name;
        public MitigationDetail Mitigation;
        public bool             OnMember;

        public bool Equals(MitigationStatus other) => Id == other.Id;

        public override bool Equals(object? obj) => obj is MitigationStatus other && Equals(other);

        public override int GetHashCode() => (int)Id;

        public static bool operator ==(MitigationStatus left, MitigationStatus right) => left.Equals(right);

        public static bool operator !=(MitigationStatus left, MitigationStatus right) => !left.Equals(right);
    }

    public static readonly List<MitigationStatus> MitigationStatuses =
    [
        new()
        {
            Id         = 1191,
            Name       = "铁壁",
            Mitigation = new MitigationDetail { Physical = 20, Magical = 20, Special = 20 },
            OnMember   = false
        },
        new()
        {
            Id         = 1856,
            Name       = "盾阵",
            Mitigation = new MitigationDetail { Physical = 15, Magical = 15, Special = 15 },
            OnMember   = false
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
            Mitigation = new MitigationDetail { Physical = 40, Magical = 40, Special = 40 },
            OnMember   = false
        },
        new()
        {
            Id         = 1176,
            Name       = "武装",
            Mitigation = new MitigationDetail { Physical = 15, Magical = 15, Special = 15 },
            OnMember   = true
        },
        new()
        {
            Id         = 1175,
            Name       = "武装戍卫",
            Mitigation = new MitigationDetail { Physical = 10, Magical = 10, Special = 10 },
            OnMember   = false
        },
        new()
        {
            Id         = 82,
            Name       = "神圣领域",
            Mitigation = new MitigationDetail { Physical = 100, Magical = 100, Special = 100 },
            OnMember   = true
        },
        new()
        {
            Id         = 2674,
            Name       = "圣盾阵",
            Mitigation = new MitigationDetail { Physical = 15, Magical = 15, Special = 15 },
            OnMember   = true
        },
        new()
        {
            Id         = 2675,
            Name       = "骑士的坚守",
            Mitigation = new MitigationDetail { Physical = 15, Magical = 15, Special = 15 },
            OnMember   = false
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
            Id         = 89,
            Name       = "复仇",
            Mitigation = new MitigationDetail { Physical = 40, Magical = 40, Special = 40 },
            OnMember   = false
        },
        new()
        {
            Id         = 2678,
            Name       = "原初的血气",
            Mitigation = new MitigationDetail { Physical = 10, Magical = 10, Special = 10 },
            OnMember   = false
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
            Id         = 746,
            Name       = "弃明投暗",
            Mitigation = new MitigationDetail { Physical = 10, Magical = 20, Special = 0 },
            OnMember   = false
        },
        new()
        {
            Id         = 747,
            Name       = "暗影墙",
            Mitigation = new MitigationDetail { Physical = 40, Magical = 40, Special = 10 },
            OnMember   = false
        },
        new()
        {
            Id         = 1894,
            Name       = "暗黑布道",
            Mitigation = new MitigationDetail { Physical = 5, Magical = 10, Special = 0 },
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
            Mitigation = new MitigationDetail { Physical = 15, Magical = 15, Special = 15 },
            OnMember   = true
        },
        new()
        {
            Id         = 1832,
            Name       = "伪装",
            Mitigation = new MitigationDetail { Physical = 15, Magical = 10, Special = 10 },
            OnMember   = false
        },
        new()
        {
            Id         = 1834,
            Name       = "星云",
            Mitigation = new MitigationDetail { Physical = 30, Magical = 30, Special = 30 },
            OnMember   = false
        },
        new()
        {
            Id         = 1839,
            Name       = "光之心",
            Mitigation = new MitigationDetail { Physical = 5, Magical = 10, Special = 0 },
            OnMember   = true
        },
        new()
        {
            Id         = 1836,
            Name       = "超火流星",
            Mitigation = new MitigationDetail { Physical = 100, Magical = 100, Special = 100 },
            OnMember   = false
        },
        new()
        {
            Id         = 2684,
            Name       = "刚玉之清",
            Mitigation = new MitigationDetail { Physical = 15, Magical = 15, Special = 15 },
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
            Mitigation = new MitigationDetail { Physical = 15, Magical = 15, Special = 15 },
            OnMember   = true
        },
        new()
        {
            Id         = 297,
            Name       = "鼓舞",
            Mitigation = new MitigationDetail { Physical = 0, Magical = 0, Special = 180 },
            OnMember   = false
        },
        new()
        {
            Id         = 1918,
            Name       = "激励",
            Mitigation = new MitigationDetail { Physical = 0, Magical = 0, Special = 180 },
            OnMember   = false
        },
        new()
        {
            Id         = 1917,
            Name       = "炽天的幕帘",
            Mitigation = new MitigationDetail { Physical = 0, Magical = 0, Special = 250 },
            OnMember   = false
        },
        new()
        {
            Id         = 299,
            Name       = "野战治疗阵",
            Mitigation = new MitigationDetail { Physical = 10, Magical = 10, Special = 500 },
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
            Id         = 2711,
            Name       = "怒涛之计",
            Mitigation = new MitigationDetail { Physical = 10, Magical = 10, Special = 0 },
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
            Id         = 2717,
            Name       = "擢升",
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
            Id         = 1232,
            Name       = "心眼",
            Mitigation = new MitigationDetail { Physical = 10, Magical = 10, Special = 10 },
            OnMember   = true
        },
        new()
        {
            Id         = 1179,
            Name       = "金刚极意",
            Mitigation = new MitigationDetail { Physical = 20, Magical = 20, Special = 20 },
            OnMember   = true
        },
        new()
        {
            Id         = 1934,
            Name       = "行吟",
            Mitigation = new MitigationDetail { Physical = 15, Magical = 15, Special = 15 },
            OnMember   = true
        },
        new()
        {
            Id         = 1951,
            Name       = "策动",
            Mitigation = new MitigationDetail { Physical = 15, Magical = 15, Special = 15 },
            OnMember   = true
        },
        new()
        {
            Id         = 1826,
            Name       = "防守之桑巴",
            Mitigation = new MitigationDetail { Physical = 15, Magical = 15, Special = 15 },
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
            Mitigation = new MitigationDetail { Physical = 20, Magical = 20, Special = 20 },
            OnMember   = true
        },
        new()
        {
            Id         = 1722,
            Name       = "超硬化",
            Mitigation = new MitigationDetail { Physical = 90, Magical = 90, Special = 90 },
            OnMember   = true
        },
        new()
        {
            Id         = 2496,
            Name       = "玄结界",
            Mitigation = new MitigationDetail { Physical = 20, Magical = 20, Special = 20 },
            OnMember   = true
        },
        new()
        {
            Id         = 2119,
            Name       = "仙人盾",
            Mitigation = new MitigationDetail { Physical = 5, Magical = 5, Special = 5 },
            OnMember   = true
        },
        new()
        {
            Id         = 1719,
            Name       = "强力守护",
            Mitigation = new MitigationDetail { Physical = 40, Magical = 40, Special = 40 },
            OnMember   = true
        },
        new()
        {
            Id         = 194,
            Name       = "铜墙铁盾",
            Mitigation = new MitigationDetail { Physical = 20, Magical = 20, Special = 20 },
            OnMember   = true
        },
        new()
        {
            Id         = 195,
            Name       = "坚守要塞",
            Mitigation = new MitigationDetail { Physical = 40, Magical = 40, Special = 40 },
            OnMember   = true
        },
        new()
        {
            Id         = 196,
            Name       = "终极堡垒",
            Mitigation = new MitigationDetail { Physical = 80, Magical = 80, Special = 80 },
            OnMember   = true
        },
        new()
        {
            Id         = 863,
            Name       = "原初大地",
            Mitigation = new MitigationDetail { Physical = 80, Magical = 80, Special = 80 },
            OnMember   = true
        },
        new()
        {
            Id         = 864,
            Name       = "暗黑之力",
            Mitigation = new MitigationDetail { Physical = 80, Magical = 80, Special = 80 },
            OnMember   = true
        },
        new()
        {
            Id         = 1931,
            Name       = "灵魂之青",
            Mitigation = new MitigationDetail { Physical = 80, Magical = 80, Special = 80 },
            OnMember   = true
        },
        new()
        {
            Id         = 3829,
            Name       = "极致防御",
            Mitigation = new MitigationDetail { Physical = 40, Magical = 40, Special = 40 },
            OnMember   = true
        },
        new()
        {
            Id         = 3832,
            Name       = "戮罪",
            Mitigation = new MitigationDetail { Physical = 40, Magical = 40, Special = 40 },
            OnMember   = true
        },
        new()
        {
            Id         = 3835,
            Name       = "暗影卫",
            Mitigation = new MitigationDetail { Physical = 40, Magical = 40, Special = 40 },
            OnMember   = true
        },
        new()
        {
            Id         = 3838,
            Name       = "大星云",
            Mitigation = new MitigationDetail { Physical = 40, Magical = 40, Special = 40 },
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
        }
    ];
}
