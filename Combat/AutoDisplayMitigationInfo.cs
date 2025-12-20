using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using DailyRoutines.Abstracts;
using DailyRoutines.Helpers;
using DailyRoutines.Managers;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Gui.Dtr;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Interface.Utility;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Newtonsoft.Json;
using LuminaStatus = Lumina.Excel.Sheets.Status;

namespace DailyRoutines.ModulesPublic;

public class AutoDisplayMitigationInfo : DailyModuleBase
{
    #region Core

    // info
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("AutoDisplayMitigationInfoTitle"),
        Description = GetLoc("AutoDisplayMitigationInfoDescription"),
        Category    = ModuleCategories.Combat,
        Author      = ["HaKu"]
    };
    
    public override ModulePermission Permission { get; } = new() { AllDefaultEnabled = true };

    // storage
    private static ModuleStorage? ModuleConfig;

    // asset
    private static readonly byte[] DamagePhysicalStr = new SeString(new IconPayload(BitmapFontIcon.DamagePhysical)).Encode();
    private static readonly byte[] DamageMagicalStr  = new SeString(new IconPayload(BitmapFontIcon.DamageMagical)).Encode();

    protected override void Init()
    {
        ModuleConfig = LoadConfig<ModuleStorage>() ?? new ModuleStorage();

        // overlay
        SetOverlay();

        // status bar
        StatusBarManager.Enable();
        StatusBarManager.BarEntry.OnClick = _ =>
        {
            if (Overlay == null)
                return;
            Overlay.IsOpen ^= true;
        };

        // fetch remote resource
        Task.Run(async () => await RemoteRepoManager.FetchMitigationStatuses());

        // draw on party list
        WindowManager.Draw += Draw;

        // refresh mitigation status
        FrameworkManager.Reg(OnFrameworkUpdateInterval, throttleMS: 500);

        DService.ClientState.TerritoryChanged += OnZoneChanged;
    }

    protected override void Uninit()
    {
        // refresh mitigation status
        FrameworkManager.Unreg(OnFrameworkUpdateInterval);
        
        DService.ClientState.TerritoryChanged -= OnZoneChanged;
        OnZoneChanged(0);

        // draw on party list
        WindowManager.Draw -= Draw;

        // status bar
        StatusBarManager.Disable();
    }

    protected override void ConfigUI()
    {
        if (ImGui.Checkbox(GetLoc("OnlyInCombat"), ref ModuleConfig.OnlyInCombat))
            SaveConfig(ModuleConfig);

        if (ImGui.Checkbox(GetLoc("TransparentOverlay"), ref ModuleConfig.TransparentOverlay))
        {
            SaveConfig(ModuleConfig);

            if (ModuleConfig.TransparentOverlay)
            {
                Overlay.Flags |= ImGuiWindowFlags.NoBackground;
                Overlay.Flags |= ImGuiWindowFlags.NoTitleBar;
            }
            else
            {
                Overlay.Flags &= ~ImGuiWindowFlags.NoBackground;
                Overlay.Flags &= ~ImGuiWindowFlags.NoTitleBar;
            }
        }

        if (ImGui.Checkbox(GetLoc("ResizeableOverlay"), ref ModuleConfig.ResizeableOverlay))
        {
            SaveConfig(ModuleConfig);

            if (ModuleConfig.ResizeableOverlay)
                Overlay.Flags &= ~ImGuiWindowFlags.NoResize;
            else
                Overlay.Flags |= ImGuiWindowFlags.NoResize;
        }

        if (ImGui.Checkbox(GetLoc("MoveableOverlay"), ref ModuleConfig.MoveableOverlay))
        {
            SaveConfig(ModuleConfig);

            if (!ModuleConfig.MoveableOverlay)
            {
                Overlay.Flags |= ImGuiWindowFlags.NoMove;
                Overlay.Flags |= ImGuiWindowFlags.NoInputs;
            }
            else
            {
                Overlay.Flags &= ~ImGuiWindowFlags.NoMove;
                Overlay.Flags &= ~ImGuiWindowFlags.NoInputs;
            }
        }
    }

    #endregion

    #region Overlay

    protected override unsafe void OverlayUI()
    {
        if (Control.GetLocalPlayer() == null || MitigationManager.IsLocalEmpty())
            return;

        ImGuiHelpers.SeStringWrapped(StatusBarManager.BarEntry?.Text?.Encode() ?? []);

        ImGui.Separator();

        using var table = ImRaii.Table("StatusTable", 3);
        if (!table)
            return;

        ImGui.TableSetupColumn("Icon", ImGuiTableColumnFlags.WidthFixed, 24f * GlobalFontScale);
        ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.WidthStretch, 20);
        ImGui.TableSetupColumn("Value", ImGuiTableColumnFlags.WidthStretch, 20);

        if (!DService.Texture.TryGetFromGameIcon(new(210405), out var barrierIcon))
            return;

        // local status
        foreach (var status in MitigationManager.LocalActiveStatus)
            DrawStatusRow(status);

        // battle npc status
        foreach (var status in MitigationManager.BattleNpcActiveStatus)
            DrawStatusRow(status);

        // local shield
        if (MitigationManager.LocalShield > 0)
        {
            if (!MitigationManager.IsLocalEmpty())
                ImGui.TableNextRow();

            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.Image(barrierIcon.GetWrapOrEmpty().Handle, ScaledVector2(24f));

            ImGui.TableNextColumn();
            ImGui.AlignTextToFramePadding();
            ImGui.Text($"{GetLoc("Shield")}");

            ImGui.TableNextColumn();
            ImGui.Text($"{MitigationManager.LocalShield}");
        }
    }

    private void SetOverlay()
    {
        Overlay            ??= new(this);
        Overlay.WindowName =   GetLoc("AutoDisplayMitigationInfoTitle");
        Overlay.Flags      &=  ~ImGuiWindowFlags.NoTitleBar;
        Overlay.Flags      &=  ~ImGuiWindowFlags.AlwaysAutoResize;

        if (ModuleConfig.TransparentOverlay)
        {
            Overlay.Flags |= ImGuiWindowFlags.NoBackground;
            Overlay.Flags |= ImGuiWindowFlags.NoTitleBar;
        }
        else
        {
            Overlay.Flags &= ~ImGuiWindowFlags.NoBackground;
            Overlay.Flags &= ~ImGuiWindowFlags.NoTitleBar;
        }

        if (ModuleConfig.ResizeableOverlay)
            Overlay.Flags &= ~ImGuiWindowFlags.NoResize;
        else
            Overlay.Flags |= ImGuiWindowFlags.NoResize;

        if (!ModuleConfig.MoveableOverlay)
        {
            Overlay.Flags |= ImGuiWindowFlags.NoMove;
            Overlay.Flags |= ImGuiWindowFlags.NoInputs;
        }
        else
        {
            Overlay.Flags &= ~ImGuiWindowFlags.NoMove;
            Overlay.Flags &= ~ImGuiWindowFlags.NoInputs;
        }
    }

    private static void DrawStatusRow(KeyValuePair<MitigationManager.MitigationInfo, float> status)
    {
        if (!LuminaGetter.TryGetRow<LuminaStatus>(status.Key.ID, out var row))
            return;
        if (!DService.Texture.TryGetFromGameIcon(new(row.Icon), out var icon))
            return;

        ImGui.TableNextRow();

        ImGui.TableNextColumn();
        ImGui.Image(icon.GetWrapOrEmpty().Handle, ScaledVector2(24f));

        ImGui.TableNextColumn();
        ImGui.AlignTextToFramePadding();
        ImGui.Text($"{row.Name} ({status.Value:F1}s)");
        ImGuiOm.TooltipHover($"{status.Key.ID}");

        ImGui.TableNextColumn();
        ImGuiHelpers.SeStringWrapped(DamagePhysicalStr);

        ImGui.SameLine();
        ImGui.Text($"{status.Key.Info.Physical}% ");

        ImGui.SameLine();
        ImGuiHelpers.SeStringWrapped(DamageMagicalStr);

        ImGui.SameLine();
        ImGui.Text($"{status.Key.Info.Magical}% ");
    }

    #endregion

    #region Hooks

    private static void OnZoneChanged(ushort obj)
    {
        PartyMemberIndexCache.Clear();
        MitigationManager.Clear();
        StatusBarManager.Clear();
    }
    
    private static unsafe void OnFrameworkUpdateInterval(IFramework _)
    {
        if (GameState.IsInPVPArea || Control.GetLocalPlayer() is null)
        {
            MitigationManager.Clear();
            StatusBarManager.Clear();
            return;
        }

        PartyMemberIndexCache.Clear();
        foreach (var member in AgentHUD.Instance()->PartyMembers)
            PartyMemberIndexCache[member.EntityId] = member.Index;

        MitigationManager.Update();

        var combatInactive = ModuleConfig.OnlyInCombat && !DService.Condition[ConditionFlag.InCombat];
        if (combatInactive)
        {
            StatusBarManager.Clear();
            return;
        }

        StatusBarManager.Update();
    }

    #endregion

    #region RemoteCache

    private static class RemoteRepoManager
    {
        // const
        private const string Uri = "https://assets.sumemo.dev";

        public static async Task FetchMitigationStatuses()
        {
            try
            {
                var json = await HttpClientHelper.Get().GetStringAsync($"{Uri}/mitigation.json");
                var resp = JsonConvert.DeserializeObject<MitigationManager.MitigationInfo[]>(json);
                if (resp == null)
                    Error($"[AutoDisplayMitigationInfo] 远程减伤技能文件解析失败: {json}");
                else
                    MitigationManager.StatusDict = resp.ToDictionary(x => x.ID, x => x);
            }
            catch (Exception ex) { Error($"[AutoDisplayMitigationInfo] 远程减伤技能文件获取失败: {ex}"); }
        }
    }

    #endregion

    #region StatusBar

    private static class StatusBarManager
    {
        // cache
        public static IDtrBarEntry? BarEntry;

        #region Funcs

        public static void Enable()
            => BarEntry ??= DService.DtrBar.Get("DailyRoutines-AutoDisplayMitigationInfo");

        public static void Update()
        {
            if (BarEntry == null || MitigationManager.IsLocalEmpty())
            {
                Clear();
                return;
            }

            // summary
            var textBuilder  = new SeStringBuilder();
            var values       = MitigationManager.FetchLocal();
            var firstBarItem = true;

            for (var i = 0; i < values.Length; i++)
            {
                if (values[i] <= 0)
                    continue;

                var icon = i switch
                {
                    0 => BitmapFontIcon.DamagePhysical,
                    1 => BitmapFontIcon.DamageMagical,
                    2 => BitmapFontIcon.Tank,
                    _ => BitmapFontIcon.None,
                };

                if (!firstBarItem)
                    textBuilder.Append(" ");

                textBuilder.AddIcon(icon);
                textBuilder.Append($"{values[i]:0}" + (i != 2 ? "%" : ""));
                firstBarItem = false;
            }

            BarEntry.Text = textBuilder.Build();

            // detail
            var tipBuilder   = new SeStringBuilder();
            var firstTipItem = true;

            // status
            foreach (var (status, _) in MitigationManager.LocalActiveStatus)
            {
                if (!firstTipItem)
                    tipBuilder.Append("\n");
                tipBuilder.Append($"{LuminaWrapper.GetStatusName(status.ID)}:");
                tipBuilder.AddIcon(BitmapFontIcon.DamagePhysical);
                tipBuilder.Append($"{status.Info.Physical}% ");
                tipBuilder.AddIcon(BitmapFontIcon.DamageMagical);
                tipBuilder.Append($"{status.Info.Magical}% ");
                firstTipItem = false;
            }

            // shield
            if (MitigationManager.LocalShield > 0)
            {
                if (!firstTipItem)
                    tipBuilder.Append("\n");
                tipBuilder.AddIcon(BitmapFontIcon.Tank);
                tipBuilder.Append($"{GetLoc("Shield")}: {MitigationManager.LocalShield}");
                firstTipItem = false;
            }

            // battle npc
            foreach (var (status, _) in MitigationManager.BattleNpcActiveStatus)
            {
                if (!firstTipItem)
                    tipBuilder.Append("\n");
                tipBuilder.Append($"{LuminaWrapper.GetStatusName(status.ID)}:");
                tipBuilder.AddIcon(BitmapFontIcon.DamagePhysical);
                tipBuilder.Append($"{status.Info.Physical}% ");
                tipBuilder.AddIcon(BitmapFontIcon.DamageMagical);
                tipBuilder.Append($"{status.Info.Magical}% ");
                firstTipItem = false;
            }

            BarEntry.Tooltip = tipBuilder.Build();
            BarEntry.Shown   = true;
        }

        public static void Clear()
        {
            if (BarEntry == null)
                return;

            BarEntry.Shown   = false;
            BarEntry.Tooltip = null;
            BarEntry.Text    = null;
        }

        public static void Disable()
        {
            BarEntry?.Remove();
            BarEntry = null;
        }

        #endregion
    }

    #endregion

    #region PartyList

    private static bool IsNeedToDrawOnPartyList;

    public static unsafe void Draw()
    {
        if (Throttler.Throttle("AutoDisplayMitigationInfo-OnUpdatePartyDrawCondition"))
            IsNeedToDrawOnPartyList = IsAddonAndNodesReady(PartyList) && !GameState.IsInPVPArea;

        if (!IsNeedToDrawOnPartyList)
            return;

        var drawList = ImGui.GetBackgroundDrawList();
        var addon    = (AddonPartyList*)PartyList;
        foreach (var memberStatus in MitigationManager.FetchParty())
        {
            if (FetchMemberIndex(memberStatus.Key) is { } memberIndex)
            {
                ref var partyMember = ref addon->PartyMembers[(int)memberIndex];
                if (partyMember.HPGaugeComponent is null || !partyMember.HPGaugeComponent->OwnerNode->IsVisible())
                    continue;

                PartyListManager.DrawMitigationNode(drawList, ref partyMember, memberStatus.Value);
                PartyListManager.DrawShieldNode(drawList, ref partyMember, memberStatus.Value);
            }
        }
    }

    private static unsafe class PartyListManager
    {
        public static void DrawMitigationNode(ImDrawListPtr drawList, ref AddonPartyList.PartyListMemberStruct partyMember, float[] status)
        {
            var mitigationValue = Math.Max(status[0], status[1]);
            if (mitigationValue == 0)
                return;

            var nameNode = partyMember.NameAndBarsContainer;
            if (nameNode is null || !nameNode->IsVisible())
                return;

            var partyListAddon = (AddonPartyList*)PartyList;
            if (!IsAddonAndNodesReady(PartyList))
                return;

            // hidden when casting
            var nameTextNode = partyMember.Name;
            if (nameTextNode is null || !nameTextNode->IsVisible())
                return;

            var partyScale = partyListAddon->Scale;

            using var fontPush = FontManager.MiedingerMidFont120.Push();

            var text     = $"{mitigationValue:N0}%";
            var textSize = ImGui.CalcTextSize(text);

            var posX = nameNode->ScreenX + (nameNode->GetWidth() * partyScale) - textSize.X - (5 * partyScale);
            var posY = nameNode->ScreenY + (2 * partyScale);

            var pos = new Vector2(posX, posY);

            drawList.AddText(pos + new Vector2(1, 1), 0x9D00A2FF, text);
            drawList.AddText(pos, 0xFFFFFFFF, text);
        }

        public static void DrawShieldNode(ImDrawListPtr drawList, ref AddonPartyList.PartyListMemberStruct partyMember, float[] status)
        {
            var shieldValue = status[2];

            var hpComponent = partyMember.HPGaugeComponent;
            if (hpComponent is null || !hpComponent->OwnerNode->IsVisible())
                return;

            var numNode = hpComponent->GetTextNodeById(2);
            if (numNode is null || !numNode->IsVisible())
                return;

            var partyListAddon = (AddonPartyList*)PartyList;
            if (!IsAddonAndNodesReady(PartyList))
                return;

            // hide mp number
            var mpNodes = new[]
            {
                partyMember.MPGaugeBar->GetTextNodeById(2)->GetAsAtkTextNode(),
                partyMember.MPGaugeBar->GetTextNodeById(3)->GetAsAtkTextNode()
            };
            if (shieldValue >= 1e5)
            {
                foreach (var mpNode in mpNodes)
                {
                    if (mpNode is null || !mpNode->IsVisible())
                        continue;
                    mpNode->SetAlpha(0);
                }
            }
            else
            {
                foreach (var mpNode in mpNodes)
                {
                    if (mpNode is null || !mpNode->IsVisible())
                        continue;
                    mpNode->SetAlpha(255);
                }
            }

            if (shieldValue == 0)
                return;

            var partyScale = partyListAddon->Scale;

            using var fontPush = FontManager.MiedingerMidFont120.Push();

            var text = $"{shieldValue:F0}";

            var posX = numNode->ScreenX + (numNode->GetWidth() * partyListAddon->Scale) + (3 * partyScale);
            var posY = numNode->ScreenY + (numNode->GetHeight() * partyListAddon->Scale / 2) - (3f * partyScale);

            drawList.AddText(new Vector2(posX + 1, posY + 1), 0x9D00A2FF, text);
            drawList.AddText(new Vector2(posX, posY), 0xFFFFFFFF, text);
        }
    }

    #endregion

    #region Mitigation

    private static unsafe class MitigationManager
    {
        // cache
        public static          Dictionary<uint, MitigationInfo> StatusDict           = [];
        public static readonly Dictionary<uint, float[]>  PartyMitigationCache = [];

        // snapshot
        private static KeyValuePair<uint, float[]>[] PartyMitigationSnapshot = [];

        // local player
        public static readonly Dictionary<MitigationInfo, float> LocalActiveStatus = [];

        public static float LocalShield
        {
            get
            {
                var localPlayer = Control.GetLocalPlayer();
                if (localPlayer == null)
                    return 0;

                return (float)localPlayer->ShieldValue / 100 * localPlayer->Health;
            }
        }

        // party member
        public static readonly Dictionary<uint, Dictionary<MitigationInfo, float>> PartyActiveStatus = [];

        public static Dictionary<uint, float> PartyShield
        {
            get
            {
                var partyShield = new Dictionary<uint, float>();

                var partyList = DService.PartyList;
                if (partyList.Count == 0)
                    return partyShield;

                foreach (var member in partyList)
                {
                    if (member.EntityId == 0)
                        continue;

                    if (DService.ObjectTable.SearchByID(member.EntityId) is ICharacter memberChara)
                        partyShield[member.EntityId] = ((float)memberChara.ShieldPercentage / 100 * memberChara.CurrentHp);
                }

                return partyShield;
            }
        }

        // battle npc
        public static readonly Dictionary<MitigationInfo, float> BattleNpcActiveStatus = [];

        #region Structs

        public class StatusInfo
        {
            [JsonProperty("physical")]
            public float Physical { get; set; }

            [JsonProperty("magical")]
            public float Magical { get; set; }
        }

        public class MitigationInfo : IEquatable<MitigationInfo>
        {
            [JsonProperty("id")]
            public uint ID { get; private set; }

            [JsonProperty("name")]
            public string Name { get; private set; }

            [JsonProperty("mitigation")]
            public StatusInfo Info { get; set; }

            [JsonProperty("on_member")]
            public bool OnMember { get; private set; }

            #region Equals

            public bool Equals(MitigationInfo? other) => ID == other.ID;

            public override bool Equals(object? obj) => obj is MitigationInfo other && Equals(other);

            public override int GetHashCode() => (int)ID;

            public static bool operator ==(MitigationInfo left, MitigationInfo right) => left.Equals(right);

            public static bool operator !=(MitigationInfo left, MitigationInfo right) => !left.Equals(right);

            #endregion
        }

        public readonly struct MemberStatus
        {
            public uint StatusID { get; }
            public uint SourceID { get; }

            private MemberStatus(uint statusID, uint sourceID)
                => (StatusID, SourceID) = (statusID, sourceID);

            public static MemberStatus From(IStatus s)
                => new(s.StatusID, s.SourceID);

            public static MemberStatus From(nint statusAddress)
                => From(IStatus.Create(statusAddress));

            public static MemberStatus From(FFXIVClientStructs.FFXIV.Client.Game.Status s) =>
                new(s.StatusId, s.SourceObject.ObjectId);
        }

        #endregion

        #region Funcs

        public static bool TryGetMitigation(uint targetID, MemberStatus memberStatus, out MitigationInfo? mitigation)
        {
            mitigation = null;

            if (StatusDict.TryGetValue(memberStatus.StatusID, out var defaultMitigation))
            {
                mitigation = defaultMitigation;

                switch (memberStatus.StatusID)
                {
                    case 2675:
                    {
                        var mitValue = memberStatus.SourceID == targetID ? 15 : 10;
                        mitigation.Info.Magical  = mitValue;
                        mitigation.Info.Physical = mitValue;
                        break;
                    }
                    case 1174 when DService.ObjectTable.SearchByID(targetID) is IBattleChara sourceChara:
                    {
                        var sourceStatusIds = sourceChara.StatusList.Select(x => x.StatusID).ToHashSet();
                        var mitValue        = sourceStatusIds.Contains(1191) || sourceStatusIds.Contains(3829) ? 20 : 10;
                        mitigation.Info.Magical  = mitValue;
                        mitigation.Info.Physical = mitValue;
                        break;
                    }
                }

                return true;
            }

            return false;
        }

        public static void Update()
        {
            // clear cache
            Clear();

            // local player
            var localPlayer = Control.GetLocalPlayer();
            if (localPlayer == null)
                return;

            // status
            foreach (var status in localPlayer->StatusManager.Status)
            {
                if (status.StatusId == 0)
                    continue;
                if (TryGetMitigation(localPlayer->EntityId, MemberStatus.From(status), out var mitigation) && mitigation is not null)
                    LocalActiveStatus.TryAdd(mitigation, status.RemainingTime);
            }

            // party
            var partyList = DService.PartyList;
            if (partyList.Count != 0)
            {
                foreach (var member in partyList)
                {
                    if (member.EntityId == 0)
                        continue;

                    var activeStatus = new Dictionary<MitigationInfo, float>();
                    foreach (var status in member.Statuses)
                    {
                        if (status.StatusId == 0)
                            continue;
                        if (TryGetMitigation(member.EntityId, MemberStatus.From(status.Address), out var mitigation) && mitigation is not null)
                            activeStatus.TryAdd(mitigation, status.RemainingTime);
                    }

                    PartyActiveStatus[member.EntityId] = activeStatus;
                }
            }

            // battle npc
            var currentTarget = DService.Targets.Target;
            if (currentTarget is IBattleNPC battleNpc)
            {
                var statusList = battleNpc.ToBCStruct()->StatusManager.Status;
                foreach (var status in statusList)
                {
                    if (StatusDict.TryGetValue(status.StatusId, out var mitigation))
                        BattleNpcActiveStatus.TryAdd(mitigation, status.RemainingTime);
                }
            }

            var partyShieldCache = PartyShield;
            foreach (var memberActiveStatus in PartyActiveStatus)
            {
                var activeStatus = memberActiveStatus.Value.Concat(BattleNpcActiveStatus).ToDictionary(kv => kv.Key, kv => kv.Value);
                PartyMitigationCache.TryAdd(memberActiveStatus.Key, [
                    Reduction(activeStatus.Keys.Select(x => x.Info.Physical)),
                    Reduction(activeStatus.Keys.Select(x => x.Info.Magical)),
                    partyShieldCache.GetValueOrDefault(memberActiveStatus.Key, 0)
                ]);
            }

            if (DService.PartyList.Count == 0 && Control.GetLocalPlayer()->EntityId is { } id)
                PartyMitigationCache.TryAdd(id, FetchLocal());

            PartyMitigationSnapshot = PartyMitigationCache.ToArray();
        }

        public static void Clear()
        {
            LocalActiveStatus.Clear();
            PartyActiveStatus.Clear();
            BattleNpcActiveStatus.Clear();

            PartyMitigationCache.Clear();
            PartyMitigationSnapshot = [];
        }

        public static bool IsLocalEmpty() => 
            LocalActiveStatus.Count == 0 && LocalShield == 0 && BattleNpcActiveStatus.Count == 0;

        public static float[] FetchLocal()
        {
            var activeStatus = LocalActiveStatus.Concat(BattleNpcActiveStatus).ToDictionary(kv => kv.Key, kv => kv.Value);
            return
            [
                Reduction(activeStatus.Keys.Select(x => x.Info.Physical)),
                Reduction(activeStatus.Keys.Select(x => x.Info.Magical)),
                LocalShield
            ];
        }

        public static IReadOnlyList<KeyValuePair<uint, float[]>> FetchParty()
            => PartyMitigationSnapshot;

        public static float Reduction(IEnumerable<float> mitigations) =>
            (1f - mitigations.Aggregate(1f, (acc, m) => acc * (1f - (m / 100f)))) * 100f;

        #endregion
    }

    #endregion

    #region Utils

    private static readonly Dictionary<uint, uint> PartyMemberIndexCache = [];

    private static uint? FetchMemberIndex(uint entityID) =>
        PartyMemberIndexCache.TryGetValue(entityID, out var index) ? index : null;

    #endregion

    #region Config

    private class ModuleStorage : ModuleConfiguration
    {
        // activate
        public bool OnlyInCombat = true;

        // ui
        public bool TransparentOverlay;
        public bool ResizeableOverlay = true;
        public bool MoveableOverlay   = true;
    }

    #endregion
}
