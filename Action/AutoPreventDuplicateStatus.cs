using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using DailyRoutines.Abstracts;
using DailyRoutines.Infos;
using DailyRoutines.Managers;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Utility.Raii;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using Lumina.Excel.Sheets;

namespace DailyRoutines.Modules;

public unsafe class AutoPreventDuplicateStatus : DailyModuleBase
{
    public override ModuleInfo Info => new()
    {
        Title = GetLoc("AutoPreventDuplicateStatusTitle"),
        Description = GetLoc("AutoPreventDuplicateStatusDescription"),
        Category = ModuleCategories.Action,
    };

    private static readonly Dictionary<DetectType, string> DetectTypeLoc = new()
    {
        { DetectType.Self, GetLoc("AutoPreventDuplicateStatus-Self") },
        { DetectType.Member, GetLoc("AutoPreventDuplicateStatus-Member") },
        { DetectType.Target, GetLoc("Target") },
    };

    private static readonly Dictionary<uint, DuplicatePreventInfo> DuplicateActions = new()
    {
        // 牵制
        { 7549, new([new(1195, DetectType.Target)]) },
        // 昏乱
        { 7560, new([new(1203, DetectType.Target)]) },
        // 抗死
        { 25857, new([new(2707, DetectType.Self)]) },
        // 武装解除
        { 2887, new([new(860, DetectType.Target)]) },
        // 策动，防守之桑巴, 行吟
        { 16889, new([new(1951, DetectType.Self), new(1934, DetectType.Self), new(1826, DetectType.Self)]) },
        { 16012, new([new(1951, DetectType.Self), new(1934, DetectType.Self), new(1826, DetectType.Self)]) },
        { 7405, new([new(1951, DetectType.Self), new(1934, DetectType.Self), new(1826, DetectType.Self)]) },
        // 大地神的抒情恋歌
        { 7408, new([new(1202, DetectType.Self)]) },
        // 雪仇
        { 7535, new([new(1193, DetectType.Target)]) },
        // 摆脱
        { 7388, new([new(1457, DetectType.Self)]) },
        // 圣光幕帘
        { 3540, new([new(1362, DetectType.Self)]) },
        // 干预
        { 7382, new([new(1174, DetectType.Target)]) },
        // 献奉
        { 25754, new([new(2682, DetectType.Target)]) },
        // 至黑之夜
        { 7393, new([new(1178, DetectType.Target)]) },
        // 光之心
        { 16160, new([new(1839, DetectType.Self)]) },
        // 刚玉之心
        { 25758, new([new(2683, DetectType.Target)]) },
        // 极光
        { 16151, new([new(1835, DetectType.Target)]) },
        // 神祝祷
        { 7432, new([new(1218, DetectType.Target)]) },
        // 水流幕
        { 25861, new([new(2708, DetectType.Target)]) },
        // 无中生有
        { 7430, new([new(1217, DetectType.Self)]) },
        // 擢升
        { 25873, new([new(2717, DetectType.Target)]) },
        // 野战治疗阵
        { 188, new([new(1944, DetectType.Self)]) },
        // 扫腿，下踢，盾牌猛击
        { 7863, new([new(2, DetectType.Target)]) },
        { 7540, new([new(2, DetectType.Target)]) },
        { 16, new([new(2, DetectType.Target)]) },
        { 29064, new([new(1343, DetectType.Target), new(3054, DetectType.Target), new(3248, DetectType.Target)]) },
        // 真北
        { 7546, new([new(1250, DetectType.Self)]) },
        // 亲疏自行 (战士)
        { 7548, new([new(2663, DetectType.Self)]) },
        // 战斗连祷
        { 3557, new([new(786, DetectType.Self)]) },
        // 龙剑
        { 83, new([new(116, DetectType.Self)]) },
        // 震脚
        { 69, new([new(110, DetectType.Self)]) },
        // 义结金兰
        { 7396, new([new(1182, DetectType.Self), new(1185, DetectType.Self)]) },
        // 夺取
        { 2248, new([new(638, DetectType.Target)]) },
        // 明镜止水
        { 7499, new([new(1233, DetectType.Self)]) },
        // 三连咏唱
        { 7421, new([new(1211, DetectType.Self)]) },
        // 促进
        { 7518, new([new(1238, DetectType.Self)]) },
        // 灼热之光
        { 25801, new([new(2703, DetectType.Self)]) },
        // 能量吸收
        { 16508, new([new(304, DetectType.Self)]) },
        // 能量抽取
        { 16510, new([new(304, DetectType.Self)]) },
        // 整备
        { 2876, new([new(851, DetectType.Self)]) },
        // 必灭之炎
        { 34579, new([new(3643, DetectType.Target)]) },
        // 魔法吐息
        { 34567, new([new(3712, DetectType.Target)]) },
        // 战斗之声
        { 118, new([new(141, DetectType.Self)]) },
        // 连环计
        { 7436, new([new(1221, DetectType.Target)]) },
        // 占卜
        { 16552, new([new(1878, DetectType.Self)]) },
        // 光速
        { 3606, new([new(841, DetectType.Self)]) },
        // 复活，复生，生辰，复苏，赤复活，天使低语
        { 125, new([new(148, DetectType.Target)]) },
        { 173, new([new(148, DetectType.Target)]) },
        { 3603, new([new(148, DetectType.Target)]) },
        { 24287, new([new(148, DetectType.Target)]) },
        { 7523, new([new(148, DetectType.Target)]) },
        { 18317, new([new(148, DetectType.Target)]) },
        // 冲刺
        { 29057, new([new(1342, DetectType.Self)]) },
        // 展开战术 (反转)
        { 29234, new([new(3087, DetectType.Target, true)], [new(3089, DetectType.Target)]) },
        { 3585, new([new(297, DetectType.Target, true)]) },
        // 魔弹射手
        { 29415, new([new(3054, DetectType.Target), new(1302, DetectType.Target), new(3039, DetectType.Target)]) },
        // 自然的奇迹
        { 29228, new([new(3054, DetectType.Target)]) },
        // 献身
        { 29081, new([new(3054, DetectType.Target)]) },
        // 均衡
        { 29258, new([new(3107, DetectType.Self)]) },
        // 心关
        { 29264, new([new(2872, DetectType.Target)]) },
        // 默者的夜曲
        { 29395, new([new(3054, DetectType.Target), new(3248, DetectType.Target)]) },
        // 星遁天诛
        { 29515, new([new(1302, DetectType.Target), new(3039, DetectType.Target)]) },
    };

