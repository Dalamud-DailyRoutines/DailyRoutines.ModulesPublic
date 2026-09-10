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

namespace DailyRoutines.ModulesPublic.Interface;

public unsafe class OptimizedXBMMonsterNotebook : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("OptimizedXBMMonsterNotebookTitle"),
        Description = Lang.Get("OptimizedXBMMonsterNotebookDescription"),
        Category    = ModuleCategory.Interface
    };

    public override ModulePermission Permission { get; } = new() { AllDefaultEnabled = true };

    private static readonly CompSig BuildNotebookDetailSig =
        new("40 53 56 57 41 56 48 81 EC ?? ?? ?? ?? 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 84 24 ?? ?? ?? ?? 48 8B DA");
    private delegate void BuildNotebookDetailDelegate
    (
        void*     agent,
        AtkValue* values
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
        void*     agent,
        AtkValue* values
    )
    {
        var petID = GetSelectedXBMPetID();
        if (petID is < MIN_PET_ID or > MAX_PET_ID)
        {
            BuildNotebookDetailHook.Original(agent, values);
            return;
        }

        var index    = (petID - 1) >> 3;
        var mask     = (byte)(1 << ((int)(petID - 1) & 7));
        var unlocked = (byte*)((nint)agent + AGENT_UNLOCKED_PETS_OFFSET);
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
        var agentModule = AgentModule.Instance();
        if (agentModule == null) return;

        var agent = (nint)agentModule->GetAgentByInternalId(AgentId.XBMMonsterNotebook);
        if (agent == nint.Zero) return;

        *(byte*)(agent + AGENT_LIST_DIRTY_OFFSET)   = 1;
        *(byte*)(agent + AGENT_DETAIL_DIRTY_OFFSET) = 1;
    }

    private static uint GetSelectedXBMPetID()
    {
        var agentModule = AgentModule.Instance();
        if (agentModule == null) return 0;

        var agent = (nint)agentModule->GetAgentByInternalId(AgentId.XBMMonsterNotebook);
        if (agent == nint.Zero) return 0;
        
       return *(uint*)(agent + AGENT_SELECTED_PET_ID_OFFSET);
    }

    #region 常量

    // TODO：等待 FFCS 有 AgentXBMMonsterNotebook 了我再提 PR

    // AgentXBMMonsterNotebook: 已解锁魔兽位域, 内容与服务端下发的 XBMManager.UnlockedPets 一致
    private const int AGENT_UNLOCKED_PETS_OFFSET = 0x70;

    // AgentXBMMonsterNotebook: 当前选中的魔兽 ID
    private const int AGENT_SELECTED_PET_ID_OFFSET = 0x9C;

    // AgentXBMMonsterNotebook: 列表 / 详情重建标记
    private const int AGENT_LIST_DIRTY_OFFSET   = 0x5E;
    private const int AGENT_DETAIL_DIRTY_OFFSET = 0x5F;

    private const uint MIN_PET_ID = 1;
    private const uint MAX_PET_ID = 50;

    #endregion
}
