using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DailyRoutines.Abstracts;
using DailyRoutines.Helpers;
using DailyRoutines.Managers;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.Gui.Dtr;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Interface.Utility;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using Lumina.Excel.Sheets;
using Newtonsoft.Json;

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

    // storage
    private static ModuleStorage? ModuleConfig;

    // asset
    private static readonly byte[] DamagePhysicalStr;
    private static readonly byte[] DamageMagicalStr;

    static AutoDisplayMitigationInfo()
    {
        DamagePhysicalStr = new SeString(new IconPayload(BitmapFontIcon.DamagePhysical)).Encode();
        DamageMagicalStr  = new SeString(new IconPayload(BitmapFontIcon.DamageMagical)).Encode();
    }

    // managers
    public static MitigationManager MitigationService;

    public override void Init()
    {
        ModuleConfig = LoadConfig<ModuleStorage>() ?? new ModuleStorage();

        // enable cast hooks
        DamageActionManager.Enable();

        // overlay
        SetOverlay();

        // status bar
        StatusBarManager.Enable();
        StatusBarManager.BarEntry.OnClick = () =>
        {
            if (Overlay == null)
                return;
            Overlay.IsOpen ^= true;
        };

        // cast bar
        CastBarManager.Enable();

        // party list
        PartyListManager.Enable();

        // fetch remote resource
        Task.Run(async () =>
        {
            await RemoteRepoManager.FetchMitigationStatuses();
            await RemoteRepoManager.FetchDamageActions();
        });
        SaveConfig(ModuleConfig);

        // managers
        MitigationService = new MitigationManager(ModuleConfig.MitigationStorage);

        // life cycle hooks
        DService.ClientState.TerritoryChanged += OnZoneChanged;
        FrameworkManager.Register(OnFrameworkUpdate);
        FrameworkManager.Register(OnFrameworkUpdateInterval, throttleMS: 500);
    }

    public override void Uninit()
    {
        // life cycle hooks
        DService.ClientState.TerritoryChanged -= OnZoneChanged;
        FrameworkManager.Unregister(OnFrameworkUpdate);
        FrameworkManager.Unregister(OnFrameworkUpdateInterval);

        // status bar
        StatusBarManager.Disable();

        // cast bar
        CastBarManager.Disable();

        // party list
        PartyListManager.Disable();

        // disable cast hooks
        DamageActionManager.Disable();

        base.Uninit();
    }

    public override void ConfigUI()
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

    public override unsafe void OverlayUI()
    {
        if (Control.GetLocalPlayer() == null || MitigationService.IsLocalEmpty())
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
        foreach (var status in MitigationService.LocalActiveStatus)
            DrawStatusRow(status);

        // battle npc status
        foreach (var status in MitigationService.BattleNpcActiveStatus)
            DrawStatusRow(status);

        // local shield
        if (MitigationService.LocalShield > 0)
        {
            if (!MitigationService.IsLocalEmpty())
                ImGui.TableNextRow();

            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.Image(barrierIcon.GetWrapOrEmpty().ImGuiHandle, ScaledVector2(24f));

            ImGui.TableNextColumn();
            ImGui.AlignTextToFramePadding();
            ImGui.Text($"{GetLoc("Shield")}");

            ImGui.TableNextColumn();
            ImGui.Text($"{MitigationService.LocalShield}");
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

    private static void DrawStatusRow(KeyValuePair<MitigationManager.Status, float> status)
    {
        if (!LuminaGetter.TryGetRow<Status>(status.Key.Id, out var row))
            return;
        if (!DService.Texture.TryGetFromGameIcon(new(row.Icon), out var icon))
            return;

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
        ImGui.Text($"{status.Key.Info.Physical}% ");

        ImGui.SameLine();
        ImGuiHelpers.SeStringWrapped(DamageMagicalStr);

        ImGui.SameLine();
        ImGui.Text($"{status.Key.Info.Magical}% ");
    }

    #endregion


    #region Hooks

    public static unsafe void OnFrameworkUpdate(IFramework _)
    {
        var combatInactive = ModuleConfig.OnlyInCombat && !DService.Condition[ConditionFlag.InCombat];
        if (DService.ClientState.IsPvP || combatInactive || Control.GetLocalPlayer() is null)
        {
            StatusBarManager.Clear();
            return;
        }

        // update status
        CastBarManager.Update();
        PartyListManager.Update();
    }

    public static unsafe void OnFrameworkUpdateInterval(IFramework _)
    {
        var combatInactive = ModuleConfig.OnlyInCombat && !DService.Condition[ConditionFlag.InCombat];
        if (DService.ClientState.IsPvP || combatInactive || Control.GetLocalPlayer() is null)
        {
            StatusBarManager.Clear();
            return;
        }

        // update status
        MitigationService.Update();
        StatusBarManager.Update();
    }

    private static void OnZoneChanged(ushort zoneId)
        => RemoteRepoManager.FetchDamageActions(zoneId);

    #endregion

    #region RemoteCache

    public static class RemoteRepoManager
    {
        // const
        private const string Uri = "https://dr-cache.sumemo.dev";

        public static async Task FetchMitigationStatuses()
        {
            try
            {
                var json = await HttpClientHelper.Get().GetStringAsync($"{Uri}/mitigation");
                var resp = JsonConvert.DeserializeObject<MitigationManager.Status[]>(json);
                if (resp == null)
                    Error($"[AutoDisplayMitigationInfo] 远程减伤文件解析失败: {json}");
                else
                {
                    ModuleConfig.MitigationStorage.Statuses = resp;
                    MitigationService.InitStatusMap();
                }
            }
            catch (Exception ex) { Error($"[AutoDisplayMitigationInfo] 远程减伤文件获取失败: {ex}"); }
        }

        // TODO: separate by zone
        private static DamageActionManager.DamageAction[] Actions = [];

        public static async Task FetchDamageActions()
        {
            try
            {
                var json = await HttpClientHelper.Get().GetStringAsync($"{Uri}/damage");
                var resp = JsonConvert.DeserializeObject<DamageActionManager.DamageAction[]>(json);
                if (resp == null)
                    Error($"[AutoDisplayMitigationInfo] 远程技能伤害文件解析失败: {json}");
                else
                    Actions = resp;
            }
            catch (Exception ex) { Error($"[AutoDisplayMitigationInfo] 远程技能伤害文件获取失败: {ex}"); }
        }

        public static void FetchDamageActions(uint zoneId)
            => DamageActionManager.Actions = Actions.Where(x => x.ZoneId == zoneId).ToDictionary(x => x.ActionId, x => x);
    }

    #endregion


    #region StatusBar

    public static class StatusBarManager
    {
        // cache
        public static IDtrBarEntry? BarEntry;

        #region Funcs

        public static void Enable()
            => BarEntry ??= DService.DtrBar.Get("DailyRoutines-AutoDisplayMitigationInfo");

        public static void Update()
        {
            if (BarEntry == null || MitigationService.IsLocalEmpty())
            {
                Clear();
                return;
            }

            // summary
            var textBuilder  = new SeStringBuilder();
            var values       = MitigationService.FetchLocal();
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
            foreach (var (status, _) in MitigationService.LocalActiveStatus)
            {
                if (!firstTipItem)
                    tipBuilder.Append("\n");
                tipBuilder.Append($"{LuminaWrapper.GetStatusName(status.Id)}:");
                tipBuilder.AddIcon(BitmapFontIcon.DamagePhysical);
                tipBuilder.Append($"{status.Info.Physical}% ");
                tipBuilder.AddIcon(BitmapFontIcon.DamageMagical);
                tipBuilder.Append($"{status.Info.Magical}% ");
                firstTipItem = false;
            }

            // shield
            if (MitigationService.LocalShield > 0)
            {
                if (!firstTipItem)
                    tipBuilder.Append("\n");
                tipBuilder.AddIcon(BitmapFontIcon.Tank);
                tipBuilder.Append($"{GetLoc("Shield")}: {MitigationService.LocalShield}");
                firstTipItem = false;
            }

            // battle npc
            foreach (var (status, _) in MitigationService.BattleNpcActiveStatus)
            {
                if (!firstTipItem)
                    tipBuilder.Append("\n");
                tipBuilder.Append($"{LuminaWrapper.GetStatusName(status.Id)}:");
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

    #region CastBar

    public static class CastBarManager
    {
        // const
        private const uint NodeId = 25;

        #region Funcs

        public static unsafe void Enable()
        {
            var addon = GetAddonByName("_TargetInfoCastBar");
            if (addon == null)
                return;

            var castBar = addon->GetNodeById(2);

            var node = (AtkTextNode*)FetchNode(NodeId, addon);
            if (node == null && !TryMakeTextNode(NodeId, out node))
                return;

            node->FontSize = 10;
            node->AtkResNode.SetWidth(200);
            node->AtkResNode.SetHeight(castBar->GetHeight());
            node->ToggleVisibility(false);
            node->SetPositionShort(
                (short)(castBar->GetXShort() + castBar->GetWidth()),
                castBar->GetYShort()
            );
            node->DrawFlags       |= 1;
            node->TextFlags       =  8;
            node->TextFlags2      =  0;
            node->FontType        =  FontType.MiedingerMed;
            node->TextColor       =  new ByteColor { R = 255, G = 225, B = 255, A = 255 };
            node->EdgeColor       =  new ByteColor { R = 255, G = 0, B   = 162, A = 157 };
            node->BackgroundColor =  new ByteColor { R = 0, G   = 0, B   = 0, A   = 0 };
            node->AlignmentType   =  AlignmentType.BottomLeft;

            LinkNodeAtEnd((AtkResNode*)node, addon);
        }

        public static unsafe void Disable()
        {
            var addon = GetAddonByName("_TargetInfoCastBar");
            if (addon == null)
                return;

            var node = FetchNode(NodeId, addon);
            if (node == null)
                return;
            UnlinkAndFreeTextNode((AtkTextNode*)node, addon);
        }

        public static unsafe void Update()
        {
            if (DamageActionManager.CurrentAction.ActionId == 0)
            {
                Clear();
                return;
            }

            var addon = GetAddonByName("_TargetInfoCastBar");
            if (addon == null)
                return;

            var node = (AtkTextNode*)FetchNode(NodeId, addon);
            if (node == null)
                return;

            node->ToggleVisibility(true);
            node->SetText(DamageActionManager.FetchLocalDamage().ToString("N0"));
        }

        public static unsafe void Clear()
        {
            var addon = GetAddonByName("_TargetInfoCastBar");
            if (addon == null)
                return;

            var node = (AtkTextNode*)FetchNode(NodeId, addon);
            if (node == null)
                return;

            node->ToggleVisibility(false);
        }

        private static unsafe AtkResNode* FetchNode(uint nodeId, AtkUnitBase* addon)
        {
            for (var i = 0; i < addon->UldManager.NodeListCount; i++)
            {
                var t = addon->UldManager.NodeList[i];
                if (t->NodeId == nodeId)
                    return t;
            }

            return null;
        }

        #endregion
    }

    #endregion

    #region PartyList

    public static class PartyListManager
    {
        // const
        private const uint MitigationNodeId = 2025;
        private const uint ShieldNodeId     = 2026;

        #region Funcs

        public static unsafe void Enable()
        {
            var addon = GetAddonByName("_PartyList");
            if (addon == null)
                return;

            EnableMitigationNodes(addon);
            EnableShieldNodes(addon);
        }

        private static unsafe void EnableMitigationNodes(AtkUnitBase* addon)
        {
            foreach (uint i in Enumerable.Range(10, 8))
            {
                var memberNode = addon->GetComponentNodeById(i)->Component;
                var plateNode  = FetchNode(14, memberNode);

                var node = (AtkTextNode*)FetchNode(MitigationNodeId, memberNode);
                if (node == null && !TryMakeTextNode(MitigationNodeId, out node))
                    continue;

                node->FontSize = 10;
                node->AtkResNode.SetWidth(plateNode->Width);
                node->AtkResNode.SetHeight(plateNode->Height);
                node->ToggleVisibility(false);
                node->SetPositionShort(
                    (short)(plateNode->GetXShort() - 5),
                    (short)(plateNode->GetYShort() + 3)
                );
                node->DrawFlags       |= 1;
                node->TextFlags       =  8;
                node->TextFlags2      =  0;
                node->FontType        =  FontType.MiedingerMed;
                node->TextColor       =  new ByteColor { R = 255, G = 225, B = 255, A = 255 };
                node->EdgeColor       =  new ByteColor { R = 255, G = 162, B = 0, A   = 157 };
                node->BackgroundColor =  new ByteColor { R = 0, G   = 0, B   = 0, A   = 0 };
                node->AlignmentType   =  AlignmentType.TopRight;

                LinkNodeAtEnd((AtkResNode*)node, memberNode);
            }
        }

        private static unsafe void EnableShieldNodes(AtkUnitBase* addon)
        {
            foreach (uint i in Enumerable.Range(10, 8))
            {
                var memberNode = addon->GetComponentNodeById(i)->Component;
                var hpNode     = FetchNode(12, memberNode)->GetAsAtkComponentNode()->Component;
                var numNode    = FetchNode(2, hpNode);

                var node = (AtkTextNode*)FetchNode(ShieldNodeId, hpNode);
                if (node == null && !TryMakeTextNode(ShieldNodeId, out node))
                    continue;

                node->FontSize = 8;
                node->AtkResNode.SetWidth(20);
                node->AtkResNode.SetHeight(numNode->Height);
                node->ToggleVisibility(false);
                node->SetPositionShort(
                    (short)(numNode->GetXShort() + numNode->GetWidth() + 2),
                    (short)(numNode->GetYShort() + 3)
                );
                node->DrawFlags       |= 1;
                node->TextFlags       =  8;
                node->TextFlags2      =  0;
                node->FontType        =  FontType.MiedingerMed;
                node->TextColor       =  new ByteColor { R = 255, G = 225, B = 255, A = 255 };
                node->EdgeColor       =  new ByteColor { R = 255, G = 162, B = 0, A   = 157 };
                node->BackgroundColor =  new ByteColor { R = 0, G   = 0, B   = 0, A   = 0 };
                node->AlignmentType   =  AlignmentType.Left;

                LinkNodeAtEnd((AtkResNode*)node, hpNode);
            }
        }

        public static unsafe void Disable()
        {
            var addon = GetAddonByName("_PartyList");
            if (addon == null)
                return;

            DisableMitigationNodes(addon);
            DisableShieldNodes(addon);
        }

        private static unsafe void DisableMitigationNodes(AtkUnitBase* addon)
        {
            foreach (uint i in Enumerable.Range(10, 8))
            {
                var memberNode = addon->GetComponentNodeById(i);

                var node = FetchNode(MitigationNodeId, memberNode->Component);
                if (node == null)
                    continue;

                UnlinkAndFreeTextNode((AtkTextNode*)node, memberNode);
            }
        }

        private static unsafe void DisableShieldNodes(AtkUnitBase* addon)
        {
            foreach (uint i in Enumerable.Range(10, 8))
            {
                var memberNode = addon->GetComponentNodeById(i)->Component;
                var hpNode     = FetchNode(12, memberNode)->GetAsAtkComponentNode();

                var node = (AtkTextNode*)FetchNode(ShieldNodeId, hpNode->Component);
                if (node == null)
                    continue;

                UnlinkAndFreeTextNode(node, hpNode);
            }
        }

        public static unsafe void Update()
        {
            var addon = GetAddonByName("_PartyList");
            if (addon == null)
                return;

            foreach (var memberStatus in MitigationService.FetchParty())
            {
                var memberIndex = FetchMemberIndex(memberStatus.Key) ?? 0;

                UpdateMitigationNode(addon, memberIndex, memberStatus.Value);
                UpdateShieldNodes(addon, memberIndex, memberStatus.Value);
            }
        }

        private static unsafe void UpdateMitigationNode(AtkUnitBase* addon, uint memberIndex, float[] memberStatus)
        {
            var memberNode = addon->GetComponentNodeById(10 + memberIndex)->Component;
            var plateNode  = FetchNode(14, memberNode);
            var namePlate  = FetchNode(17, plateNode);

            var node = (AtkTextNode*)FetchNode(MitigationNodeId, memberNode);
            if (node == null)
                return;

            if (memberStatus[0] == 0 && memberStatus[1] == 0)
            {
                node->ToggleVisibility(false);
                return;
            }

            var desc = $"{Math.Max(memberStatus[0], memberStatus[1]):N0}%";

            node->ToggleVisibility(namePlate->IsVisible());
            node->SetText(desc);
        }

        private static unsafe void UpdateShieldNodes(AtkUnitBase* addon, uint memberIndex, float[] memberStatus)
        {
            var memberNode = addon->GetComponentNodeById(10 + memberIndex)->Component;
            var hpNode     = FetchNode(12, memberNode)->GetAsAtkComponentNode()->Component;
            var numNode    = FetchNode(2, hpNode);

            var node = (AtkTextNode*)FetchNode(ShieldNodeId, hpNode);
            if (node == null)
                return;

            if (memberStatus[2] == 0)
            {
                node->ToggleVisibility(false);
                return;
            }

            var desc = $"{memberStatus[2]:F0}";

            node->ToggleVisibility(numNode->IsVisible());
            node->SetText(desc);
        }

        private static unsafe AtkResNode* FetchNode(uint nodeId, AtkComponentBase* addon)
        {
            for (var i = 0; i < addon->UldManager.NodeListCount; i++)
            {
                var t = addon->UldManager.NodeList[i];
                if (t->NodeId == nodeId)
                    return t;
            }

            return null;
        }

        private static unsafe AtkResNode* FetchNode(uint nodeId, AtkResNode* addon)
        {
            for (var child = addon->ChildNode; child != null; child = child->NextSiblingNode)
            {
                if (child->NodeId == nodeId)
                    return child;
            }

            return null;
        }

        #endregion
    }

    #endregion

    #region DamageMonitor

    public static class DamageActionManager
    {
        // cache
        public static Dictionary<uint, DamageAction> Actions       = [];
        public static DamageAction                   CurrentAction = new() { ActionId = 0 };

        #region Structs

        public struct DamageAction
        {
            [JsonProperty("zone_id")]
            public uint ZoneId { get; set; }

            [JsonProperty("entity_id")]
            public uint EntityId { get; set; }

            [JsonProperty("action_id")]
            public uint ActionId { get; set; }

            [JsonProperty("name")]
            public string Name { get; set; }

            [JsonProperty("type")]
            public string Type { get; set; }

            [JsonProperty("range")]
            public string Range { get; set; }

            [JsonProperty("damage")]
            public float Damage { get; set; }

            [JsonProperty("duration")]
            public float Duration { get; set; }
        }

        public enum EffectType : byte
        {
            Nothing                   = 0,
            Miss                      = 1,
            FullResist                = 2,
            Damage                    = 3,
            Heal                      = 4,
            BlockedDamage             = 5,
            ParriedDamage             = 6,
            Invulnerable              = 7,
            NoEffectText              = 8,
            MpLoss                    = 10,
            MpGain                    = 11,
            TpLoss                    = 12,
            TpGain                    = 13,
            ApplyStatusEffectTarget   = 14,
            ApplyStatusEffectSource   = 15,
            RecoveredFromStatusEffect = 16,
            LoseStatusEffectTarget    = 17,
            LoseStatusEffectSource    = 18,
            StatusNoEffect            = 20,
            ThreatPosition            = 24,
            EnmityAmountUp            = 25,
            EnmityAmountDown          = 26,
            StartActionCombo          = 27,
            Knockback                 = 33,
            Mount                     = 40,
            FullResistStatus          = 55,
            Vfx                       = 59,
            Gauge                     = 60,
            PartialInvulnerable       = 74,
            Interrupt                 = 75,
        }

        public enum EffectDisplayType : byte
        {
            HideActionName = 0,
            ShowActionName = 1,
            ShowItemName   = 2,
            MountName      = 13
        }


        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct EffectHeader
        {
            public uint AnimationTargetId;

            public uint Unknown1;

            public uint ActionId;

            public uint GlobalEffectCounter;

            public float AnimationLockTime;

            public uint Unknown2;

            public ushort HiddenAnimation;

            public ushort Rotation;

            public ushort ActionAnimationId;

            public byte Variation;

            public EffectDisplayType EffectDisplayType;

            public byte Unknown3;

            public byte EffectCount;

            public ushort Unknown4;
        }


        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct Effect
        {
            public EffectType EffectType;
            public byte       Param0;
            public byte       Param1;
            public byte       Param2;
            public byte       Flags1;
            public byte       Flags2;
            public ushort     Value;
        }

        #endregion

        #region Hooks

        // start cast hook
        private static readonly CompSig                  StartCastSig = new("E8 ?? ?? ?? ?? E9 ?? ?? ?? ?? 48 89 BC 24 D0 00 00 00");
        private static          Hook<StartCastDelegate>? StartCastHook;

        // start cast delegate
        private unsafe delegate nint StartCastDelegate(BattleChara* player, ActionType type, uint actionId, nint a4, float rotation, float a6);

        // complete cast hook
        private static readonly CompSig                     CompleteCastSig = new("E8 ?? ?? ?? ?? 48 8B CF E8 ?? ?? ?? ?? 45 33 C0 48 8D 0D");
        private static          Hook<CompleteCastDelegate>? CompleteCastHook;

        // complete cast delegate
        private unsafe delegate nint CompleteCastDelegate(
            BattleChara* player,   ActionType type,     uint  actionId,               uint spellId,            GameObjectId animationTargetId,
            Vector3*     location, float      rotation, short lastUsedActionSequence, int  animationVariation, int          ballistaEntityId
        );

        // action effect hook
        private static readonly CompSig                       ActionEffectSig = new("E8 ?? ?? ?? ?? 48 8B 8D F0 03 00 00");
        private static          Hook<OnActionEffectDelegate>? ActionEffectHook;

        // action effect delegate
        private unsafe delegate void OnActionEffectDelegate(int sourceId, BattleChara* player, Vector3* location, EffectHeader* effectHeader, Effect* effectArray, ulong* effectTrail);

        public static unsafe void Enable()
        {
            StartCastHook?.Dispose();
            StartCastHook = StartCastSig.GetHook<StartCastDelegate>(OnStartCast);
            StartCastHook.Enable();

            CompleteCastHook?.Dispose();
            CompleteCastHook = CompleteCastSig.GetHook<CompleteCastDelegate>(OnCompleteCast);
            CompleteCastHook.Enable();

            ActionEffectHook?.Dispose();
            ActionEffectHook = ActionEffectSig.GetHook<OnActionEffectDelegate>(OnActionEffect);
            ActionEffectHook.Enable();

            // auto emit from pipe
            ActionPipe = new();
            Task.Run(ListenAction);
        }

        public static void Disable()
        {
            StartCastHook?.Dispose();
            CompleteCastHook?.Dispose();
            ActionEffectHook?.Dispose();

            // auto emit from pipe
            ActionPipe?.Cancel();
        }

        private static unsafe nint OnStartCast(BattleChara* player, ActionType type, uint actionId, nint a4, float rotation, float a6)
        {
            // auto emit from cache
            if (CurrentAction.ActionId == 0)
                FindAction(actionId);

            return StartCastHook.Original(player, ActionType.Action, actionId, a4, rotation, a6);
        }

        private static unsafe nint OnCompleteCast(
            BattleChara* player,   ActionType type,     uint  actionId,               uint spellId,            GameObjectId animationTargetId,
            Vector3*     location, float      rotation, short lastUsedActionSequence, int  animationVariation, int          ballistaEntityId
        )
        {
            // auto clear (duration = 0)
            if (CurrentAction.ActionId == actionId && CurrentAction.Duration == 0)
                ClearAction();

            return CompleteCastHook.Original(player, type, actionId, spellId, animationTargetId, location, rotation, lastUsedActionSequence, animationVariation, ballistaEntityId);
        }

        private static unsafe void OnActionEffect(int sourceId, BattleChara* player, Vector3* location, EffectHeader* effectHeader, Effect* effectArray, ulong* effectTrail)
        {
            ActionEffectHook.Original(sourceId, player, location, effectHeader, effectArray, effectTrail);

            try
            {
                for (var i = 0; i < effectHeader->EffectCount; i++)
                {
                    var targetId = (uint)(effectTrail[i] & uint.MaxValue);
                    var actionId = effectHeader->EffectDisplayType switch
                    {
                        EffectDisplayType.MountName => 0xD_000_000 + effectHeader->ActionId,
                        EffectDisplayType.ShowItemName => 0x2_000_000 + effectHeader->ActionId,
                        _ => effectHeader->ActionAnimationId
                    };

                    for (var j = 0; j < 8; j++)
                    {
                        ref var effect = ref effectArray[i * 8 + j];
                        if (effect.EffectType == 0)
                            continue;

                        // unzip damage [high8: Flag1, low16: Value]
                        uint damage = effect.Value;
                        if ((effect.Flags2 & 0x40) == 0x40)
                            damage += (uint)effect.Flags1 << 16;
                    }
                }
            }
            catch (Exception ex) { Error($"[AutoDisplayMitigationInfo] 技能生效回调解析失败: {ex}"); }
        }

        #endregion

        #region Action

        private static void FindAction(uint actionId)
        {
            // match action from cache
            if (!Actions.TryGetValue(actionId, out CurrentAction))
                return;

            // duration?
            if (CurrentAction.Duration > 0)
                Task.Run(async () => await Timer.Emit(ClearAction, (int)(CurrentAction.Duration * 1000)));
        }

        private static CancellationTokenSource? ActionPipe;

        private static void ListenAction()
        {
            return;
            while (!ActionPipe.IsCancellationRequested)
            {
                using var pipe = new NamedPipeServerStream("DR-ADMI", PipeDirection.In, 4);
                pipe.WaitForConnection();

                try
                {
                    using var reader = new StreamReader(pipe);
                    var       json   = reader.ReadLine();
                    if (string.IsNullOrEmpty(json))
                        continue;


                    CurrentAction = JsonConvert.DeserializeObject<DamageAction>(json);
                    if (CurrentAction.Duration > 0)
                        Task.Run(async () => await Timer.Emit(ClearAction, (int)(CurrentAction.Duration * 1000)));
                }
                catch (Exception ex) { Error($"[AutoDisplayMitigationInfo] Damage Action Pipe Listen Failed: {ex}"); }
            }
        }

        private static void ClearAction()
            => CurrentAction = new DamageAction() { ActionId = 0 };

        #endregion

        #region Funcs

        public static float FetchLocalDamage()
        {
            if (CurrentAction.ActionId == 0)
                return 0;

            var status = MitigationService.FetchLocal();
            var mitigation = CurrentAction.Type switch
            {
                "Physical" => status[0],
                "Magical" => status[1],
                _ => 0
            };
            var shield = status[2];

            return CurrentAction.Damage * (1 - (mitigation / 100)) - shield;
        }

        #endregion
    }

    #endregion

    #region Timer

    public static class Timer
    {
        private static CancellationTokenSource? CTS;

        public static async Task Emit(Action onElapsed, int delay)
        {
            // cancel previous timer
            CTS?.Cancel();
            CTS?.Dispose();

            // create a new timer
            CTS = new CancellationTokenSource();
            var token = CTS.Token;

            try
            {
                await Task.Delay(delay, token);

                // check if the token is not canceled
                if (!token.IsCancellationRequested)
                    onElapsed();
            }
            catch (TaskCanceledException) { }
        }
    }

    #endregion

    #region Mitigation

    public unsafe class MitigationManager(MitigationManager.Storage config)
    {
        // cache
        private Dictionary<uint, Status> statusDict = [];

        // local player
        public readonly Dictionary<Status, float> LocalActiveStatus = [];
        public          float                     LocalShield => ((float)Control.GetLocalPlayer()->ShieldValue / 100 * Control.GetLocalPlayer()->Health);

        // party member
        public readonly Dictionary<uint, Dictionary<Status, float>> PartyActiveStatus = [];

        public Dictionary<uint, float> PartyShield
        {
            get
            {
                var partyShield = new Dictionary<uint, float>();
                var partyList   = DService.PartyList.OrderBy(member => FetchMemberIndex(member.ObjectId) ?? 0).ToList();

                foreach (var member in partyList)
                {
                    if (DService.ObjectTable.SearchById(member.ObjectId) is ICharacter memberChara)
                        partyShield[member.ObjectId] = ((float)memberChara.ShieldPercentage / 100 * memberChara.CurrentHp);
                }

                return partyShield;
            }
        }

        // battle npc
        public readonly Dictionary<Status, float> BattleNpcActiveStatus = [];

        // config
        public class Storage
        {
            public Status[] Statuses = [];
        }

        #region Structs

        public struct StatusInfo
        {
            [JsonProperty("physical")]
            public float Physical { get; private set; }

            [JsonProperty("magical")]
            public float Magical { get; private set; }
        }

        public struct Status : IEquatable<Status>
        {
            [JsonProperty("id")]
            public uint Id { get; private set; }

            [JsonProperty("name")]
            public string Name { get; private set; }

            [JsonProperty("mitigation")]
            public StatusInfo Info { get; private set; }

            [JsonProperty("on_member")]
            public bool OnMember { get; private set; }

            #region Equals

            public bool Equals(Status other) => Id == other.Id;

            public override bool Equals(object? obj) => obj is Status other && Equals(other);

            public override int GetHashCode() => (int)Id;

            public static bool operator ==(Status left, Status right) => left.Equals(right);

            public static bool operator !=(Status left, Status right) => !left.Equals(right);

            #endregion
        }

        #endregion

        #region Funcs

        public void InitStatusMap()
            => statusDict = config.Statuses.ToDictionary(x => x.Id, x => x);

        public void Update()
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
                if (statusDict.TryGetValue(status.StatusId, out var mitigation))
                    LocalActiveStatus.Add(mitigation, status.RemainingTime);
            }

            // party
            var partyList = DService.PartyList.OrderBy(member => FetchMemberIndex(member.ObjectId) ?? 0).ToList();
            foreach (var member in partyList)
            {
                var activateStatus = new Dictionary<Status, float>();
                foreach (var status in member.Statuses)
                {
                    if (statusDict.TryGetValue(status.StatusId, out var mitigation))
                        activateStatus.Add(mitigation, status.RemainingTime);
                }

                PartyActiveStatus[member.ObjectId] = activateStatus;
            }

            // battle npc
            var currentTarget = DService.Targets.Target;
            if (currentTarget is IBattleNpc battleNpc)
            {
                var statusList = battleNpc.ToBCStruct()->StatusManager.Status;
                foreach (var status in statusList)
                {
                    if (statusDict.TryGetValue(status.StatusId, out var mitigation))
                        BattleNpcActiveStatus.Add(mitigation, status.RemainingTime);
                }
            }
        }

        public void Clear()
        {
            LocalActiveStatus.Clear();
            PartyActiveStatus.Clear();
            BattleNpcActiveStatus.Clear();
        }

        public bool IsLocalEmpty()
            => LocalActiveStatus.Count == 0 && LocalShield == 0 && BattleNpcActiveStatus.Count == 0;

        public float[] FetchLocal()
        {
            var activeStatus = LocalActiveStatus.Concat(BattleNpcActiveStatus).ToDictionary(kv => kv.Key, kv => kv.Value);
            return
            [
                Reduction(activeStatus.Keys.Select(x => x.Info.Physical)),
                Reduction(activeStatus.Keys.Select(x => x.Info.Magical)),
                LocalShield
            ];
        }

        public Dictionary<uint, float[]> FetchParty()
        {
            if (DService.PartyList.Count == 0)
                return new Dictionary<uint, float[]>() { { GameState.EntityID, FetchLocal() } };

            var partyValues = new Dictionary<uint, float[]>();
            foreach (var memberActiveStatus in PartyActiveStatus)
            {
                var activeStatus = memberActiveStatus.Value.Concat(BattleNpcActiveStatus).ToDictionary(kv => kv.Key, kv => kv.Value);
                partyValues[memberActiveStatus.Key] =
                [
                    Reduction(activeStatus.Keys.Select(x => x.Info.Physical)),
                    Reduction(activeStatus.Keys.Select(x => x.Info.Magical)),
                    PartyShield[memberActiveStatus.Key]
                ];
            }

            return partyValues;
        }


        public static float Reduction(IEnumerable<float> mitigations) =>
            (1f - mitigations.Aggregate(1f, (acc, m) => acc * (1f - (m / 100f)))) * 100f;

        #endregion
    }

    #endregion

    #region Utils

    private static IPartyMember? FetchMember(uint id)
        => DService.PartyList.FirstOrDefault(m => m.ObjectId == id);

    private static unsafe uint? FetchMemberIndex(uint id)
        => (uint)AgentHUD.Instance()->PartyMembers.ToArray()
                                                  .Select((m, i) => (m, i))
                                                  .FirstOrDefault(t => t.m.EntityId == id).i;

    private static string NumToKilo(float num)
        => num > 1000 ? $"{num / 1_000:N0}K" : $"{num:N0}";

    #endregion

    #region Config

    private class ModuleStorage : ModuleConfiguration
    {
        // mitigation
        public readonly MitigationManager.Storage MitigationStorage = new();

        // activate
        public bool OnlyInCombat = true;

        // ui
        public bool TransparentOverlay;
        public bool ResizeableOverlay = true;
        public bool MoveableOverlay   = true;
    }

    #endregion
}
