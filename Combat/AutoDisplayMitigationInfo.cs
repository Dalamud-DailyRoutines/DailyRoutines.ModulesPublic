using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using DailyRoutines.Abstracts;
using DailyRoutines.Helpers;
using DailyRoutines.Infos;
using DailyRoutines.Managers;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.Gui.Dtr;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Hooking;
using Dalamud.Interface.Utility;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.Graphics;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Newtonsoft.Json;
using Status = Lumina.Excel.Sheets.Status;


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
    private static ModuleStorage? moduleConfig;

    // asset
    private static readonly byte[] damagePhysicalStr;
    private static readonly byte[] damageMagicalStr;

    static AutoDisplayMitigationInfo()
    {
        damagePhysicalStr = new SeString(new IconPayload(BitmapFontIcon.DamagePhysical)).Encode();
        damageMagicalStr  = new SeString(new IconPayload(BitmapFontIcon.DamageMagical)).Encode();
    }

    public override void Init()
    {
        moduleConfig = LoadConfig<ModuleStorage>() ?? new ModuleStorage();

        // enable cast hook
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

        // fetch remote resource
        Task.Run(async () => await RemoteRepoManager.FetchAll());

        // zone hook
        DService.ClientState.TerritoryChanged += OnZoneChanged;

        // party list
        DService.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "_PartyList", PartyListManager.OnPartyListSetup);
        DService.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "_PartyList", PartyListManager.OnPartyListFinalize);

        // target cast bar
        // DService.AddonLifecycle.RegisterListener(AddonEvent.PostDraw, "_TargetInfoCastBar", CastBarManager.Update);
        // DService.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "_TargetInfoCastBar", CastBarManager.Disable);

        // refresh mitigation status
        FrameworkManager.Register(OnFrameworkUpdate);
        FrameworkManager.Register(OnFrameworkUpdateInterval, throttleMS: 500);

        UnsafeInit();
    }

    private static unsafe void UnsafeInit()
    {
        // party list
        if (PartyListManager.PartyList is not null)
            PartyListManager.Enable(PartyListManager.PartyList);
    }

    public override void Uninit()
    {
        // zone hook
        DService.ClientState.TerritoryChanged -= OnZoneChanged;

        // party list refresh
        DService.AddonLifecycle.UnregisterListener(AddonEvent.PostDraw, PartyListManager.OnPartyListSetup);
        DService.AddonLifecycle.UnregisterListener(AddonEvent.PreFinalize, PartyListManager.OnPartyListFinalize);

        // target cast bar refresh
        // DService.AddonLifecycle.UnregisterListener(AddonEvent.PostDraw, CastBarManager.Update);

        // refresh mitigation status
        FrameworkManager.Unregister(OnFrameworkUpdate);
        FrameworkManager.Unregister(OnFrameworkUpdateInterval);

        // status bar
        StatusBarManager.Disable();

        // disable cast hook
        DamageActionManager.Disable();

        base.Uninit();
    }

    public override void ConfigUI()
    {
        if (ImGui.Checkbox(GetLoc("OnlyInCombat"), ref moduleConfig.OnlyInCombat))
            SaveConfig(moduleConfig);

        if (ImGui.Checkbox(GetLoc("TransparentOverlay"), ref moduleConfig.TransparentOverlay))
        {
            SaveConfig(moduleConfig);

            if (moduleConfig.TransparentOverlay)
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

        if (ImGui.Checkbox(GetLoc("ResizeableOverlay"), ref moduleConfig.ResizeableOverlay))
        {
            SaveConfig(moduleConfig);

            if (moduleConfig.ResizeableOverlay)
                Overlay.Flags &= ~ImGuiWindowFlags.NoResize;
            else
                Overlay.Flags |= ImGuiWindowFlags.NoResize;
        }

        if (ImGui.Checkbox(GetLoc("MoveableOverlay"), ref moduleConfig.MoveableOverlay))
        {
            SaveConfig(moduleConfig);

            if (!moduleConfig.MoveableOverlay)
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
            ImGui.Image(barrierIcon.GetWrapOrEmpty().ImGuiHandle, ScaledVector2(24f));

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

        if (moduleConfig.TransparentOverlay)
        {
            Overlay.Flags |= ImGuiWindowFlags.NoBackground;
            Overlay.Flags |= ImGuiWindowFlags.NoTitleBar;
        }
        else
        {
            Overlay.Flags &= ~ImGuiWindowFlags.NoBackground;
            Overlay.Flags &= ~ImGuiWindowFlags.NoTitleBar;
        }

        if (moduleConfig.ResizeableOverlay)
            Overlay.Flags &= ~ImGuiWindowFlags.NoResize;
        else
            Overlay.Flags |= ImGuiWindowFlags.NoResize;

        if (!moduleConfig.MoveableOverlay)
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
        ImGuiHelpers.SeStringWrapped(damagePhysicalStr);

        ImGui.SameLine();
        ImGui.Text($"{status.Key.Info.Physical}% ");

        ImGui.SameLine();
        ImGuiHelpers.SeStringWrapped(damageMagicalStr);

        ImGui.SameLine();
        ImGui.Text($"{status.Key.Info.Magical}% ");
    }

    #endregion

    #region Hooks

    private static unsafe void OnFrameworkUpdate(IFramework _)
    {
        var combatInactive = moduleConfig.OnlyInCombat && !DService.Condition[ConditionFlag.InCombat];
        if (DService.ClientState.IsPvP || combatInactive || Control.GetLocalPlayer() is null)
            return;

        // update party list
        PartyListManager.Update();
    }

    private static unsafe void OnFrameworkUpdateInterval(IFramework _)
    {
        var combatInactive = moduleConfig.OnlyInCombat && !DService.Condition[ConditionFlag.InCombat];
        if (DService.ClientState.IsPvP || combatInactive || Control.GetLocalPlayer() is null)
        {
            StatusBarManager.Clear();
            return;
        }

        // update status
        MitigationManager.Update();
        StatusBarManager.Update();
    }

    private static void OnZoneChanged(ushort zoneId)
        => RemoteRepoManager.FetchDamageActions(zoneId);

    #endregion

    #region RemoteCache

    private static class RemoteRepoManager
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
                    Error($"[AutoDisplayMitigationInfo] 远程减伤技能文件解析失败: {json}");
                else
                    MitigationManager.StatusDict = resp.ToDictionary(x => x.Id, x => x);
            }
            catch (Exception ex) { Error($"[AutoDisplayMitigationInfo] 远程减伤技能文件获取失败: {ex}"); }
        }

        // TODO: separate by zone
        private static DamageActionManager.DamageAction[] actions = [];

        public static async Task FetchDamageActions()
        {
            try
            {
                var json = await HttpClientHelper.Get().GetStringAsync($"{Uri}/damage");
                var resp = JsonConvert.DeserializeObject<DamageActionManager.DamageAction[]>(json);
                if (resp == null)
                    Error($"[AutoDisplayMitigationInfo] 远程技能伤害文件解析失败: {json}");
                else
                    actions = resp;
            }
            catch (Exception ex) { Error($"[AutoDisplayMitigationInfo] 远程技能伤害文件获取失败: {ex}"); }
        }

        public static void FetchDamageActions(uint zoneId)
            => DamageActionManager.Actions = actions.Where(x => x.ZoneId == zoneId).ToDictionary(x => x.ActionId, x => x);

        public static async Task FetchAll()
        {
            try
            {
                var tasks = new[] { FetchMitigationStatuses(), FetchDamageActions() };
                await Task.WhenAll(tasks);
            }
            catch (Exception ex) { Error($"[AutoDisplayMitigationInfo] 远程资源获取失败: {ex}"); }
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
                tipBuilder.Append($"{LuminaWrapper.GetStatusName(status.Id)}:");
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

    private static class CastBarManager
    {
        // const
        private const uint NodeId = 25;

        // params
        private static bool hasBuilt;

        #region Funcs

        public static unsafe void Enable()
        {
            var addon = GetAddonByName("_TargetInfoCastBar");
            if (addon == null)
                return;

            var castBar = addon->GetNodeById(2);
            if (castBar == null)
                return;

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
            hasBuilt = true;
        }

        public static unsafe void Disable(AddonEvent type, AddonArgs args)
        {
            var addon = GetAddonByName("_TargetInfoCastBar");
            if (addon == null)
                return;

            var node = FetchNode(NodeId, addon);
            if (node == null)
                return;

            UnlinkAndFreeTextNode((AtkTextNode*)node, addon);
            hasBuilt = false;
        }

        public static unsafe void Update(AddonEvent type, AddonArgs args)
        {
            if (!hasBuilt)
                Enable();

            if (DamageActionManager.CurrentAction.ActionId == 0 || !IsScreenReady())
            {
                Clear();
                return;
            }

            var addon = (AtkUnitBase*)args.Addon;
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

    private static unsafe class PartyListManager
    {
        // const
        private const uint MitigationNodeId = 100025;
        private const uint ShieldNodeId     = 200025;

        // params
        private static bool            hasBuilt;
        public static  AddonPartyList* PartyList => (AddonPartyList*)GetAddonByName("_PartyList");

        // nodes
        private static readonly nint[] mitigationNodes = new nint[8];
        private static readonly nint[] shieldNodes     = new nint[8];

        #region Funcs

        public static void OnPartyListSetup(AddonEvent type, AddonArgs args)
            => Enable((AddonPartyList*)args.Addon);

        public static void Enable(AddonPartyList* addonPartyList)
        {
            DService.Framework.RunOnFrameworkThread(() =>
            {
                if (hasBuilt)
                    return;

                EnableMitigationNodes(addonPartyList);
                EnableShieldNodes(addonPartyList);

                hasBuilt = true;
            });
        }

        private static void EnableMitigationNodes(AddonPartyList* addonPartyList)
        {
            foreach (var index in Enumerable.Range(0, 8))
            {
                ref var partyMember   = ref addonPartyList->PartyMembers[index];
                ref var nameContainer = ref partyMember.NameAndBarsContainer;

                if (!TryMakeTextNode(MitigationNodeId, out var node))
                    continue;
                mitigationNodes[index] = (nint)node;

                node->FontSize = 10;
                node->AtkResNode.SetWidth(nameContainer->GetWidth());
                node->AtkResNode.SetHeight(nameContainer->GetHeight());
                node->ToggleVisibility(false);
                node->SetPositionShort(
                    (short)(nameContainer->GetXShort() - 5),
                    (short)(nameContainer->GetYShort() + 3)
                );
                node->DrawFlags       |= 1;
                node->TextFlags       =  8;
                node->TextFlags2      =  0;
                node->FontType        =  FontType.MiedingerMed;
                node->TextColor       =  new ByteColor { R = 255, G = 225, B = 255, A = 255 };
                node->EdgeColor       =  new ByteColor { R = 255, G = 162, B = 0, A   = 157 };
                node->BackgroundColor =  new ByteColor { R = 0, G   = 0, B   = 0, A   = 0 };
                node->AlignmentType   =  AlignmentType.TopRight;

                LinkNodeAtEnd((AtkResNode*)node, partyMember.PartyMemberComponent->OwnerNode->Component);
            }
        }

        private static void EnableShieldNodes(AddonPartyList* addonPartyList)
        {
            foreach (var index in Enumerable.Range(0, 8))
            {
                ref var partyMember = ref addonPartyList->PartyMembers[index];
                ref var hpComponent = ref partyMember.HPGaugeComponent;
                var     numNode     = hpComponent->GetTextNodeById(2);

                if (!TryMakeTextNode(ShieldNodeId, out var node))
                    continue;
                shieldNodes[index] = (nint)node;

                node->FontSize = 8;
                node->AtkResNode.SetWidth(numNode->GetWidth());
                node->AtkResNode.SetHeight(numNode->GetHeight());
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

                LinkNodeAtEnd((AtkResNode*)node, hpComponent->OwnerNode->Component);
            }
        }

        public static void OnPartyListFinalize(AddonEvent type, AddonArgs args)
            => Disable((AddonPartyList*)args.Addon);

        public static void Disable(AddonPartyList* addonPartyList)
        {
            DService.Framework.RunOnFrameworkThread(() =>
            {
                if (!hasBuilt)
                    return;

                DisableMitigationNodes(addonPartyList);
                DisableShieldNodes(addonPartyList);

                hasBuilt = false;
            });
        }

        private static void DisableMitigationNodes(AddonPartyList* addonPartyList)
        {
            foreach (var index in Enumerable.Range(0, 8))
            {
                ref var partyMember = ref addonPartyList->PartyMembers[index];
                UnlinkAndFreeTextNode((AtkTextNode*)mitigationNodes[index], partyMember.PartyMemberComponent->OwnerNode);
            }
        }

        private static void DisableShieldNodes(AddonPartyList* addonPartyList)
        {
            foreach (var index in Enumerable.Range(0, 8))
            {
                ref var partyMember = ref addonPartyList->PartyMembers[index];
                ref var hpComponent = ref partyMember.HPGaugeComponent;
                UnlinkAndFreeTextNode((AtkTextNode*)shieldNodes[index], hpComponent->OwnerNode);
            }
        }

        public static void Update()
        {
            if (PartyList is null || !hasBuilt)
                return;

            foreach (uint index in Enumerable.Range(0, 8))
            {
                UpdateMitigationNode(index, [0, 0, 0]);
                UpdateShieldNode(index, [0, 0, 0]);
            }

            foreach (var memberStatus in MitigationManager.FetchParty())
            {
                if (FetchMemberIndex(memberStatus.Key) is { } memberIndex)
                {
                    UpdateMitigationNode(memberIndex, memberStatus.Value);
                    UpdateShieldNode(memberIndex, memberStatus.Value);
                }
            }
        }

        private static void UpdateMitigationNode(uint memberIndex, float[] memberStatus)
        {
            var nameNode = PartyList->PartyMembers[(int)memberIndex].Name;
            var node     = (AtkTextNode*)mitigationNodes[(int)memberIndex];

            if (memberStatus[0] == 0 && memberStatus[1] == 0)
            {
                node->ToggleVisibility(false);
                return;
            }

            var desc = $"{Math.Max(memberStatus[0], memberStatus[1]):N0}%";

            node->ToggleVisibility(nameNode->IsVisible());
            node->SetText(desc);
        }

        private static void UpdateShieldNode(uint memberIndex, float[] memberStatus)
        {
            var node = (AtkTextNode*)shieldNodes[(int)memberIndex];

            if (memberStatus[2] == 0)
            {
                node->ToggleVisibility(false);
                return;
            }

            var desc = $"{memberStatus[2]:F0}";

            node->ToggleVisibility(true);
            node->SetText(desc);
        }

        #endregion
    }

    #endregion

    #region DamageMonitor

    private static class DamageActionManager
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

        // action effect hook
        private static readonly CompSig                       actionEffectSig = new("E8 ?? ?? ?? ?? 48 8B 8D F0 03 00 00");
        private static          Hook<OnActionEffectDelegate>? actionEffectHook;

        // action effect delegate
        private unsafe delegate void OnActionEffectDelegate(int sourceId, BattleChara* player, Vector3* location, EffectHeader* effectHeader, Effect* effectArray, ulong* effectTrail);

        public static unsafe void Enable()
        {
            UseActionManager.RegCharacterStartCast(OnStartCast);
            UseActionManager.RegCharacterCompleteCast(OnCompleteCast);

            actionEffectHook?.Dispose();
            actionEffectHook = actionEffectSig.GetHook<OnActionEffectDelegate>(OnActionEffect);
            actionEffectHook.Enable();

            // auto emit from pipe
            actionPipe = new();
            Task.Run(ListenAction);
        }

        public static unsafe void Disable()
        {
            UseActionManager.UnregCharacterStartCast(OnStartCast);
            UseActionManager.UnregCharacterCompleteCast(OnCompleteCast);

            actionEffectHook?.Dispose();

            // auto emit from pipe
            actionPipe?.Cancel();
        }

        private static unsafe void OnStartCast(nint a1, BattleChara* player, ActionType type, uint actionId, nint a4, float rotation, float a6)
        {
            // auto emit from cache
            if (CurrentAction.ActionId == 0)
                FindAction(actionId);
        }

        private static unsafe void OnCompleteCast(
            nint     a1,       BattleChara* player,   ActionType type,                   uint actionId,           uint spellId, GameObjectId animationTargetId,
            Vector3* location, float        rotation, short      lastUsedActionSequence, int  animationVariation, int  ballistaEntityId
        )
        {
            // auto clear (duration = 0)
            if (CurrentAction.ActionId == actionId && CurrentAction.Duration == 0)
                ClearAction();
        }

        private static unsafe void OnActionEffect(int sourceId, BattleChara* player, Vector3* location, EffectHeader* effectHeader, Effect* effectArray, ulong* effectTrail)
        {
            actionEffectHook.Original(sourceId, player, location, effectHeader, effectArray, effectTrail);

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

        private static CancellationTokenSource? actionPipe;

        private static void ListenAction()
        {
            /*return;
            while (!actionPipe.IsCancellationRequested)
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
            }*/
        }

        private static void ClearAction()
            => CurrentAction = new DamageAction() { ActionId = 0 };

        #endregion

        #region Funcs

        public static float FetchLocalDamage()
        {
            if (CurrentAction.ActionId == 0)
                return 0;

            var status = MitigationManager.FetchLocal();
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

    private static class Timer
    {
        private static CancellationTokenSource? cts;

        public static async Task Emit(Action onElapsed, int delay)
        {
            // cancel previous timer
            cts?.Cancel();
            cts?.Dispose();

            // create a new timer
            cts = new CancellationTokenSource();
            var token = cts.Token;

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

    private static unsafe class MitigationManager
    {
        // cache
        public static Dictionary<uint, Status> StatusDict = [];

        // local player
        public static readonly Dictionary<Status, float> LocalActiveStatus = [];

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
        public static readonly Dictionary<uint, Dictionary<Status, float>> PartyActiveStatus = [];

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
                    if (member.ObjectId == 0)
                        continue;

                    if (DService.ObjectTable.SearchById(member.ObjectId) is ICharacter memberChara)
                        partyShield[member.ObjectId] = ((float)memberChara.ShieldPercentage / 100 * memberChara.CurrentHp);
                }

                return partyShield;
            }
        }

        // battle npc
        public static readonly Dictionary<Status, float> BattleNpcActiveStatus = [];

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
                if (StatusDict.TryGetValue(status.StatusId, out var mitigation))
                    LocalActiveStatus.TryAdd(mitigation, status.RemainingTime);
            }

            // party
            var partyList = DService.PartyList;
            if (partyList.Count != 0)
            {
                foreach (var member in partyList)
                {
                    if (member.ObjectId == 0)
                        continue;

                    var activeStatus = new Dictionary<Status, float>();
                    foreach (var status in member.Statuses)
                    {
                        if (StatusDict.TryGetValue(status.StatusId, out var mitigation))
                            activeStatus.TryAdd(mitigation, status.RemainingTime);
                    }

                    PartyActiveStatus[member.ObjectId] = activeStatus;
                }
            }

            // battle npc
            var currentTarget = DService.Targets.Target;
            if (currentTarget is IBattleNpc battleNpc)
            {
                var statusList = battleNpc.ToBCStruct()->StatusManager.Status;
                foreach (var status in statusList)
                {
                    if (StatusDict.TryGetValue(status.StatusId, out var mitigation))
                        BattleNpcActiveStatus.TryAdd(mitigation, status.RemainingTime);
                }
            }
        }

        public static void Clear()
        {
            LocalActiveStatus.Clear();
            PartyActiveStatus.Clear();
            BattleNpcActiveStatus.Clear();
        }

        public static bool IsLocalEmpty()
            => LocalActiveStatus.Count == 0 && LocalShield == 0 && BattleNpcActiveStatus.Count == 0;

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

        public static Dictionary<uint, float[]> FetchParty()
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
                    PartyShield.GetValueOrDefault(memberActiveStatus.Key, 0)
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

    private static unsafe uint? FetchMemberIndex(uint entityId)
        => AgentHUD.Instance()->PartyMembers.ToArray()
                                            .Select((m, i) => (Member: m, Index: (uint)i))
                                            .FirstOrDefault(t => t.Member.EntityId == entityId).Index;

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
