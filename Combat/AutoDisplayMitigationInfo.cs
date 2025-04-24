using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Gui.Dtr;
using Dalamud.Interface.Utility.Raii;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
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
using Newtonsoft.Json;

namespace DailyRoutines.Modules;

public class AutoDisplayMitigationInfo : DailyModuleBase
{
    #region Core

    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("AutoDisplayMitigationInfoTitle"),
        Description = GetLoc("AutoDisplayMitigationInfoDescription"),
        Category    = ModuleCategories.Combat,
        Author      = ["HaKu"]
    };

    // icon asset
    private static readonly byte[] DamagePhysicalStr;
    private static readonly byte[] DamageMagicalStr;

    // config & overlay
    private static Config        ModuleConfig = null!;
    private static IDtrBarEntry? BarEntry;

    // cache variables
    private static Dictionary<uint, MitigationStatus>  MitigationStatusMap;
    private static Dictionary<MitigationStatus, float> LastMitigationStatus = [];

    static AutoDisplayMitigationInfo()
    {
        DamagePhysicalStr = new SeString(new IconPayload(BitmapFontIcon.DamagePhysical)).Encode();
        DamageMagicalStr  = new SeString(new IconPayload(BitmapFontIcon.DamageMagical)).Encode();
    }

    public override void Init()
    {
        ModuleConfig = LoadConfig<Config>() ?? new();

        // overlay
        Overlay            ??= new(this);
        Overlay.WindowName =   GetLoc("AutoDisplayMitigationInfoTitle");
        Overlay.Flags      &=  ~ImGuiWindowFlags.NoTitleBar;
        Overlay.Flags      &=  ~ImGuiWindowFlags.NoResize;
        Overlay.Flags      &=  ~ImGuiWindowFlags.AlwaysAutoResize;

        // status bar
        BarEntry ??= DService.DtrBar.Get("DailyRoutines-AutoDisplayMitigationInfo");
        BarEntry.OnClick = () =>
        {
            if (Overlay == null) return;
            Overlay.IsOpen ^= true;
        };

        // fetch remote resources
        DService.Framework.RunOnTick(async () => await FetchRemoteVersion());

        // life cycle hooks
        FrameworkManager.Register(false, OnFrameworkUpdate);
    }

    #endregion

    #region UI

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

        using var table = ImRaii.Table("StatusTable", 3);

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

    #endregion

    public override void Uninit()
    {
        FrameworkManager.Unregister(OnFrameworkUpdate);

        BarEntry?.Remove();
        BarEntry = null;

        base.Uninit();
    }

    #region Hooks

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

    #endregion

    #region StatusBar

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

            if (i != 0) textBuildr.Append(" ");
            textBuildr.AddIcon(icon);
            textBuildr.Append($"{values[i]:0.0}%");
        }

        textBuildr.Append($" ({statuses.Count})");
        BarEntry.Text = textBuildr.Build();

        var tooltipBuilder = new SeStringBuilder();
        foreach (var (status, _) in LastMitigationStatus)
        {
            tooltipBuilder.Append($"{LuminaWarpper.GetStatusName(status.Id)} ({status.Id}):");
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
        // remove last \n when no shield
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

    #endregion

    #region Func

    private static float MitigationReduction(IEnumerable<float> mitigations) =>
        (1f - mitigations.Aggregate(1f, (acc, m) => acc * (1f - (m / 100f)))) * 100f;

    private static void UpdateMitigationStatusMap()
        => MitigationStatusMap = ModuleConfig.MitigationStatuses.ToDictionary(s => s.Id);

    #endregion

    #region RemoteCache

    // cache related client setting
    private const string SuCache = "https://dr-cache.sumemo.dev";

    private async Task FetchRemoteVersion()
    {
        // fetch remote data
        try
        {
            var json = await HttpClientHelper.Get().GetStringAsync($"{SuCache}/version");
            var resp = JsonConvert.DeserializeAnonymousType(json, new { version = "" });
            if (resp == null || string.IsNullOrWhiteSpace(resp.version))
                Error($"Deserialize Remote Version Failed: {json}");
            else
            {
                // fetch remote data
                var remoteCacheVersion = resp.version;
                if (new Version(ModuleConfig.LocalCacheVersion) < new Version(remoteCacheVersion))
                {
                    // update config
                    ModuleConfig.LocalCacheVersion = resp.version;
                    SaveConfig(ModuleConfig);

                    // update out-date cache
                    await FetchMitigationStatuses();
                }
            }
        }
        catch (Exception ex)
        {
            Error($"[AutoDisplayMitigationInfo] Fetch Remote Version Failed: {ex}");
        }

        // build cache
        UpdateMitigationStatusMap();
    }

    private async Task FetchMitigationStatuses()
    {
        try
        {
            var json = await HttpClientHelper.Get().GetStringAsync($"{SuCache}/mitigation?v={ModuleConfig.LocalCacheVersion}");
            var resp = JsonConvert.DeserializeObject<MitigationStatus[]>(json);
            if (resp == null)
                Error($"[AutoDisplayMitigationInfo] Deserialize Mitigation List Failed: {json}");
            else
            {
                ModuleConfig.MitigationStatuses = resp;
                SaveConfig(ModuleConfig);
            }
        }
        catch (Exception ex)
        {
            Error($"[AutoDisplayMitigationInfo] Fetch Mitigation List Failed: {ex}");
        }
    }

    #endregion

    #region Config

    private class Config : ModuleConfiguration
    {
        public bool OnlyInCombat = true;

        // remote cache
        public string             LocalCacheVersion  = "0.0.0";
        public MitigationStatus[] MitigationStatuses = [];
    }

    public struct MitigationDetail
    {
        [JsonProperty("physical")]
        public float Physical { get; private set; }

        [JsonProperty("magical")]
        public float Magical { get; private set; }

        [JsonProperty("special")]
        public float Special { get; private set; }
    }

    public struct MitigationStatus : IEquatable<MitigationStatus>
    {
        [JsonProperty("id")]
        public uint Id { get; private set; }

        [JsonProperty("name")]
        public string Name { get; private set; }

        [JsonProperty("mitigation")]
        public MitigationDetail Mitigation { get; private set; }

        [JsonProperty("on_member")]
        public bool OnMember { get; private set; }

        public bool Equals(MitigationStatus other) => Id == other.Id;

        public override bool Equals(object? obj) => obj is MitigationStatus other && Equals(other);

        public override int GetHashCode() => (int)Id;

        public static bool operator ==(MitigationStatus left, MitigationStatus right) => left.Equals(right);

        public static bool operator !=(MitigationStatus left, MitigationStatus right) => !left.Equals(right);
    }

    #endregion
}
