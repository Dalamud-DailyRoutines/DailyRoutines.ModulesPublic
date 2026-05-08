using System.Collections.Frozen;
using System.Numerics;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using DailyRoutines.Manager;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Gui.Dtr;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Interface.Utility;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Newtonsoft.Json;
using OmenTools.Dalamud;
using OmenTools.Interop.Game.Lumina;
using OmenTools.OmenService;
using OmenTools.Threading;
using Control = FFXIVClientStructs.FFXIV.Client.Game.Control.Control;
using LuminaStatus = Lumina.Excel.Sheets.Status;
using GameBattleChara = FFXIVClientStructs.FFXIV.Client.Game.Character.BattleChara;

namespace DailyRoutines.ModulesPublic;

public class AutoDisplayMitigationInfo : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoDisplayMitigationInfoTitle"),
        Description = Lang.Get("AutoDisplayMitigationInfoDescription"),
        Category    = ModuleCategory.Combat,
        Author      = ["HaKu"]
    };

    public override ModulePermission Permission { get; } = new() { AllDefaultEnabled = true };

    private          Config          config = null!;
    private          IDtrBarEntry?   barEntry;
    private readonly MitigationState state = new();

    private readonly CancellationTokenSource? remoteFetchCancelSource = new();

    private bool isCombatEventsRegistered;
    private bool isNeedToDrawOnPartyList;

    protected override void Init()
    {
        config = Config.Load(this) ?? new();

        _ = RemoteRepoManager.FetchMitigationStatusesAsync(remoteFetchCancelSource.Token);

        DService.Instance().ClientState.TerritoryChanged += OnZoneChanged;
        DService.Instance().Condition.ConditionChange    += OnConditionChanged;

        if (GameState.ContentFinderCondition != 0)
            OnZoneChanged(0);
        if (DService.Instance().Condition[ConditionFlag.InCombat])
            OnConditionChanged(ConditionFlag.InCombat, true);
    }

    protected override void Uninit()
    {
        DService.Instance().Condition.ConditionChange    -= OnConditionChanged;
        DService.Instance().ClientState.TerritoryChanged -= OnZoneChanged;

        UnregCombatEvents();

        barEntry?.Remove();
        barEntry = null;

        remoteFetchCancelSource?.Cancel();
        remoteFetchCancelSource?.Dispose();
    }

    protected override void ConfigUI()
    {
        if (ImGui.Checkbox(Lang.Get("TransparentOverlay"), ref config.TransparentOverlay))
        {
            config.Save(this);

            if (config.TransparentOverlay)
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

        if (ImGui.Checkbox(Lang.Get("ResizeableOverlay"), ref config.ResizeableOverlay))
        {
            config.Save(this);

            if (config.ResizeableOverlay)
                Overlay.Flags &= ~ImGuiWindowFlags.NoResize;
            else
                Overlay.Flags |= ImGuiWindowFlags.NoResize;
        }

        if (ImGui.Checkbox(Lang.Get("MoveableOverlay"), ref config.MoveableOverlay))
        {
            config.Save(this);

            if (!config.MoveableOverlay)
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

    protected override void OverlayUI()
    {
        if (state.IsLocalEmpty)
            return;

        ImGuiHelpers.SeStringWrapped(barEntry?.Text?.Encode() ?? []);

        ImGui.Separator();

        using var table = ImRaii.Table("StatusTable", 3);
        if (!table)
            return;

        ImGui.TableSetupColumn("Icon",  ImGuiTableColumnFlags.WidthFixed,   24f * GlobalUIScale);
        ImGui.TableSetupColumn("Name",  ImGuiTableColumnFlags.WidthStretch, 20);
        ImGui.TableSetupColumn("Value", ImGuiTableColumnFlags.WidthStretch, 20);

        if (!DService.Instance().Texture.TryGetFromGameIcon(new(210405), out var barrierIcon))
            return;

        foreach (var status in state.LocalActiveStatus)
            DrawStatusRow(status);

        foreach (var status in state.TargetActiveStatus)
            DrawStatusRow(status);

        if (state.LocalShield > 0)
        {
            if (!state.IsLocalEmpty)
                ImGui.TableNextRow();

            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.Image(barrierIcon.GetWrapOrEmpty().Handle, ScaledVector2(24f));

            ImGui.TableNextColumn();
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted($"{Lang.Get("Shield")}");

            ImGui.TableNextColumn();
            ImGui.TextUnformatted($"{state.LocalShield}");
        }
    }

    private void SetOverlay()
    {
        Overlay            ??= new(this);
        Overlay.WindowName =   Lang.Get("AutoDisplayMitigationInfoTitle");
        Overlay.Flags      &=  ~ImGuiWindowFlags.NoTitleBar;
        Overlay.Flags      &=  ~ImGuiWindowFlags.AlwaysAutoResize;

        if (config.TransparentOverlay)
        {
            Overlay.Flags |= ImGuiWindowFlags.NoBackground;
            Overlay.Flags |= ImGuiWindowFlags.NoTitleBar;
        }
        else
        {
            Overlay.Flags &= ~ImGuiWindowFlags.NoBackground;
            Overlay.Flags &= ~ImGuiWindowFlags.NoTitleBar;
        }

        if (config.ResizeableOverlay)
            Overlay.Flags &= ~ImGuiWindowFlags.NoResize;
        else
            Overlay.Flags |= ImGuiWindowFlags.NoResize;

        if (!config.MoveableOverlay)
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

    private static void DrawStatusRow(ActiveMitigation status)
    {
        if (!LuminaGetter.TryGetRow<LuminaStatus>(status.StatusID, out var row))
            return;
        if (!DService.Instance().Texture.TryGetFromGameIcon(new(row.Icon), out var icon))
            return;

        ImGui.TableNextRow();

        ImGui.TableNextColumn();
        ImGui.Image(icon.GetWrapOrEmpty().Handle, ScaledVector2(24f));

        ImGui.TableNextColumn();
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted($"{row.Name} ({status.RemainingTime:F1}s)");
        ImGuiOm.TooltipHover($"{status.StatusID}");

        ImGui.TableNextColumn();
        ImGuiHelpers.SeStringWrapped(DamagePhysicalString);

        ImGui.SameLine();
        ImGui.TextUnformatted($"{status.Physical}% ");

        ImGui.SameLine();
        ImGuiHelpers.SeStringWrapped(DamageMagicalString);

        ImGui.SameLine();
        ImGui.TextUnformatted($"{status.Magical}% ");
    }

    private void RegCombatEvents()
    {
        if (isCombatEventsRegistered)
            return;

        WindowManager.Instance().PostDraw += Draw;
        FrameworkManager.Instance().Reg(OnUpdate, 500);

        barEntry         ??= DService.Instance().DTRBar.Get("DailyRoutines-AutoDisplayMitigationInfo");
        barEntry.OnClick =   _ =>
        {
            if (Overlay == null)
                SetOverlay();
            
            Overlay.IsOpen ^= true;
        };

        isCombatEventsRegistered = true;
    }

    private void UnregCombatEvents()
    {
        if (!isCombatEventsRegistered)
            return;

        WindowManager.Instance().PostDraw -= Draw;
        FrameworkManager.Instance().Unreg(OnUpdate);
        state.Clear();

        if (barEntry != null)
        {
            barEntry.Shown   = false;
            barEntry.Tooltip = null;
            barEntry.Text    = null;
        }

        isCombatEventsRegistered = false;
    }

    private void UpdateStatusBar()
    {
        if (barEntry == null)
            return;

        if (state.IsLocalEmpty)
        {
            barEntry.Shown   = false;
            barEntry.Tooltip = null;
            barEntry.Text    = null;
            return;
        }

        var textBuilder  = new SeStringBuilder();
        var firstBarItem = true;

        AppendSummary(ref textBuilder, ref firstBarItem, BitmapFontIcon.DamagePhysical, state.LocalPhysical, true);
        AppendSummary(ref textBuilder, ref firstBarItem, BitmapFontIcon.DamageMagical,  state.LocalMagical,  true);
        AppendSummary(ref textBuilder, ref firstBarItem, BitmapFontIcon.Tank,           state.LocalShield,   false);

        barEntry.Text = textBuilder.Build();

        var tipBuilder   = new SeStringBuilder();
        var firstTipItem = true;

        foreach (var status in state.LocalActiveStatus)
        {
            if (!firstTipItem)
                tipBuilder.Append("\n");
            tipBuilder.Append($"{LuminaWrapper.GetStatusName(status.StatusID)}:");
            tipBuilder.AddIcon(BitmapFontIcon.DamagePhysical);
            tipBuilder.Append($"{status.Physical}% ");
            tipBuilder.AddIcon(BitmapFontIcon.DamageMagical);
            tipBuilder.Append($"{status.Magical}% ");
            firstTipItem = false;
        }

        if (state.LocalShield > 0)
        {
            if (!firstTipItem)
                tipBuilder.Append("\n");
            tipBuilder.AddIcon(BitmapFontIcon.Tank);
            tipBuilder.Append($"{Lang.Get("Shield")}: {state.LocalShield}");
            firstTipItem = false;
        }

        foreach (var status in state.TargetActiveStatus)
        {
            if (!firstTipItem)
                tipBuilder.Append("\n");
            tipBuilder.Append($"{LuminaWrapper.GetStatusName(status.StatusID)}:");
            tipBuilder.AddIcon(BitmapFontIcon.DamagePhysical);
            tipBuilder.Append($"{status.Physical}% ");
            tipBuilder.AddIcon(BitmapFontIcon.DamageMagical);
            tipBuilder.Append($"{status.Magical}% ");
            firstTipItem = false;
        }

        barEntry.Tooltip = tipBuilder.Build();
        barEntry.Shown   = true;

        return;

        void AppendSummary(ref SeStringBuilder builder, ref bool first, BitmapFontIcon icon, float value, bool suffixPercent)
        {
            if (value <= 0)
                return;

            if (!first)
                builder.Append(" ");

            builder.AddIcon(icon);
            builder.Append($"{value:0}" + (suffixPercent ? "%" : ""));
            first = false;
        }
    }
    
    #region 事件

    private void OnZoneChanged(uint u)
    {
        UnregCombatEvents();

        if (GameState.ContentFinderCondition == 0) return;

        RegCombatEvents();
    }

    private void OnConditionChanged(ConditionFlag flag, bool value)
    {
        if (flag != ConditionFlag.InCombat) return;

        if (value)
            RegCombatEvents();
        else
            UnregCombatEvents();
    }

    private void OnUpdate(IFramework _)
    {
        if (GameState.IsInPVPArea)
        {
            UnregCombatEvents();
            return;
        }

        if (!DService.Instance().Condition[ConditionFlag.InCombat] && GameState.ContentFinderCondition == 0)
        {
            UnregCombatEvents();
            return;
        }

        state.Update();
        UpdateStatusBar();
    }

    #endregion

    #region PartyList

    private unsafe void Draw()
    {
        if (Throttler.Shared.Throttle("AutoDisplayMitigationInfo-OnUpdatePartyDrawCondition"))
            isNeedToDrawOnPartyList = PartyList->IsAddonAndNodesReady() && !GameState.IsInPVPArea;

        if (!isNeedToDrawOnPartyList)
            return;

        var drawList = ImGui.GetBackgroundDrawList();
        var addon    = (AddonPartyList*)PartyList;

        var snapshot = state.PartySnapshot;
        var count    = snapshot.Length;

        for (var i = 0; i < count; i++)
        {
            ref var partyMember = ref addon->PartyMembers[i];
            if (partyMember.HPGaugeComponent is null || !partyMember.HPGaugeComponent->OwnerNode->IsVisible())
                continue;

            ref readonly var status = ref snapshot[i];
            DrawMitigationNode(drawList, ref partyMember, status);
            DrawShieldNode(drawList, ref partyMember, status);
        }
    }

    private static unsafe void DrawMitigationNode
    (
        ImDrawListPtr                            drawList,
        ref AddonPartyList.PartyListMemberStruct partyMember,
        in  PartyMitigationSnapshot              status
    )
    {
        var mitigationValue = MathF.Max(status.Physical, status.Magical);
        if (mitigationValue <= 0)
            return;

        var nameNode = partyMember.NameAndBarsContainer;
        if (nameNode is null || !nameNode->IsVisible())
            return;

        var nameTextNode = partyMember.Name;
        if (nameTextNode is null || !nameTextNode->IsVisible())
            return;

        var partyListAddon = (AddonPartyList*)PartyList;
        var partyScale     = partyListAddon->Scale;

        using var fontPush = FontManager.Instance().MiedingerMidFont120.Push();

        var text     = $"{mitigationValue:N0}%";
        var textSize = ImGui.CalcTextSize(text);

        var posX = nameNode->ScreenX + nameNode->GetWidth() * partyScale - textSize.X - 5 * partyScale;
        var posY = nameNode->ScreenY                                     + 2              * partyScale;

        var pos = new Vector2(posX, posY);

        drawList.AddText(pos + new Vector2(1, 1), 0x9D00A2FF, text);
        drawList.AddText(pos,                     0xFFFFFFFF, text);
    }

    private static unsafe void DrawShieldNode
    (
        ImDrawListPtr                            drawList,
        ref AddonPartyList.PartyListMemberStruct partyMember,
        in  PartyMitigationSnapshot              status
    )
    {
        var shieldValue = status.Shield;

        var hpComponent = partyMember.HPGaugeComponent;
        if (hpComponent is null || !hpComponent->OwnerNode->IsVisible())
            return;

        var numNode = hpComponent->GetTextNodeById(2);
        if (numNode is null || !numNode->IsVisible())
            return;

        var partyListAddon = (AddonPartyList*)PartyList;
        var mpNode2        = partyMember.MPGaugeBar->GetTextNodeById(2)->GetAsAtkTextNode();
        var mpNode3        = partyMember.MPGaugeBar->GetTextNodeById(3)->GetAsAtkTextNode();

        if (shieldValue >= 1e5)
        {
            if (mpNode2 is not null && mpNode2->IsVisible())
                mpNode2->SetAlpha(0);
            if (mpNode3 is not null && mpNode3->IsVisible())
                mpNode3->SetAlpha(0);
        }
        else
        {
            if (mpNode2 is not null && mpNode2->IsVisible())
                mpNode2->SetAlpha(255);
            if (mpNode3 is not null && mpNode3->IsVisible())
                mpNode3->SetAlpha(255);
        }

        if (shieldValue <= 0)
            return;

        var partyScale = partyListAddon->Scale;

        using var fontPush = FontManager.Instance().MiedingerMidFont120.Push();

        var text = $"{shieldValue:F0}";

        var posX = numNode->ScreenX                                                    + numNode->GetWidth() * partyListAddon->Scale + 3 * partyScale;
        var posY = numNode->ScreenY + numNode->GetHeight() * partyListAddon->Scale / 2 - 3f                  * partyScale;

        drawList.AddText(new Vector2(posX + 1, posY + 1), 0x9D00A2FF, text);
        drawList.AddText(new Vector2(posX,     posY),     0xFFFFFFFF, text);
    }

    #endregion

    private readonly struct MitigationDefinition
    (
        float physical,
        float magical,
        bool  onMember
    )
    {
        public float Physical { get; } = physical;
        public float Magical  { get; } = magical;
        public bool  OnMember { get; } = onMember;
    }

    private readonly struct ActiveMitigation
    (
        uint  statusID,
        float remainingTime,
        float physical,
        float magical
    )
    {
        public uint  StatusID      { get; } = statusID;
        public float RemainingTime { get; } = remainingTime;
        public float Physical      { get; } = physical;
        public float Magical       { get; } = magical;
    }

    private readonly struct PartyMitigationSnapshot
    (
        uint  entityID,
        float physical,
        float magical,
        float shield
    )
    {
        public uint  EntityID { get; } = entityID;
        public float Physical { get; } = physical;
        public float Magical  { get; } = magical;
        public float Shield   { get; } = shield;
    }

    private sealed unsafe class MitigationState
    {
        private readonly PartyMitigationSnapshot[] partySnapshotBuffer = new PartyMitigationSnapshot[8];
        private          int                       localActiveCount;
        private          ActiveMitigation[]        localActiveStatusBuffer = new ActiveMitigation[16];
        private          int                       partyCount;
        private          int                       targetActiveCount;
        private          ActiveMitigation[]        targetActiveStatusBuffer = new ActiveMitigation[16];

        public float LocalShield { get; private set; }

        public float LocalPhysical { get; private set; }

        public float LocalMagical { get; private set; }

        public bool IsLocalEmpty =>
            localActiveCount == 0 && LocalShield == 0 && targetActiveCount == 0;

        public ReadOnlySpan<ActiveMitigation> LocalActiveStatus =>
            localActiveStatusBuffer.AsSpan(0, localActiveCount);

        public ReadOnlySpan<ActiveMitigation> TargetActiveStatus =>
            targetActiveStatusBuffer.AsSpan(0, targetActiveCount);

        public ReadOnlySpan<PartyMitigationSnapshot> PartySnapshot =>
            partySnapshotBuffer.AsSpan(0, partyCount);

        public void Clear()
        {
            localActiveCount  = 0;
            targetActiveCount = 0;
            partyCount        = 0;
            LocalShield       = 0;
            LocalPhysical     = 0;
            LocalMagical      = 0;
        }

        public void Update()
        {
            var definitions = RemoteRepoManager.GetStatusDefinitions();

            var localPlayer = Control.GetLocalPlayer();

            if (localPlayer == null)
            {
                Clear();
                return;
            }

            LocalShield = (float)localPlayer->ShieldValue / 100 * localPlayer->Health;

            UpdateLocal(localPlayer, definitions);
            UpdateTarget(definitions);
            UpdateLocalSummaryWithTarget();
            UpdateParty(localPlayer, definitions);
        }

        private void UpdateLocal(GameBattleChara* localPlayer, FrozenDictionary<uint, MitigationDefinition> definitions)
        {
            Span<ActiveMitigation> buffer = stackalloc ActiveMitigation[64];
            var                    count  = 0;

            var factorPhysical = 1f;
            var factorMagical  = 1f;

            foreach (var status in localPlayer->StatusManager.Status)
            {
                if (status.StatusId == 0)
                    continue;

                if (!TryGetMitigationValues(localPlayer->EntityId, MemberStatus.From(status), definitions, out var physical, out var magical))
                    continue;

                AddOrUpdateActiveMitigation(buffer, ref count, status.StatusId, status.RemainingTime, physical, magical);

                MultiplyFactors(physical, magical, ref factorPhysical, ref factorMagical);
            }

            LocalPhysical = ReductionFromFactor(factorPhysical);
            LocalMagical  = ReductionFromFactor(factorMagical);

            StoreSnapshot(ref localActiveStatusBuffer, ref localActiveCount, buffer, count);
        }

        private void UpdateTarget(FrozenDictionary<uint, MitigationDefinition> definitions)
        {
            Span<ActiveMitigation> buffer = stackalloc ActiveMitigation[64];
            var                    count  = 0;

            var currentTarget = TargetManager.Target;

            if (currentTarget is IBattleNPC battleNpc)
            {
                var statusList = battleNpc.ToBCStruct()->StatusManager.Status;

                foreach (var status in statusList)
                {
                    if (status.StatusId == 0)
                        continue;

                    if (!definitions.TryGetValue(status.StatusId, out var def))
                        continue;

                    AddOrUpdateActiveMitigation(buffer, ref count, status.StatusId, status.RemainingTime, def.Physical, def.Magical);
                }
            }

            StoreSnapshot(ref targetActiveStatusBuffer, ref targetActiveCount, buffer, count);
        }

        private void UpdateLocalSummaryWithTarget()
        {
            if (targetActiveCount == 0)
                return;

            var factorPhysical = 1f;
            var factorMagical  = 1f;

            for (var i = 0; i < localActiveCount; i++)
            {
                ref readonly var s = ref localActiveStatusBuffer[i];
                MultiplyFactors(s.Physical, s.Magical, ref factorPhysical, ref factorMagical);
            }

            for (var i = 0; i < targetActiveCount; i++)
            {
                ref readonly var s = ref targetActiveStatusBuffer[i];
                MultiplyFactors(s.Physical, s.Magical, ref factorPhysical, ref factorMagical);
            }

            LocalPhysical = ReductionFromFactor(factorPhysical);
            LocalMagical  = ReductionFromFactor(factorMagical);
        }

        private void UpdateParty(GameBattleChara* localPlayer, FrozenDictionary<uint, MitigationDefinition> definitions)
        {
            Span<PartyMitigationSnapshot> buffer = stackalloc PartyMitigationSnapshot[8];
            buffer.Clear();

            buffer[0] = new PartyMitigationSnapshot(localPlayer->EntityId, LocalPhysical, LocalMagical, LocalShield);
            var maxIndex = 1;

            var enemyPhysicalFactor = 1f;
            var enemyMagicalFactor  = 1f;

            for (var i = 0; i < targetActiveCount; i++)
            {
                ref readonly var s = ref targetActiveStatusBuffer[i];
                MultiplyFactors(s.Physical, s.Magical, ref enemyPhysicalFactor, ref enemyMagicalFactor);
            }

            foreach (var member in AgentHUD.Instance()->PartyMembers)
            {
                if (member.Index < 0 || member.Index >= buffer.Length)
                    continue;

                if (member.Index == 0)
                    continue;

                if (member.Index + 1 > maxIndex)
                    maxIndex = member.Index + 1;

                var entityID = member.EntityId;

                if (entityID == 0 || member.Object == null)
                {
                    buffer[member.Index] = new PartyMitigationSnapshot(entityID, 0, 0, 0);
                    continue;
                }

                var factorPhysical = enemyPhysicalFactor;
                var factorMagical  = enemyMagicalFactor;

                foreach (var status in member.Object->StatusManager.Status)
                {
                    if (status.StatusId == 0)
                        continue;

                    if (!TryGetMitigationValues(entityID, MemberStatus.From(status), definitions, out var physical, out var magical))
                        continue;

                    MultiplyFactors(physical, magical, ref factorPhysical, ref factorMagical);
                }

                var physicalReduction = ReductionFromFactor(factorPhysical);
                var magicalReduction  = ReductionFromFactor(factorMagical);
                var shield            = (float)member.Object->ShieldValue / 100 * member.Object->Health;

                buffer[member.Index] = new PartyMitigationSnapshot(entityID, physicalReduction, magicalReduction, shield);
            }

            buffer[..maxIndex].CopyTo(partySnapshotBuffer);
            partyCount = maxIndex;
        }

        private static bool TryGetMitigationValues
            (uint targetID, MemberStatus memberStatus, FrozenDictionary<uint, MitigationDefinition> definitions, out float physical, out float magical)
        {
            physical = 0;
            magical  = 0;

            var statusID = memberStatus.StatusID;

            if (statusID == 2675)
            {
                var value = memberStatus.SourceID == targetID ? 15f : 10f;
                physical = value;
                magical  = value;
                return true;
            }

            if (statusID == 1174)
            {
                if (DService.Instance().ObjectTable.SearchByID(targetID) is not IBattleChara sourceChara)
                    return false;

                var value = 10f;

                foreach (var s in sourceChara.StatusList)
                {
                    if (s.StatusID is 1191 or 3829)
                    {
                        value = 20f;
                        break;
                    }
                }

                physical = value;
                magical  = value;
                return true;
            }

            if (!definitions.TryGetValue(statusID, out var def))
                return false;

            physical = def.Physical;
            magical  = def.Magical;
            return true;
        }

        private static void AddOrUpdateActiveMitigation
            (Span<ActiveMitigation> buffer, ref int count, uint statusID, float remainingTime, float physical, float magical)
        {
            for (var i = 0; i < count; i++)
            {
                if (buffer[i].StatusID != statusID)
                    continue;

                if (remainingTime > buffer[i].RemainingTime)
                    buffer[i] = new ActiveMitigation(statusID, remainingTime, physical, magical);

                return;
            }

            if (count < buffer.Length)
                buffer[count++] = new ActiveMitigation(statusID, remainingTime, physical, magical);
        }

        private static void MultiplyFactors(float physical, float magical, ref float physicalFactor, ref float magicalFactor)
        {
            if (physical > 0)
                physicalFactor *= 1f - physical / 100f;
            if (magical > 0)
                magicalFactor *= 1f - magical / 100f;
        }

        private static float ReductionFromFactor(float factor) =>
            factor >= 1f ? 0f : (1f - factor) * 100f;

        private static void StoreSnapshot(ref ActiveMitigation[] destination, ref int destinationCount, Span<ActiveMitigation> values, int count)
        {
            if (count == 0)
            {
                destinationCount = 0;
                return;
            }

            EnsureCapacity(ref destination, count);
            values[..count].CopyTo(destination);
            destinationCount = count;
        }

        private static void EnsureCapacity(ref ActiveMitigation[] array, int needed)
        {
            if (array.Length >= needed)
                return;

            var newSize = array.Length == 0 ? 8 : array.Length * 2;
            if (newSize < needed)
                newSize = needed;

            array = new ActiveMitigation[newSize];
        }

        private readonly struct MemberStatus
        (
            uint statusID,
            uint sourceID
        )
        {
            public uint StatusID { get; } = statusID;
            public uint SourceID { get; } = sourceID;

            public static MemberStatus From(Status s) =>
                new(s.StatusId, s.SourceObject.ObjectId);
        }
    }

    private static class RemoteRepoManager
    {
        private const string EndpointCN     = "https://haku.diemoe.net/assets/mitigation.json";
        private const string EndpointGlobal = "https://assets.sumemo.dev/mitigation.json";

        private static FrozenDictionary<uint, MitigationDefinition> StatusDefinitions = FrozenDictionary<uint, MitigationDefinition>.Empty;

        public static FrozenDictionary<uint, MitigationDefinition> GetStatusDefinitions() =>
            Volatile.Read(ref StatusDefinitions);

        public static async Task FetchMitigationStatusesAsync(CancellationToken ct)
        {
            try
            {
                var endpoint = GameState.IsCN ? EndpointCN : EndpointGlobal;
                var json     = await HTTPClientHelper.Instance().Get().GetStringAsync(endpoint, ct).ConfigureAwait(false);
                var resp = JsonConvert.DeserializeObject<MitigationInfoDto[]>(json);

                if (resp == null)
                {
                    DLog.Error("[AutoDisplayMitigationInfo] 远程减伤技能文件解析失败");
                    return;
                }

                var builder = new Dictionary<uint, MitigationDefinition>(resp.Length);

                foreach (var item in resp)
                {
                    if (item == null || item.ID == 0)
                        continue;

                    var info = item.Mitigation;
                    builder[item.ID] = new MitigationDefinition(info?.Physical ?? 0, info?.Magical ?? 0, item.OnMember);
                }

                Volatile.Write(ref StatusDefinitions, builder.ToFrozenDictionary());
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                DLog.Error($"[AutoDisplayMitigationInfo] 远程减伤技能文件获取失败: {ex}");
            }
        }
    }

    private class Config : ModuleConfig
    {
        public bool MoveableOverlay   = true;
        public bool ResizeableOverlay = true;
        public bool TransparentOverlay;
    }

    private sealed class MitigationInfoDto
    {
        [JsonProperty("id")]
        public uint ID { get; private set; }

        [JsonProperty("mitigation")]
        public StatusInfoDto? Mitigation { get; private set; }

        [JsonProperty("on_member")]
        public bool OnMember { get; private set; }
    }

    private sealed class StatusInfoDto
    {
        [JsonProperty("physical")]
        public float Physical { get; private set; }

        [JsonProperty("magical")]
        public float Magical { get; private set; }
    }

    #region 常量

    private static byte[] DamagePhysicalString { get; } = new SeString(new IconPayload(BitmapFontIcon.DamagePhysical)).Encode();
    private static byte[] DamageMagicalString  { get; } = new SeString(new IconPayload(BitmapFontIcon.DamageMagical)).Encode();

    #endregion
}
