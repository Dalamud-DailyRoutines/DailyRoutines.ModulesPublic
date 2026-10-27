using DailyRoutines.Common.Info;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.Nodes;

namespace DailyRoutines.ModulesPublic;

public unsafe class AutoDisplayRelicGrowthProgress : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoDisplayRelicGrowthProgressTitle"),
        Description = Lang.Get("AutoDisplayRelicGrowthProgressDescription"),
        Category    = ModuleCategory.Interface,
        PreviewImageURL =
        [
            "https://gh.atmoomen.top/raw.githubusercontent.com/Dalamud-DailyRoutines/DailyRoutines/main/Resources/Modules/AutoDisplayRelicGrowthProgress/preview-1.webp"
        ]
    };

    public override ModulePermission Permission { get; } = new()
    {
        AllDefaultEnabled = true
    };

    private TextNode? progressLabelNode;
    private TextNode? progressNode;

    protected override void Init()
    {
        IAddonLifecycle.Instance().RegisterListener(AddonEvent.PostDraw,    "Relic2Glass", OnAddon);
        IAddonLifecycle.Instance().RegisterListener(AddonEvent.PreFinalize, "Relic2Glass", OnAddon);
    }

    protected override void Uninit()
    {
        IAddonLifecycle.Instance().UnregisterListener(OnAddon);

        progressNode?.Dispose();
        progressNode = null;

        progressLabelNode?.Dispose();
        progressLabelNode = null;
    }

    private void OnAddon
    (
        AddonEvent type,
        AddonArgs  args
    )
    {
        switch (type)
        {
            case AddonEvent.PreFinalize:
                progressNode      = null;
                progressLabelNode = null;
                break;

            case AddonEvent.PostDraw:
                if (Relic2Glass == null)
                    return;
                if (progressNode != null)
                    return;

                var componentNode = Relic2Glass->GetComponentNodeById(6);
                if (componentNode == null)
                    return;

                var progress = Relic2Glass->AtkValues[1].UInt;

                progressNode = new()
                {
                    FontType      = FontType.Miedinger,
                    TextFlags     = TextFlags.Edge | TextFlags.Bold,
                    CharSpacing   = 6,
                    AlignmentType = AlignmentType.Center,
                    String        = $"{progress}/2000",
                    FontSize      = 16,
                    Width         = 380,
                    Height        = 32,
                    Y             = -20
                };
                AtkColors.ValueEmphasize.ApplyTo(progressNode);
                progressNode.AttachNode(componentNode);

                progressLabelNode = new()
                {
                    TextFlags     = TextFlags.Edge,
                    AlignmentType = AlignmentType.Left,
                    String        = Lang.Get("AutoDisplayRelicGrowthProgress-Progress"),
                    FontSize      = 12,
                    Width         = 380,
                    Height        = 32,
                    Y             = -40
                };
                AtkColors.ListHeader.ApplyTo(progressLabelNode);
                progressLabelNode.AttachNode(componentNode);

                break;
        }
    }
}