    private static readonly Throttler<uint> NotificationThrottler = new();
    private static Config ModuleConfig = null!;

    public override void Init()
    {
        ModuleConfig = LoadConfig<Config>() ?? new();

        DuplicateActions.Keys.Except(ModuleConfig.EnabledActions.Keys).ToList()
                        .ForEach(key => ModuleConfig.EnabledActions[key] = true);

        ModuleConfig.EnabledActions.Keys.Except(DuplicateActions.Keys).ToList()
                    .ForEach(key => ModuleConfig.EnabledActions.Remove(key));

        SaveConfig(ModuleConfig);

        UseActionManager.Register(OnPreUseAction);
    }

    public override void ConfigUI()
    {
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(LightSkyBlue, $"{GetLoc("AutoPreventDuplicateStatus-OverlapThreshold")}:");

        ImGui.SameLine();
        ImGui.SetNextItemWidth(150f * GlobalFontScale);
        ImGui.InputFloat("###OverlapThreshold", ref ModuleConfig.OverlapThreshold, 0, 0, "%.1f");
        if (ImGui.IsItemDeactivatedAfterEdit())
            SaveConfig(ModuleConfig);
        
        ImGuiOm.HelpMarker(GetLoc("AutoPreventDuplicateStatus-OverlapThresholdHelp"));

        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(LightSkyBlue, $"{GetLoc("SendNotification")}:");

        ImGui.SameLine();
        if (ImGui.Checkbox("###SendNotification", ref ModuleConfig.SendNotification))
            SaveConfig(ModuleConfig);

        ImGui.Spacing();

        using (var node = ImRaii.TreeNode($"{GetLoc("Action")}: {ModuleConfig.EnabledActions.Count(x => x.Value)} / {ModuleConfig.EnabledActions.Count}"))
        {
            if (node)
            {
                var tableSize = new Vector2(ImGui.GetContentRegionAvail().X - ImGui.GetTextLineHeightWithSpacing(), 0);
                using var table = ImRaii.Table("###ActionTable", 3, ImGuiTableFlags.Borders, tableSize);
                if (!table) return;

                ImGui.TableSetupColumn("名称",   ImGuiTableColumnFlags.WidthStretch, 30);
                ImGui.TableSetupColumn("一层状态", ImGuiTableColumnFlags.WidthStretch, 30);
                ImGui.TableSetupColumn("二层状态", ImGuiTableColumnFlags.WidthStretch, 30);

                ImGui.TableNextRow(ImGuiTableRowFlags.Headers);

                ImGui.TableNextColumn();
                ImGui.Text(GetLoc("Action"));

                ImGui.TableNextColumn();
                ImGui.Text(GetLoc("AutoPreventDuplicateStatus-RelatedStatus"));

                ImGui.TableNextColumn();
                ImGui.Text($"{GetLoc("AutoPreventDuplicateStatus-RelatedStatus")} 2");

                foreach (var actionInfo in DuplicateActions)
                {
                    if (!LuminaCache.TryGetRow<Action>(actionInfo.Key, out var result)) continue;
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    var isActionEnabled = ModuleConfig.EnabledActions[actionInfo.Key];
                    if (ImGui.Checkbox($"###Is{actionInfo.Key}Enabled", ref isActionEnabled))
                    {
                        ModuleConfig.EnabledActions[actionInfo.Key] ^= true;
                        SaveConfig(ModuleConfig);
                    }

                    ImGui.SameLine();
                    ImGui.Spacing();

                    ImGui.SameLine();
                    ImGuiOm.TextImage(result.Name.ExtractText(), ImageHelper.GetIcon(result.Icon).ImGuiHandle,
                                      ScaledVector2(20f));
                    
                    ImGui.TableNextColumn();
                    ImGui.Spacing();
                    foreach (var status in actionInfo.Value.Statuses)
                        DrawDuplicateStatus(status);

                    ImGui.TableNextColumn();
                    if (actionInfo.Value.SecondStatuses != null)
                    {
                        ImGui.Spacing();
                        foreach (var status in actionInfo.Value.SecondStatuses)
                            DrawDuplicateStatus(status);
                    }
                }
            }
        }
    }

