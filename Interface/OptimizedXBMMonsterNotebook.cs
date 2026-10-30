using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.Nodes;
using Lumina.Excel.Sheets;
using OmenTools.Interop.Game.Lumina;
using OmenTools.Interop.Game.Models;
using OmenTools.OmenService;

namespace DailyRoutines.ModulesPublic;

public unsafe class OptimizedXBMMonsterNotebook : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("OptimizedXBMMonsterNotebookTitle"),
        Description = Lang.Get("OptimizedXBMMonsterNotebookDescription"),
        Category    = ModuleCategory.Interface
    };

    public override ModulePermission Permission { get; } = new()
    {
        AllDefaultEnabled = true
    };

    private static readonly CompSig BuildNotebookDetailSig =
        new("40 53 56 57 41 56 48 81 EC ?? ?? ?? ?? 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 84 24 ?? ?? ?? ?? 48 8B DA");
    private delegate void BuildNotebookDetailDelegate
    (
        AgentXBMMonsterNotebook* agent,
        AtkValue*                values
    );
    private Hook<BuildNotebookDetailDelegate>? BuildNotebookDetailHook;

    private TextButtonNode? smallButton;
    private TextButtonNode? mediumButton;
    private TextButtonNode? largeButton;

    protected override void Init()
    {
        BuildNotebookDetailHook = BuildNotebookDetailSig.GetHook<BuildNotebookDetailDelegate>(BuildNotebookDetailDetour);
        BuildNotebookDetailHook.Enable();

        if (XBMMonsterBookDetail->IsAddonAndNodesReady())
            MarkAddonDirty();

        IAddonLifecycle.Instance().RegisterListener(AddonEvent.PostDraw,    "XBMMonsterBookDetail", OnAddon);
        IAddonLifecycle.Instance().RegisterListener(AddonEvent.PreFinalize, "XBMMonsterBookDetail", OnAddon);
    }

    protected override void Uninit()
    {
        IAddonLifecycle.Instance().UnregisterListener(OnAddon);

        smallButton?.Dispose();
        smallButton = null;

        mediumButton?.Dispose();
        mediumButton = null;

        largeButton?.Dispose();
        largeButton = null;
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
                smallButton  = null;
                mediumButton = null;
                largeButton  = null;
                break;

            case AddonEvent.PostDraw:
                if (XBMMonsterBookDetail == null) return;

                if (smallButton == null)
                {
                    smallButton = new()
                    {
                        Size     = new(20),
                        Position = new(0),
                        OnClick = () =>
                        {

                            var petID = GetSelectedXBMPetID();
                            if (!LuminaGetter.TryGetRow(petID, out XBMPet xbmPetRow)) return;

                            ChatManager.Instance().SendCommand($"/beastsize {LuminaWrapper.GetPetName((uint)xbmPetRow.Unknown4)} small");
                            MarkAddonDirty();
                        }
                    };
                    smallButton.BackgroundNode.IsVisible = false;

                    smallButton.AttachNode(XBMMonsterBookDetail->GetNodeById(15));
                }

                if (mediumButton == null)
                {
                    mediumButton = new()
                    {
                        Size     = new(20),
                        Position = new(22, 0),
                        OnClick = () =>
                        {

                            var petID = GetSelectedXBMPetID();
                            if (!LuminaGetter.TryGetRow(petID, out XBMPet xbmPetRow)) return;

                            ChatManager.Instance().SendCommand($"/beastsize {LuminaWrapper.GetPetName((uint)xbmPetRow.Unknown4)} medium");
                            MarkAddonDirty();
                        }
                    };
                    mediumButton.BackgroundNode.IsVisible = false;

                    mediumButton.AttachNode(XBMMonsterBookDetail->GetNodeById(15));
                }

                if (largeButton == null)
                {
                    largeButton = new()
                    {
                        Size     = new(20),
                        Position = new(44, 0),
                        OnClick = () =>
                        {

                            var petID = GetSelectedXBMPetID();
                            if (!LuminaGetter.TryGetRow(petID, out XBMPet xbmPetRow)) return;

                            ChatManager.Instance().SendCommand($"/beastsize {LuminaWrapper.GetPetName((uint)xbmPetRow.Unknown4)} large");
                            MarkAddonDirty();
                        }
                    };
                    largeButton.BackgroundNode.IsVisible = false;

                    largeButton.AttachNode(XBMMonsterBookDetail->GetNodeById(15));
                }

                break;
        }
    }

    private void BuildNotebookDetailDetour
    (
        AgentXBMMonsterNotebook* agent,
        AtkValue*                values
    )
    {
        var petID = GetSelectedXBMPetID();

        if (petID is < MIN_PET_ID or > MAX_PET_ID)
        {
            BuildNotebookDetailHook.Original(agent, values);
            return;
        }

        var index    = (int)((petID - 1) >> 3);
        var mask     = (byte)(1          << ((int)(petID - 1) & 7));
        var unlocked = agent->UnlockedPets;
        var previous = unlocked[index];

        // 左侧列表在同一次 Update 中先于此构建，因此不会影响左侧仍为问号
        unlocked[index] = (byte)(previous | mask);

        try
        {
            BuildNotebookDetailHook.Original(agent, values);
        }
        finally
        {
            unlocked[index] = previous;
        }
    }

    private static void MarkAddonDirty()
    {
        var agent = AgentXBMMonsterNotebook.Instance();
        if (agent == null) return;

        agent->RequestNotebookRebuild       = true;
        agent->RequestNotebookDetailRebuild = true;
    }

    private static uint GetSelectedXBMPetID()
    {
        var agent = AgentXBMMonsterNotebook.Instance();
        if (agent == null) return 0;

        return agent->HoveredPetId;
    }

    #region 常量

    private const uint MIN_PET_ID = 1;
    private const uint MAX_PET_ID = 50;

    #endregion
}
