using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using FFXIVClientStructs.Expansion.FFXIV.Client.UI;
using FFXIVClientStructs.Expansion.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.BaseTypes;
using KamiToolKit.Nodes;
using OmenTools.Interop.Game.Lumina;

namespace DailyRoutines.ModulesPublic;

public unsafe class OptimizedKTGHudGauge : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("OptimizedKTGHudGaugeTitle"),
        Description = Lang.Get("OptimizedKTGHudGaugeDescription"),
        Category    = ModuleCategory.GoldSaucer
    };

    public override ModulePermission Permission { get; } = new()
    {
        AllDefaultEnabled = true
    };

    private VerticalListNode? infoNode;

    private StatsRow? pointsStats;
    private StatsRow? mgpStats;
    private StatsRow? treasureStats;
    private StatsRow? achievementStats;

    private TextNode? bonusTimeText;

    protected override void Init()
    {
        IAddonLifecycle.Instance().RegisterListener(AddonEvent.PostRefresh, "KTGHudScore", OnScoreAddon);
        IAddonLifecycle.Instance().RegisterListener(AddonEvent.PreFinalize, "KTGHudScore", OnScoreAddon);
        if (KTGHudScore->IsAddonAndNodesReady())
            OnScoreAddon(AddonEvent.PostRefresh, null);

        AchievementManager.Instance().AchievementReceived += OnAchievementReceived;
    }

    protected override void Uninit()
    {
        AchievementManager.Instance().AchievementReceived -= OnAchievementReceived;

        IAddonLifecycle.Instance().UnregisterListener(OnScoreAddon);

        OnScoreAddon(AddonEvent.PreFinalize, null);
    }

    private void OnScoreAddon
    (
        AddonEvent type,
        AddonArgs? args
    )
    {
        var addon = (AddonKTGHudScore*)(args?.Addon.Address ?? (nint)KTGHudScore);

        switch (type)
        {
            case AddonEvent.PreFinalize:
                bonusTimeText?.Dispose();
                bonusTimeText = null;

                infoNode?.Dispose();
                infoNode = null;

                pointsStats      = null;
                mgpStats         = null;
                achievementStats = null;
                treasureStats    = null;
                break;

            case AddonEvent.PostRefresh:
                if (!addon->AtkUnitBase.IsAddonAndNodesReady())
                    return;

                if (infoNode != null)
                {
                    UpdateBonusTime();
                    UpdateStats();
                    return;
                }

                var source = addon->BonusTextNode;
                var gauge  = addon->ProgressGaugeBar->OwnerNode;
                bonusTimeText = new()
                {
                    Y                = gauge->Y + gauge->Height - 10f,
                    Size             = new(addon->RootNode->Width, 24),
                    FontType         = FontType.Axis,
                    FontSize         = 16,
                    TextFlags        = source->TextFlags,
                    TextColor        = source->TextColor.ToVector4(),
                    TextOutlineColor = source->EdgeColor.ToVector4(),
                    AlignmentType    = AlignmentType.Right
                };
                bonusTimeText.AttachNode((AtkUnitBase*)addon);
                UpdateBonusTime();

                infoNode = new VerticalListNode
                {
                    Y           = bonusTimeText.Y + bonusTimeText.Height + 4f,
                    Width       = bonusTimeText.Width,
                    ItemSpacing = 6f,
                    FitContents = true
                };

                pointsStats = new
                (
                    source,
                    infoNode.Width,
                    LuminaWrapper.GetItemIconID(POINTS_ITEM_ID),
                    LuminaWrapper.GetItemName(POINTS_ITEM_ID)
                );
                mgpStats = new
                (
                    source,
                    infoNode.Width,
                    LuminaWrapper.GetItemIconID(MGP_ITEM_ID),
                    LuminaWrapper.GetItemName(MGP_ITEM_ID)
                );
                treasureStats = new
                (
                    source,
                    infoNode.Width,
                    LuminaWrapper.GetItemIconID(TREASURE_ITEM_ID),
                    LuminaWrapper.GetItemName(TREASURE_ITEM_ID),
                    true
                );
                achievementStats = new
                (
                    source,
                    infoNode.Width,
                    LuminaWrapper.GetAchievementIconID(ACHIEVEMENT_ID),
                    LuminaWrapper.GetAchievementName(ACHIEVEMENT_ID),
                    true
                );

                infoNode.AddNode([pointsStats, mgpStats, treasureStats, achievementStats]);

                UpdateStats();

                infoNode.AttachNode((AtkUnitBase*)addon);
                break;
        }
    }

    private void OnAchievementReceived
    (
        AchievementInfo info
    )
    {
        if (info.ID != ACHIEVEMENT_ID)
            return;

        UpdateStats();
    }

    private void UpdateStats()
    {
        if (infoNode == null)
            return;

        pointsStats.StatsTextNode.String =
            $"{LocalPlayerState.GetItemCount(POINTS_ITEM_ID).ToChineseString()}/{10000.ToChineseString()}";

        mgpStats?.StatsTextNode.String =
            $"{LocalPlayerState.GetItemCount(MGP_ITEM_ID).ToChineseString()}/{9999999.ToChineseString()}";

        treasureStats?.StatsTextNode.String =
            $"{LocalPlayerState.GetItemCount(TREASURE_ITEM_ID).ToChineseString()}";

        if (AchievementManager.Instance().TryGetAchievement(ACHIEVEMENT_ID, out var achievementInfo))
        {
            achievementStats?.StatsTextNode.String =
                $"{achievementInfo.Current}/{achievementInfo.Max}";
        }

        infoNode.RecalculateLayout();
    }

    private void UpdateBonusTime()
    {
        if (bonusTimeText == null)
            return;

        var hud = AgentKTGHud.Instance();
        if (hud == null)
            return;

        bonusTimeText.IsVisible = hud->ScoreBonusTime > 0;
        bonusTimeText.String = Lang.Get
        (
            "OptimizedKTGHudGauge-BonusLeftTimeFormat",
            new Dictionary<string, object>
            {
                ["seconds"] = MathF.Round(hud->ScoreBonusTime / 1000f, 1)
            }
        );
    }

    private class StatsRow : VerticalListNode
    {
        public TextNode StatsTextNode { get; init; }

        public StatsRow
        (
            AtkTextNode* source,
            float        width,
            uint         iconID,
            string       iconName,
            bool         useFrame = false
        )
        {
            Height = 28f + 20f;
            Width  = width;

            var firstRow = new HorizontalListNode
            {
                ItemSpacing = 4f,
                Height      = 20f,
                Width       = width
            };

            NodeBase iconNode;

            if (useFrame)
            {
                iconNode = new IconNode
                {
                    Size   = new(firstRow.Height),
                    IconId = iconID
                };
            }
            else
            {
                iconNode = new IconImageNode
                {
                    Size       = new(firstRow.Height),
                    IconId     = iconID,
                    FitTexture = true
                };
            }

            var iconTextNode = new TextNode
            {
                Height           = firstRow.Height,
                Width            = width - firstRow.ItemSpacing - iconNode.Width,
                FontType         = FontType.Axis,
                FontSize         = 12,
                TextFlags        = source->TextFlags,
                TextColor        = source->TextColor.ToVector4(),
                TextOutlineColor = source->EdgeColor.ToVector4(),
                AlignmentType    = AlignmentType.Right,
                String           = iconName
            };

            StatsTextNode = new()
            {
                Height           = 28f,
                Width            = width,
                FontType         = FontType.Axis,
                FontSize         = 18,
                TextFlags        = source->TextFlags,
                TextColor        = source->TextColor.ToVector4(),
                TextOutlineColor = source->EdgeColor.ToVector4(),
                AlignmentType    = AlignmentType.Right
            };

            firstRow.AddNode([iconTextNode, iconNode]);
            AddNode([firstRow, StatsTextNode]);
        }
    }

    #region 常量

    private const uint POINTS_ITEM_ID   = 52322;
    private const uint MGP_ITEM_ID      = 29;
    private const uint TREASURE_ITEM_ID = 52450;
    private const uint ACHIEVEMENT_ID   = 4073;

    #endregion
}