    private static void DrawDuplicateStatus(DuplicateStatusInfo status)
    {
        var statusIcon = status.GetIcon();
        if (statusIcon == null) return;

        ImGui.SameLine();
        ImGui.Image(statusIcon.ImGuiHandle, new(ImGui.GetTextLineHeightWithSpacing()));

        ImGuiOm.TooltipHover($"{status.GetName()}\n" +
                             $"{GetLoc("AutoPreventDuplicateStatus-DetectType")}: {DetectTypeLoc[status.DetectType]}");
    }

    private static void OnPreUseAction(
        ref bool isPrevented,
        ref ActionType actionType, ref uint actionID, ref ulong targetID, ref uint extraParam,
        ref ActionManager.UseActionMode queueState, ref uint comboRouteID, ref bool* outOptAreaTargeted)
    {
        if (actionType != ActionType.Action) return;

        var adjustedActionID = ActionManager.Instance()->GetAdjustedActionId(actionID);
        if (!DuplicateActions.TryGetValue(adjustedActionID, out var info) ||
            !ModuleConfig.EnabledActions.TryGetValue(adjustedActionID, out var enableState) || !enableState)
            return;

        if (ActionManager.Instance()->GetActionStatus(actionType, adjustedActionID) != 0) return;

        var actionData = LuminaCache.GetRow<Action>(adjustedActionID);
        if (actionData == null) return;

        var canTargetSelf = actionData.Value.CanTargetSelf;
        // 雪仇
        if (adjustedActionID == 7535) canTargetSelf = false;

        var gameObj = GameObjectManager.Instance()->Objects.GetObjectByGameObjectId(targetID);

        var targetIDDetection = targetID;
        if (canTargetSelf && !ActionManager.CanUseActionOnTarget(adjustedActionID, gameObj))
            targetIDDetection = DService.ClientState.LocalPlayer.EntityId;

        if (info.ShouldPrevent(targetIDDetection))
        {
            if (ModuleConfig.SendNotification && NotificationThrottler.Throttle(adjustedActionID, 1_000))
                NotificationInfo(GetLoc("AutoPreventDuplicateStatus-PreventedNotification",
                                                     actionData.Value.Name.ExtractText(), adjustedActionID));

            isPrevented = true;
        }
    }

    public override void Uninit()
    {
        UseActionManager.Unregister(OnPreUseAction);
        NotificationThrottler.Clear();
    }

    private enum DetectType
    {
        Self = 0,
        Member = 1,
        Target = 2,
    }

    private sealed record DuplicatePreventInfo(DuplicateStatusInfo[] Statuses, DuplicateStatusInfo[]? SecondStatuses = null)
    {
        public bool ShouldPrevent(ulong gameObjectID)
        {
            if (SecondStatuses != null)
            {
                foreach (var secondInfo in SecondStatuses)
                {
                    if (!secondInfo.isReverse)
                    {
                        if (secondInfo.HasStatus())
                            return false;
                    }
                    else
                    {
                        if (!secondInfo.HasStatus())
                            return false;
                    }
                }
            }

            foreach (var firstInfo in Statuses)
            {
                if (!firstInfo.isReverse)
                {
                    if (firstInfo.HasStatus(gameObjectID))
                        return true;
                }
                else
                {
                    if (!firstInfo.HasStatus(gameObjectID))
                        return true;
                }
            }

            return false;
        }
        
    }

    private sealed record DuplicateStatusInfo(uint StatusID, DetectType DetectType, bool isReverse = false)
    {
        private bool IsPermanent => 
            PresetData.Statuses.TryGetValue(StatusID, out var statusInfo) && statusInfo.IsPermanent;

        public IDalamudTextureWrap? GetIcon() => !PresetData.Statuses.TryGetValue(StatusID, out var rowData) ? null : DService.Texture.GetFromGameIcon(new(rowData.Icon)).GetWrapOrDefault();

        public string? GetName() => !PresetData.Statuses.TryGetValue(StatusID, out var rowData) ? null : rowData.Name.ExtractText();

        public bool HasStatus()
        {
            switch (DetectType)
            {
                case DetectType.Self:
                    return HasStatus(&Control.GetLocalPlayer()->StatusManager);
                case DetectType.Target:
                    var target = DService.Targets.Target;
                    if (target == null) return false;
                    return HasStatus(&target.ToBCStruct()->StatusManager);
                case DetectType.Member:
                    if (DService.PartyList.Length <= 0) return false;
                    foreach (var partyMember in DService.PartyList)
                    {
                        var pStruct = partyMember.ToStruct();
                        if (pStruct == null) continue;
                        var state = HasStatus(&pStruct->StatusManager);
                        if (state) return true;
                    }
                    return false;
                default:
                    return false;
            }
        }

        public bool HasStatus(ulong gameObjectID)
        {
            var localPlayer = DService.ClientState.LocalPlayer;
            if (localPlayer == null) return false;
            var localPlayerGameObjectID = localPlayer.GameObjectId;

            var battleChara = DetectType == DetectType.Self || gameObjectID == 0xE0000000 || gameObjectID == localPlayerGameObjectID
                                  ? Control.GetLocalPlayer()
                                  : (BattleChara*)GameObjectManager.Instance()->Objects.GetObjectByGameObjectId(gameObjectID);
            if (battleChara == null) return false;
            return HasStatus(&battleChara->StatusManager);
        }

        public bool HasStatus(StatusManager* statusManager)
        {
            if (statusManager == null) return false;

            var statusIndex = statusManager->GetStatusIndex(StatusID);
            if (statusIndex == -1) return false;

            return IsPermanent || statusManager->Status[statusIndex].RemainingTime > ModuleConfig.OverlapThreshold;
        }
    }

    private class Config : ModuleConfiguration
    {
        public Dictionary<uint, bool> EnabledActions = [];
        public float OverlapThreshold = 3.5f;
        public bool SendNotification = true;
    }
}
