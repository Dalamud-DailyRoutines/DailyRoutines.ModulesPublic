using System.Collections.Frozen;
using System.Numerics;
using System.Text;
using DailyRoutines.Common.Extensions;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using DailyRoutines.Internal;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit;
using KamiToolKit.Classes;
using KamiToolKit.Nodes;
using KamiToolKit.Premade.Node.Simple;
using KamiToolKit.Timelines;
using Lumina.Excel.Sheets;
using OmenTools.Dalamud;
using OmenTools.Dalamud.Abstractions;
using OmenTools.Dalamud.Attributes;
using OmenTools.Dalamud.Helpers;
using OmenTools.Info.Game.ItemSource;
using OmenTools.Info.Game.ItemSource.Enums;
using OmenTools.Interop.Game;
using OmenTools.Interop.Game.Lumina;
using OmenTools.Interop.Game.Models;
using OmenTools.OmenService;
using OmenTools.Threading.TaskHelper;
using DetailKind = FFXIVClientStructs.FFXIV.Client.Enums.DetailKind;

namespace DailyRoutines.ModulesPublic;

public class OptimizedRecipeNote : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title               = Lang.Get("OptimizedRecipeNoteTitle"),
        Description         = Lang.Get("OptimizedRecipeNoteDescription"),
        Category            = ModuleCategory.UIOptimization,
        ModulesPrerequisite = ["AutoShowItemNPCShopInfo"]
    };

    public override ModulePermission Permission { get; } = new() { AllDefaultEnabled = true };

    private static readonly CompSig SimpleCraftGetAmountUpperLimitSig = new("4C 8B DC 48 83 EC ?? 48 8B 81 ?? ?? ?? ?? 44 0F B6 CA");
    private delegate        int     SimpleCraftGetAmountUpperLimitDelegate(nint agent, bool eventCase);
    private                 Hook<SimpleCraftGetAmountUpperLimitDelegate>? SimpleCraftGetAmountUpperLimitHook;
    
    private static readonly CompSig SimpleCraftAmountJudgeSig = new("0F 87 ?? ?? ?? ?? 48 8B 81 ?? ?? ?? ?? 48 85 C0");
    // ja → nop
    private readonly MemoryPatch simpleCraftAmountJudgePatch =
        new(SimpleCraftAmountJudgeSig.Get(), [0x90, 0x90, 0x90, 0x90, 0x90, 0x90]);

    private static readonly CompSig RecipeNotePraticeSettingSetupSig = new
    (
        "48 89 5C 24 ?? 48 89 6C 24 ?? 48 89 74 24 ?? 57 48 83 EC ?? BA ?? ?? ?? ?? 49 8B F0 48 8B E9 E8 ?? ?? ?? ?? 48 8D 4E ?? 48 8B D8 E8 ?? ?? ?? ?? 48 8B D0 48 8B CB E8 ?? ?? ?? ?? 48 8D 4E"
    );
    private unsafe delegate AtkValue* RecipeNotePraticeSettingSetupDelegate
    (
        AtkEventListener* listener,
        AtkValue*         returnValue,
        AtkValue*         values
    );
    private Hook<RecipeNotePraticeSettingSetupDelegate>? RecipeNotePraticeSettingSetupHook;
    
    private Config config = null!;
    
    private readonly Dictionary<uint, CaculationResult> caculationResults = [];

    private TextButtonNode?    caculateRecipeButton;
    private TextButtonNode?    switchJobButton;
    private TextureButtonNode? clearSearchButton;

    private TextButtonNode?      displayOthersButton;
    private HorizontalListNode?  displayOthersJobsLayout;
    private List<IconButtonNode> displayOthersJobButtons = [];

    private List<IconButtonNode> materialSourceButtons = [];

    private TextButtonNode? levelRecipeButton;
    private TextButtonNode? specialRecipeButton;
    private TextButtonNode? masterRecipeButton;

    private DalamudLinkPayload? installRaphaelLinkPayload;
    private Task?               installRaphaelTask;

    private uint lastRecipeID;

    protected override unsafe void Init()
    {
        TaskHelper ??= new() { TimeoutMS = 15_000 };

        config = Config.Load(this) ?? new();

        SimpleCraftGetAmountUpperLimitHook = 
            SimpleCraftGetAmountUpperLimitSig.GetHook<SimpleCraftGetAmountUpperLimitDelegate>(SimpleCraftGetAmountUpperLimitDetour);

        RecipeNotePraticeSettingSetupHook =
            RecipeNotePraticeSettingSetupSig.GetHook<RecipeNotePraticeSettingSetupDelegate>(RecipeNotePraticeSettingSetupDetour);

        if (config.IsQuickSynthesisMore)
        {
            simpleCraftAmountJudgePatch.Enable();
            SimpleCraftGetAmountUpperLimitHook.Enable();
        }
        
        if (config.IsMorePraticeQuality)
            RecipeNotePraticeSettingSetupHook.Enable();
        
        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PostSetup,           "RecipeNote", OnAddon);
        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PostDraw,            "RecipeNote", OnAddon);
        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PostRequestedUpdate, "RecipeNote", OnAddon);
        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PreFinalize,         "RecipeNote", OnAddon);
    }

    protected override void Uninit()
    {
        DService.Instance().AddonLifecycle.UnregisterListener(OnAddon);
        OnAddon(AddonEvent.PreFinalize, null);

        AddonActionsPreview.Addon?.Dispose();
        AddonActionsPreview.Addon = null;

        foreach (var x in caculationResults.Values)
        {
            LinkPayloadManager.Instance().Unreg(x.CopyLinkPayload.CommandId);
            LinkPayloadManager.Instance().Unreg(x.PreviewLinkPayload.CommandId);
        }
        caculationResults.Clear();

        if (installRaphaelLinkPayload != null)
            LinkPayloadManager.Instance().Unreg(installRaphaelLinkPayload.CommandId);
        installRaphaelTask = null;
    }

    protected override void ConfigUI()
    {
        if (ImGui.Checkbox(Lang.Get("OptimizedRecipeNote-Config-SearchClearButton"), ref config.IsSearchClearButton))
        {
            config.Save(this);
            OnAddon(AddonEvent.PreFinalize, null);
        }       
        ImGuiOm.HelpMarker(Lang.Get("OptimizedRecipeNote-Config-SearchClearButton-Help"));
        
        if (ImGui.Checkbox(Lang.Get("OptimizedRecipeNote-Config-CategoryButtons"), ref config.IsCategoryButtons))
        {
            config.Save(this);
            OnAddon(AddonEvent.PreFinalize, null);
        }       
        ImGuiOm.HelpMarker(Lang.Get("OptimizedRecipeNote-Config-CategoryButtons-Help"));
        
        if (ImGui.Checkbox(Lang.Get("OptimizedRecipeNote-Config-DisplayOthersButtons"), ref config.IsDisplayOthersButtons))
        {
            config.Save(this);
            OnAddon(AddonEvent.PreFinalize, null);
        }       
        ImGuiOm.HelpMarker(Lang.Get("OptimizedRecipeNote-Config-DisplayOthersButtons-Help"));
        
        if (ImGui.Checkbox(Lang.Get("OptimizedRecipeNote-Config-MaterialSourceButtons"), ref config.IsMaterialSourceButtons))
        {
            config.Save(this);
            OnAddon(AddonEvent.PreFinalize, null);
        }       
        ImGuiOm.HelpMarker(Lang.Get("OptimizedRecipeNote-Config-MaterialSourceButtons-Help"));
        
        if (ImGui.Checkbox(Lang.Get("OptimizedRecipeNote-Config-SwitchJobButton"), ref config.IsSwitchJobButton))
        {
            config.Save(this);
            OnAddon(AddonEvent.PreFinalize, null);
        }       
        ImGuiOm.HelpMarker(Lang.Get("OptimizedRecipeNote-Config-SwitchJobButton-Help"));
        
        if (ImGui.Checkbox(Lang.Get("OptimizedRecipeNote-Config-QuickSynthesisMore"), ref config.IsQuickSynthesisMore))
        {
            config.Save(this);
            
            simpleCraftAmountJudgePatch.Set(config.IsQuickSynthesisMore);
            SimpleCraftGetAmountUpperLimitHook.Toggle(config.IsQuickSynthesisMore);
        }
        ImGuiOm.HelpMarker(Lang.Get("OptimizedRecipeNote-Config-QuickSynthesisMore-Help"));
        
        if (ImGui.Checkbox(Lang.Get("OptimizedRecipeNote-Config-MorePraticeQuality"), ref config.IsMorePraticeQuality))
        {
            config.Save(this);
            
            RecipeNotePraticeSettingSetupHook.Toggle(config.IsMorePraticeQuality);
        }
        ImGuiOm.HelpMarker(Lang.Get("OptimizedRecipeNote-Config-MorePraticeQuality-Help"));
    }
    
    public static unsafe int SimpleCraftGetAmountUpperLimitDetour(nint agentRecipeNote, bool isHQ)
    {
        var selectedRecipe = RecipeNote.Instance()->RecipeList->SelectedRecipe;
        if (selectedRecipe == null) return 0;

        var maxPortion = 255;

        foreach (var ingredient in selectedRecipe->Ingredients)
        {
            if (ingredient.ItemId == 0) continue;

            var itemCountNQ = InventoryManager.Instance()->GetInventoryItemCount(ingredient.ItemId);
            var itemCountHQ = InventoryManager.Instance()->GetInventoryItemCount(ingredient.ItemId, true);

            var itemCount = isHQ ? itemCountNQ + itemCountHQ : itemCountNQ;
            if (itemCount == 0) return 0;

            var portion = itemCount / ingredient.Amount;
            if (portion == 0) return 0;

            portion    = (int)MathF.Min(255,     portion);
            maxPortion = (int)MathF.Min(portion, maxPortion);
        }

        return maxPortion;
    }
    
    private unsafe AtkValue* RecipeNotePraticeSettingSetupDetour(AtkEventListener* listener, AtkValue* returnValue, AtkValue* values)
    {
        // 初始品质
        values[1].SetUInt(values[1].UInt * 2);
        
        // 当前品质
        values[0].SetUInt(values[1].UInt);
        
        // 初始品质文字
        values[2].SetManagedString(DService.Instance().SeStringEvaluator.EvaluateFromAddon(14222, [values[1].UInt]));

        return RecipeNotePraticeSettingSetupHook.Original(listener, returnValue, values);
    }

    private unsafe void OnAddon(AddonEvent type, AddonArgs args)
    {
        switch (type)
        {
            case AddonEvent.PreFinalize:
                RemoveCaculateRecipeButton();

                RemoveSwitchJobButton();

                RemoveMaterialSourceButtons();

                RemoveDisplayOthersButtons();

                RemoveClearSearchButton();
                
                RemoveCategoryButtons();

                break;

            case AddonEvent.PostSetup:
                if (AddonActionsPreview.Addon?.Nodes is not { Count: > 0 } nodes) return;
                foreach (var node in nodes)
                    node.Alpha = 1;
                break;

            case AddonEvent.PostRequestedUpdate:
                if (!AgentRecipeNote.Instance()->RecipeSearchOpen && TryGetCurrentRecipe(out var recipeID, out _))
                    lastRecipeID = recipeID;

                UpdateRecipeAddonButtons();
                break;

            case AddonEvent.PostDraw:
                if (!RecipeNoteAddon->IsAddonAndNodesReady()) return;
                
                // 求解配方按钮
                CreateCaculateRecipeButton();
                
                // 切换职业按钮
                CreateSwitchJobButton();
                
                // 材料来源按钮
                CreateMaterialSourceButtons();
                
                // 显示其余按钮
                CreateDisplayOthersButtons();
                
                // 清除搜索按钮
                CreateClearSearchButton();
                
                // 分类按钮
                CreateCategoryButtons();

                UpdateRecipeAddonButtons();

                break;
        }

        return;

        void UpdateRecipeAddonButtons()
        {
            if (RecipeNoteAddon == null) return;
        
            UpdateCaculateRecipeButton();
        
            UpdateSwitchJobButton();

            UpdateClearSearchButton();
        
            UpdateDisplayOthersButtons();
        
            UpdateMaterialSourceButtons();
        }
    }
    
    #region 原生界面元素插入

    // 清除搜索按钮
    private unsafe void CreateClearSearchButton()
    {
        if (!config.IsSearchClearButton || clearSearchButton != null)
            return;
        
        clearSearchButton = new()
        {
            IsVisible          = true,
            Position           = new(130, 25),
            Size               = new(28),
            TexturePath        = "ui/uld/WindowA_Button_hr1.tex",
            TextureCoordinates = Vector2.Zero,
            TextureSize        = new(28),
            OnClick = () =>
            {
                if (lastRecipeID == 0) return;

                var agent = AgentRecipeNote.Instance();
                if (!agent->RecipeSearchOpen) return;

                agent->OpenRecipeByRecipeId(lastRecipeID);
            }
        };

        clearSearchButton.AttachNode(RecipeNoteAddon->GetNodeById(24));
    }

    private unsafe void UpdateClearSearchButton()
    {
        if (!config.IsSearchClearButton || clearSearchButton == null)
            return;
        
        clearSearchButton.IsVisible = AgentRecipeNote.Instance()->RecipeSearchOpen && lastRecipeID != 0;
    }

    private void RemoveClearSearchButton()
    {
        clearSearchButton?.Dispose();
        clearSearchButton = null;
    }
    
    // 切换分类按钮
    private unsafe void CreateCategoryButtons()
    {
        if (!config.IsCategoryButtons ||
            levelRecipeButton   != null &&
            specialRecipeButton != null &&
            masterRecipeButton  != null)
            return;

        if (levelRecipeButton == null)
        {
            levelRecipeButton = new()
            {
                IsVisible   = true,
                Position    = new(0, 32),
                Size        = new(58, 38),
                TextTooltip = LuminaWrapper.GetAddonText(1710),
                OnClick = () =>
                {
                    AgentRecipeNote.Instance()->SelectedRecipeCategoryPage = 2;
                    var button = RecipeNoteAddon->GetComponentButtonById(35);

                    if (button != null)
                    {
                        DService.Instance().Framework.Run(() => button->Click());
                        DService.Instance().Framework.Run(() => button->Click());
                        DService.Instance().Framework.Run(() => button->Click());
                        DService.Instance().Framework.Run(() => button->Click());
                    }
                }
            };
            levelRecipeButton.BackgroundNode.IsVisible = false;
            levelRecipeButton.AttachNode(RecipeNoteAddon->GetNodeById(32));
        }

        if (specialRecipeButton == null)
        {
            specialRecipeButton = new()
            {
                IsVisible   = true,
                Position    = new(50, 32),
                Size        = new(58, 38),
                TextTooltip = LuminaWrapper.GetAddonText(1711),
                OnClick = () =>
                {
                    AgentRecipeNote.Instance()->SelectedRecipeCategoryPage = 0;
                    var button = RecipeNoteAddon->GetComponentButtonById(35);

                    if (button != null)
                    {
                        DService.Instance().Framework.Run(() => button->Click());
                        DService.Instance().Framework.Run(() => button->Click());
                        DService.Instance().Framework.Run(() => button->Click());
                        DService.Instance().Framework.Run(() => button->Click());
                    }
                }
            };
            specialRecipeButton.BackgroundNode.IsVisible = false;
            specialRecipeButton.AttachNode(RecipeNoteAddon->GetNodeById(32));
        }

        if (masterRecipeButton == null)
        {
            masterRecipeButton = new()
            {
                IsVisible   = true,
                Position    = new(102, 32),
                Size        = new(58, 38),
                TextTooltip = LuminaWrapper.GetAddonText(14212),
                OnClick = () =>
                {
                    AgentRecipeNote.Instance()->SelectedRecipeCategoryPage = 1;
                    var button = RecipeNoteAddon->GetComponentButtonById(35);

                    if (button != null)
                    {
                        DService.Instance().Framework.Run(() => button->Click());
                        DService.Instance().Framework.Run(() => button->Click());
                        DService.Instance().Framework.Run(() => button->Click());
                        DService.Instance().Framework.Run(() => button->Click());
                    }
                }
            };
            masterRecipeButton.BackgroundNode.IsVisible = false;
            masterRecipeButton.AttachNode(RecipeNoteAddon->GetNodeById(32));
        }
    }

    private void RemoveCategoryButtons()
    {
        levelRecipeButton?.Dispose();
        levelRecipeButton = null;

        specialRecipeButton?.Dispose();
        specialRecipeButton = null;

        masterRecipeButton?.Dispose();
        masterRecipeButton = null;
    }
    
    // 显示其余配方按钮
    private unsafe void CreateDisplayOthersButtons()
    {
        if (!config.IsDisplayOthersButtons ||
            displayOthersButton     != null &&
            displayOthersJobsLayout != null)
            return;

        if (displayOthersButton == null)
        {
            displayOthersButton = new()
            {
                Position  = new(0, -32),
                Size      = new(140, 32),
                String    = Lang.Get("OptimizedRecipeNote-Button-ShowOtherRecipes"),
                IsVisible = true,
                OnClick = () =>
                {
                    if (!TryGetCurrentRecipe(out _, out var recipe))
                        return;
                    
                    if (!SameItemRecipes.TryGetValue(recipe.ItemResult.RowId, out _))
                        return;

                    AgentRecipeNote.Instance()->SearchRecipeByItemId(recipe.ItemResult.RowId);
                }
            };

            displayOthersButton.LabelNode.AutoAdjustTextSize();
            displayOthersButton.AttachNode(RecipeNoteAddon->GetNodeById(57));
        }

        if (displayOthersJobsLayout == null)
        {
            displayOthersJobsLayout = new()
            {
                IsVisible   = true,
                ItemSpacing = 5,
                Size        = new(220, 24),
                Position    = new(142, -30)
            };

            for (var i = 0U; i < 8; i++)
            {
                var iconButtonNode = new IconButtonNode
                {
                    IconId      = 62008 + i,
                    Size        = new(24),
                    IsVisible   = true,
                    TextTooltip = LuminaWrapper.GetJobName(8 + i)
                };

                var iconNode = new IconImageNode
                {
                    IconId         = 62008 + i,
                    Size           = new(24),
                    IsVisible      = true,
                    ImageNodeFlags = ImageNodeFlags.AutoFit
                };

                iconNode.AddTimeline
                (
                    new TimelineBuilder()
                        .AddFrameSetWithFrame(1,  10, 1,  Vector2.Zero, 255, multiplyColor: new(100.0f))
                        .AddFrameSetWithFrame(11, 17, 11, Vector2.Zero, 255, multiplyColor: new(100.0f))
                        .AddFrameSetWithFrame
                        (
                            18,
                            26,
                            18,
                            Vector2.Zero + new Vector2(0.0f, 1.0f),
                            255,
                            multiplyColor: new(100.0f)
                        )
                        .AddFrameSetWithFrame(27, 36, 27, Vector2.Zero, 153, multiplyColor: new(80.0f))
                        .AddFrameSetWithFrame(37, 46, 37, Vector2.Zero, 255, multiplyColor: new(100.0f))
                        .AddFrameSetWithFrame(47, 53, 47, Vector2.Zero, 255, multiplyColor: new(100.0f))
                        .Build()
                );

                iconButtonNode.BackgroundNode.IsVisible = false;
                iconButtonNode.ImageNode.IsVisible      = false;
                iconNode.AttachNode(iconButtonNode);

                displayOthersJobButtons.Add(iconButtonNode);
                displayOthersJobsLayout.AddNode(iconButtonNode);
            }

            displayOthersJobsLayout.AttachNode(RecipeNoteAddon->GetNodeById(57));
        }
    }

    private unsafe void UpdateDisplayOthersButtons()
    {
        if (!config.IsDisplayOthersButtons        ||
            displayOthersButton           == null ||
            displayOthersJobsLayout       == null ||
            displayOthersJobButtons.Count != 8)
            return;

        if (!TryGetCurrentRecipe(out _, out var recipe) ||
            !SameItemRecipes.TryGetValue(recipe.ItemResult.RowId, out var allRecipes))
        {
            displayOthersButton.IsVisible     = false;
            displayOthersJobsLayout.IsVisible = false;
            return;
        }

        displayOthersButton.IsVisible     = true;
        displayOthersJobsLayout.IsVisible = true;

        var allCraftTypes = allRecipes.ToDictionary(x => x.CraftType.RowId, x => x.RowId);
        for (var i = 0U; i < 8; i++)
        {
            var node = displayOthersJobButtons[(int)i];

            if (allCraftTypes.TryGetValue(i, out var otherRecipeID))
            {
                node.Alpha     = 1;
                node.IsEnabled = true;
                node.OnClick = () =>
                {
                    if (TryGetCurrentRecipe(out var recipeID, out _) &&
                        recipeID == otherRecipeID)
                        return;

                    AgentRecipeNote.Instance()->OpenRecipeByRecipeId(otherRecipeID);
                };
            }
            else
            {
                node.Alpha     = 0.2f;
                node.IsEnabled = false;
                node.OnClick   = () => { };
            }
        }
    }

    private void RemoveDisplayOthersButtons()
    {
        displayOthersButton?.Dispose();
        displayOthersButton = null;
        
        displayOthersJobButtons.Clear();

        displayOthersJobsLayout?.Dispose(); // 把子节点都一并清除掉了
        displayOthersJobsLayout = null;
    }
    
    // 材料来源
    private unsafe void CreateMaterialSourceButtons()
    {
        if (!config.IsMaterialSourceButtons ||
            materialSourceButtons.Count != 0)
            return;
        
        for (var i = 0U; i < 6; i++)
        {
            var componentNode = RecipeNoteAddon->GetComponentNodeById(89 + i);

            var index = i;
            var buttonNode = new IconButtonNode
            {
                IconId    = 60412,
                Size      = new(32),
                IsVisible = true,
                Position  = new(-26, 8f),
                OnClick = () =>
                {
                    if (!TryGetCurrentRecipe(out _, out var recipe) ||
                        !recipe.Ingredient[(int)index].IsValid)
                        return;

                    var item        = recipe.Ingredient[(int)index].Value;
                    var sourceState = ItemSourceInfo.Query(item.RowId).State;
                    var hasNPCShop  = sourceState == ItemSourceQueryState.Ready;

                    // 既能 NPC 买到又能市场布告板
                    if (item.ItemSearchCategory.RowId > 0 && hasNPCShop)
                    {
                        if (!PluginConfig.Instance().ConflictKeyBinding.IsPressed())
                            OpenShopListByItemIDIPC.InvokeFunc(item.RowId);
                        else
                            ChatManager.Instance().SendMessage($"/pdr market {item.Name}");
                    }
                    else if (hasNPCShop)
                        OpenShopListByItemIDIPC.InvokeFunc(item.RowId);
                    else if (item.ItemSearchCategory.RowId > 0)
                        ChatManager.Instance().SendMessage($"/pdr market {item.Name}");
                }
            };

            var backgroundNode = (SimpleNineGridNode)buttonNode.BackgroundNode;

            backgroundNode.TexturePath        = "ui/uld/partyfinder_hr1.tex";
            backgroundNode.TextureCoordinates = new(38, 2);
            backgroundNode.TextureSize        = new(32, 34);
            backgroundNode.LeftOffset         = 0;
            backgroundNode.RightOffset        = 0;

            materialSourceButtons.Add(buttonNode);
            buttonNode.AttachNode(componentNode);
        }
    }

    private unsafe void UpdateMaterialSourceButtons()
    {
        if (!config.IsMaterialSourceButtons ||
            materialSourceButtons.Count != 6)
            return;
        
        if (!TryGetCurrentRecipe(out _, out var recipe))
            return;
        
        var maxIngredientAmount = 0;
        for (var i = 0; i < recipe.Ingredient.Count; i++)
        {
            if (recipe.Ingredient[i] is not { IsValid: true, RowId: > 100 }) continue;
            if (recipe.AmountIngredient[i] is var amount && amount > maxIngredientAmount)
                maxIngredientAmount = amount;
        }

        var appendOffset = maxIngredientAmount >= 10 ? 30 : 10;
        var resNode0 = RecipeNoteAddon->GetNodeById(95);
        if (resNode0 != null)
            resNode0->SetXFloat(46 + appendOffset);

        var resNode1 = RecipeNoteAddon->GetNodeById(88);
        if (resNode1 != null)
            resNode1->SetXFloat(0 + appendOffset);

        var resNode2 = RecipeNoteAddon->GetNodeById(84);
        if (resNode2 != null)
            resNode2->SetXFloat(0 + appendOffset);

        var resNodeProgress = RecipeNoteAddon->GetNodeById(2);
        if (resNodeProgress != null)
            resNodeProgress->SetAlpha(0);

        for (var d = 0; d < materialSourceButtons.Count; d++)
        {
            if (!recipe.Ingredient[d].IsValid) break;

            var item        = recipe.Ingredient[d].Value;
            var sourceState = ItemSourceInfo.Query(item.RowId).State;
            var hasNPCShop  = sourceState == ItemSourceQueryState.Ready;

            var button     = materialSourceButtons[d];
            var sourceText = string.Empty;

            button.X = -6 + (maxIngredientAmount >= 10 ? -20 : 0);

            // 既能 NPC 买到又能市场布告板
            if (item.ItemSearchCategory.RowId > 0 && hasNPCShop)
            {
                button.IconId = 60412;
                sourceText    = $"{LuminaWrapper.GetAddonText(350)} / {LuminaWrapper.GetAddonText(548)} [{Lang.Get("ConflictKey")}]";
            }
            else if (hasNPCShop)
            {
                button.IconId = 60412;
                sourceText    = $"{LuminaWrapper.GetAddonText(350)}";
            }
            else if (item.ItemSearchCategory.RowId > 0)
            {
                button.IconId = 60570;
                sourceText    = $"{LuminaWrapper.GetAddonText(548)}";
            }
            else
                sourceText = string.Empty;

            if (sourceText == string.Empty)
                button.IsVisible = false;
            else
            {
                button.IsVisible   = true;
                button.TextTooltip = sourceText;
            }
        }
    }
    
    private unsafe void RemoveMaterialSourceButtons()
    {
        foreach (var x in materialSourceButtons)
            x.Dispose();
        materialSourceButtons.Clear();

        if (RecipeNoteAddon != null)
        {
            var resNode0 = RecipeNoteAddon->GetNodeById(95);
            if (resNode0 != null)
                resNode0->SetXFloat(46);

            var resNode1 = RecipeNoteAddon->GetNodeById(88);
            if (resNode1 != null)
                resNode1->SetXFloat(0);

            var resNode2 = RecipeNoteAddon->GetNodeById(84);
            if (resNode2 != null)
                resNode2->SetXFloat(0);
        }
    }
    
    // 切换职业
    private unsafe void CreateSwitchJobButton()
    {
        if (!config.IsSwitchJobButton ||
            switchJobButton != null)
            return;
        
        switchJobButton = new()
        {
            Position = new(228, 522),
            Size     = new(140, 36),
            String   = Lang.Get("OptimizedRecipeNote-Button-SwitchJob"),
            OnClick  = () =>
            {
                if (!TryGetCurrentRecipe(out var recipeID, out var recipe))
                    return;

                // 职业对了
                if (recipe.CraftType.RowId == LocalPlayerState.ClassJob - 8) return;

                // 能直接切换
                if (!DService.Instance().Condition[ConditionFlag.PreparingToCraft])
                {
                    LocalPlayerState.SwitchGearset(recipe.CraftType.RowId + 8);
                    return;
                }

                TaskHelper.Enqueue(() => AgentRecipeNote.Instance()->Hide());
                TaskHelper.Enqueue(() => !DService.Instance().Condition[ConditionFlag.PreparingToCraft]);
                TaskHelper.Enqueue(() => LocalPlayerState.SwitchGearset(recipe.CraftType.RowId + 8));
                TaskHelper.Enqueue(() => AgentRecipeNote.Instance()->OpenRecipeByRecipeId(recipeID));
            }
        };

        switchJobButton.AttachNode(RecipeNoteAddon->GetNodeById(57));
    }

    private unsafe void UpdateSwitchJobButton()
    {
        if (!config.IsSwitchJobButton ||
            switchJobButton == null)
            return;

        if (!TryGetCurrentRecipe(out _, out var recipe))
        {
            switchJobButton.IsVisible = false;
            return;
        }

        var startCraftButton = RecipeNoteAddon->GetComponentButtonById(104);
        if (recipe.CraftType.RowId != LocalPlayerState.ClassJob - 8)
        {
            switchJobButton.IsVisible = true;
            if (startCraftButton != null)
                startCraftButton->OwnerNode->ToggleVisibility(false);
            
            switchJobButton.TextTooltip = LuminaWrapper.GetJobName(8 + recipe.CraftType.RowId);

            for (var i = 102U; i < 105; i++)
            {
                var buttonNode = RecipeNoteAddon->GetComponentButtonById(i);
                if (buttonNode != null)
                    buttonNode->SetEnabledState(false);
            }
        }
        else
        {
            switchJobButton.IsVisible = false;
            if (startCraftButton != null)
                startCraftButton->OwnerNode->ToggleVisibility(true);

            for (var i = 102U; i < 105; i++)
            {
                var buttonNode = RecipeNoteAddon->GetComponentButtonById(i);
                if (buttonNode != null)
                    buttonNode->SetEnabledState(true);
            }
        }
    }
    
    private void RemoveSwitchJobButton()
    {
        switchJobButton?.Dispose();
        switchJobButton = null;
    }
    
    // 求解配方
    private unsafe void CreateCaculateRecipeButton()
    {
        if (!config.IsCaculateRecipeButton ||
            caculateRecipeButton != null)
            return;

        caculateRecipeButton = new()
        {
            Position = new(228, 490),
            Size     = new(140, 32),
            String   = Lang.Get("OptimizedRecipeNote-Button-CaculateRecipe")
        };
        
        caculateRecipeButton.OnClick = () =>
        {
            if (!DService.Instance().PI.IsPluginEnabled(RaphaelIPC.INTERNAL_NAME))
            {
                PrintInstallRaphaelPluginMessage();
                return;
            }
            
            if (!TryGetCurrentRecipe(out var recipeID, out var recipe))
                return;

            // 职业不对
            if (recipe.CraftType.RowId != LocalPlayerState.ClassJob - 8) return;

            var craftPoint    = PlayerState.Instance()->GetAttributeByIndex(PlayerAttribute.CraftingPoints);
            var craftsmanship = PlayerState.Instance()->GetAttributeByIndex(PlayerAttribute.Craftsmanship);
            var control       = PlayerState.Instance()->GetAttributeByIndex(PlayerAttribute.Control);
            var id = RaphaelIPC.StartCalculation
            (
                recipeID,
                new()
                {
                    BackloadProgress  = true,
                    EnsureReliability = true,
                    TargetQuality     = GetRecipeMaxQuality(recipe),
                    MaxThreads        = Environment.ProcessorCount,
                    TimeoutSeconds    = 90
                }
            );
            
            caculateRecipeButton.IsEnabled = false;
            TaskHelper.Enqueue
            (
                () =>
                {
                    var response = RaphaelIPC.GetCalculationStatus(id);

                    switch (response.Status)
                    {
                        case RaphaelCalculationStatus.Success:
                            caculateRecipeButton.IsEnabled = true;

                            var copyLinkPayload    = LinkPayloadManager.Instance().Reg(OnClickCopyPayload,    out _);
                            var previewLinkPayload = LinkPayloadManager.Instance().Reg(OnClickPreviewPayload, out _);
                            caculationResults[id] = new
                            (
                                response.Actions,
                                recipeID,
                                craftPoint,
                                craftsmanship,
                                control,
                                copyLinkPayload,
                                previewLinkPayload
                            );
                            PrintActionsMessage(caculationResults[id]);
                            return true;
                        case RaphaelCalculationStatus.Failed:
                            caculateRecipeButton.IsEnabled = true;
                            if (!string.IsNullOrWhiteSpace(response.ErrorMessage))
                                NotifyHelper.Instance().ChatError(response.ErrorMessage);

                            TaskHelper.Abort();
                            return true;
                        default:
                            return false;
                    }
                },
                "请求技能数据"
            );
        };

        caculateRecipeButton.AttachNode(RecipeNoteAddon->GetNodeById(57));
    }

    private void UpdateCaculateRecipeButton()
    {
        if (!config.IsCaculateRecipeButton ||
            caculateRecipeButton == null)
            return;

        if (!TryGetCurrentRecipe(out _, out var recipe) ||
            recipe.CraftType.RowId != LocalPlayerState.ClassJob - 8)
        {
            caculateRecipeButton.IsVisible = false;
            return;
        }
        
        caculateRecipeButton.IsVisible = true;
    }

    private void RemoveCaculateRecipeButton()
    {
        caculateRecipeButton?.Dispose();
        caculateRecipeButton = null;
    }

    #endregion

    #region 消息事件

    private void OnClickInstallRaphaelPayload(uint id, SeString _)
    {
        if (DService.Instance().PI.InstalledPlugins.Any(x => x.InternalName == "Raphael.Dalamud"))
        {
            ChatManager.Instance().SendMessage("/xlenableplugin Raphael.Dalamud");
            return;
        }

        if (installRaphaelTask != null) return;

        installRaphaelTask = DService.Instance().Framework
                                     .RunOnTick
                                     (async () => await DalamudReflector.AddPlugin
                                                  (
                                                      "https://raw.githubusercontent.com/AtmoOmen/DalamudPlugins/main/pluginmaster.json",
                                                      "Raphael.Dalamud"
                                                  )
                                     )
                                     .ContinueWith(_ => installRaphaelTask = null);
    }

    private void OnClickPreviewPayload(uint id, SeString _)
    {
        if (caculationResults.FirstOrDefault(x => x.Value.PreviewLinkPayload.CommandId == id) is not { Value.RecipeID: > 0 } result) return;

        AddonActionsPreview.OpenWithActions(TaskHelper, result.Value);
    }

    private void OnClickCopyPayload(uint id, SeString _)
    {
        if (caculationResults.FirstOrDefault(x => x.Value.CopyLinkPayload.CommandId == id) is not { Value.RecipeID: > 0 } result) return;

        var builder = new StringBuilder();
        foreach (var action in result.Value.Actions)
            builder.AppendLine($"/ac {LuminaWrapper.GetActionName(action)} <wait.3>");
        ImGui.SetClipboardText(builder.ToString());

        NotifyHelper.Instance().NotificationSuccess($"{Lang.Get("CopiedToClipboard")}");
    }

    #endregion

    #region 消息发送

    private static void PrintActionsMessage(CaculationResult result)
    {
        var builder = new SeStringBuilder();
        builder.AddText($"{Lang.Get("OptimizedRecipeNote-Message-CaculationResult")}")
               .Add(NewLinePayload.Payload)
               .AddText($"{Lang.Get("Recipe")}: ")
               .AddItemLink(result.GetRecipe().ItemResult.RowId)
               .AddText(" (")
               .AddIcon(result.GetJob().ToBitmapFontIcon())
               .AddText(result.GetJob().Name.ToString())
               .AddText(")")
               .Add(NewLinePayload.Payload)
               .AddText($"{Lang.Get("Step")}: ")
               .AddText($"{Lang.Get("OptimizedRecipeNote-Message-StepsInfo", result.Actions.Count, result.Actions.Count * 3)}")
               .Add(NewLinePayload.Payload)
               .AddText($"{Lang.Get("Operation")}: ")
               .Add(RawPayload.LinkTerminator)
               .Add(result.CopyLinkPayload)
               .AddText("[")
               .AddUiForeground(35)
               .AddText($"{Lang.Get("Copy")}")
               .AddUiForegroundOff()
               .AddText("]")
               .Add(RawPayload.LinkTerminator)
               .AddText(" / ")
               .Add(RawPayload.LinkTerminator)
               .Add(result.PreviewLinkPayload)
               .AddText("[")
               .AddUiForeground(35)
               .AddText($"{Lang.Get("Preview")}")
               .AddUiForegroundOff()
               .AddText("]")
               .Add(RawPayload.LinkTerminator);
        NotifyHelper.Instance().Chat(builder.Build());
    }

    private void PrintInstallRaphaelPluginMessage()
    {
        installRaphaelLinkPayload ??= LinkPayloadManager.Instance().Reg(OnClickInstallRaphaelPayload, out _);

        var message = new SeStringBuilder().AddIcon(BitmapFontIcon.Warning)
                                           .AddText($" {Lang.Get("OptimizedRecipeNote-Message-InstallRapheal")}")
                                           .Add(NewLinePayload.Payload)
                                           .AddText($"{Lang.Get("Operation")}: ")
                                           .Add(RawPayload.LinkTerminator)
                                           .Add(installRaphaelLinkPayload)
                                           .AddText("[")
                                           .AddUiForeground(35)
                                           .AddText($"{Lang.Get("Enable")} / {Lang.Get("Install")}")
                                           .AddUiForegroundOff()
                                           .AddText("]")
                                           .Add(RawPayload.LinkTerminator)
                                           .Build();
        NotifyHelper.Instance().Chat(message);
    }

    #endregion
    
    
    #region 工具

    private static unsafe bool TryGetCurrentRecipe(out uint recipeID, out Recipe recipe)
    {
        recipeID = 0;
        recipe   = default;
        
        var recipeList = UIState.Instance()->RecipeNote.RecipeList;
        if (recipeList == null) return false;

        var data = recipeList->SelectedRecipe;
        if (data == null) return false;

        if (!LuminaGetter.TryGetRow(data->RecipeId, out Recipe recipeRow) ||
            recipeRow.ItemResult.Value is not { RowId: > 0 })
            return false;
        
        recipeID = data->RecipeId;
        recipe   = recipeRow;
        return true;
    }

    private static int GetRecipeMaxQuality(Recipe recipe) =>
        (int)(GetRecipeLevelTable(recipe).Quality * (float)recipe.QualityFactor / 100f);

    private static RecipeLevelTable GetRecipeLevelTable(Recipe recipe) =>
        recipe.Number == 0 && LocalPlayerState.CurrentLevel < 100
            ? LuminaGetter.Get<RecipeLevelTable>().First(x => x.ClassJobLevel == LocalPlayerState.CurrentLevel)
            : recipe.RecipeLevelTable.Value;

    #endregion

    private class AddonActionsPreview
    (
        TaskHelper       taskHelper,
        CaculationResult result
    ) : NativeAddon
    {
        private static Task?                OpenAddonTask;
        public static  AddonActionsPreview? Addon  { get; set; }
        public         CaculationResult     Result { get; private set; } = result;
        public         List<DragDropNode>   Nodes  { get; set; }         = [];

        public WeakReference<TaskHelper> TaskHelper { get; private set; } = new(taskHelper);

        public TextButtonNode ExecuteButton { get; private set; }

        public static void OpenWithActions(TaskHelper taskHelper, CaculationResult result)
        {
            if (OpenAddonTask != null) return;

            var isAddonExisted = Addon?.IsOpen ?? false;

            if (Addon != null)
            {
                Addon.Dispose();
                Addon = null;
            }

            OpenAddonTask = DService.Instance().Framework.RunOnTick
            (
                () =>
                {
                    var rowCount = MathF.Ceiling(result.Actions.Count / 10f);
                    Addon ??= new(taskHelper, result)
                    {
                        InternalName = "DRRecipeNoteActionsPreview",
                        Title        = $"{Lang.Get("OptimizedRecipeNote-AddonTitle")}",
                        Subtitle     = $"{Lang.Get("OptimizedRecipeNote-Message-StepsInfo", result.Actions.Count, result.Actions.Count * 3)}",
                        Size         = new(500f, 160f + 50f * (rowCount - 1))
                    };
                    Addon.Open();
                },
                TimeSpan.FromMilliseconds(isAddonExisted ? 500 : 0)
            ).ContinueWith(_ => OpenAddonTask = null);
        }

        protected override unsafe void OnSetup(AtkUnitBase* addon, Span<AtkValue> atkValues)
        {
            if (Result.Actions.Count == 0) return;

            var statsRow = new HorizontalListNode
            {
                IsVisible = true,
                Position  = new(12, 40),
                Size      = new(0, 44)
            };

            var jobTextNode = new TextNode
            {
                IsVisible = true,
                TextFlags = TextFlags.AutoAdjustNodeSize,
                String = new SeStringBuilder()
                         .AddText($"{LuminaWrapper.GetAddonText(294)}: ")
                         .AddIcon(Result.GetJob().ToBitmapFontIcon())
                         .AddText(Result.GetJob().Name.ToString())
                         .Build()
                         .Encode()
            };
            jobTextNode.Size =  jobTextNode.GetTextDrawSize($"{jobTextNode.String}123");
            statsRow.Width   += jobTextNode.Width;
            statsRow.AddNode(jobTextNode);
            
            statsRow.Width += 12;
            statsRow.AddDummy(12);

            var craftmanshipTextNode = new TextNode
            {
                IsVisible = true,
                TextFlags = TextFlags.AutoAdjustNodeSize,
                String    = $"{LuminaWrapper.GetAddonText(3261)}: {Result.Craftmanship}"
            };
            statsRow.Width += craftmanshipTextNode.Width;
            statsRow.AddNode(craftmanshipTextNode);

            statsRow.Width += 12;
            statsRow.AddDummy(12);

            var controlTextNode = new TextNode
            {
                IsVisible = true,
                TextFlags = TextFlags.AutoAdjustNodeSize,
                String    = $"{LuminaWrapper.GetAddonText(3262)}: {Result.Control}"
            };
            statsRow.Width += controlTextNode.Width;
            statsRow.AddNode(controlTextNode);

            statsRow.Width += 12;
            statsRow.AddDummy(12);

            var craftPointTextNode = new TextNode
            {
                IsVisible = true,
                TextFlags = TextFlags.AutoAdjustNodeSize,
                String    = $"{LuminaWrapper.GetAddonText(3223)}: {Result.CraftPoint}"
            };
            statsRow.Width += craftPointTextNode.Width;
            statsRow.AddNode(craftPointTextNode);

            statsRow.AttachNode(this);

            var operationRow = new HorizontalFlexNode
            {
                IsVisible = true,
                Position  = new(8, 65),
                Size      = new(0, 44)
            };

            ExecuteButton = new TextButtonNode
            {
                IsVisible = true,
                Size      = new(100, 24),
                String    = Lang.Get("Execute"),
                OnClick = () =>
                {
                    if (Synthesis == null) return;
                    if (Result.Actions is not { Count: > 0 } actions) return;
                    if (!TaskHelper.TryGetTarget(out var taskHelper)) return;

                    for (var index = 0; index < actions.Count; index++)
                    {
                        var x = actions[index];
                        var i = index;
                        taskHelper.Enqueue
                        (() =>
                            {
                                if (DService.Instance().Condition[ConditionFlag.ExecutingCraftingAction]) return true;

                                ChatManager.Instance().SendMessage($"/ac {LuminaWrapper.GetActionName(x)}");
                                return false;
                            }
                        );
                        taskHelper.Enqueue(() => Nodes[i].Alpha = 0.2f);
                        taskHelper.Enqueue(() => !DService.Instance().Condition[ConditionFlag.ExecutingCraftingAction]);
                    }
                }
            };
            operationRow.Width += ExecuteButton.Width;
            operationRow.AddNode(ExecuteButton);

            operationRow.Width += 4;
            operationRow.AddDummy(4);

            var macroButtonCount = (int)Math.Ceiling(Result.Actions.Count / 15.0);

            for (var i = 0; i < macroButtonCount; i++)
            {
                var macroIndex = i;
                var copyMacroButton = new TextButtonNode
                {
                    IsVisible = true,
                    Size      = new(120, 24),
                    String    = Lang.Get("OptimizedRecipeNote-Button-CopyMacro", macroIndex + 1),
                    OnClick = () =>
                    {
                        var startIndex      = macroIndex * 15;
                        var endIndex        = Math.Min(startIndex                           + 15, Result.Actions.Count);
                        var actionsForMacro = Result.Actions.Skip(startIndex).Take(endIndex - startIndex);

                        var builder = new StringBuilder();
                        foreach (var action in actionsForMacro)
                            builder.AppendLine($"/ac {LuminaWrapper.GetActionName(action)} <wait.3>");
                        ImGui.SetClipboardText(builder.ToString());

                        NotifyHelper.Instance().NotificationSuccess($"{Lang.Get("CopiedToClipboard")}");
                    }
                };
                operationRow.Width += copyMacroButton.Width;
                operationRow.AddNode(copyMacroButton);

                operationRow.Width += 4;
                operationRow.AddDummy(4);
            }

            operationRow.AttachNode(this);

            var container = new VerticalListNode
            {
                IsVisible = true,
                Position  = new(12, 97),
                Size      = new(44)
            };

            var currentRow = new HorizontalFlexNode
            {
                IsVisible = true,
                Size      = new(0, 44)
            };

            var itemsInCurrentRow = 0;

            for (var index = 0; index < Result.Actions.Count; index++)
            {
                var actionID = Result.Actions[index];
                var iconID   = LuminaWrapper.GetActionIconID(actionID);
                if (iconID == 0) continue;

                if (itemsInCurrentRow >= 10)
                {
                    container.AddNode(currentRow);
                    container.AddDummy(4f);

                    currentRow = new HorizontalFlexNode
                    {
                        IsVisible = true,
                        Size      = new(0, 44)
                    };
                    itemsInCurrentRow = 0;
                }

                var dragDropNode = new DragDropNode
                {
                    Size         = new(44f),
                    IsVisible    = true,
                    IconId       = iconID,
                    AcceptedType = DragDropType.Nothing,
                    IsDraggable  = true,
                    IsClickable  = true,
                    Payload = new()
                    {
                        Type = actionID > 10_0000 ? DragDropType.CraftingAction : DragDropType.Action,
                        Int2 = (int)actionID
                    },
                    OnRollOver = node =>
                    {
                        var tooltipArgs = new AtkTooltipManager.AtkTooltipArgs();

                        tooltipArgs.ActionArgs.Flags = 1;
                        tooltipArgs.ActionArgs.Kind  = actionID > 10_0000 ? DetailKind.CraftingAction : DetailKind.Action;
                        tooltipArgs.ActionArgs.Id    = (int)actionID;

                        AtkStage.Instance()->TooltipManager.ShowTooltip(AtkTooltipType.Action, addon->Id, node, &tooltipArgs);
                    },
                    OnRollOut = node => node.HideTooltip()
                };
                dragDropNode.OnClicked = _ =>
                {
                    if (DService.Instance().Condition[ConditionFlag.ExecutingCraftingAction] ||
                        TaskHelper.TryGetTarget(out var taskHelper) && taskHelper.IsBusy)
                        return;

                    if (Synthesis != null)
                        dragDropNode.Alpha = 0.2f;
                    ChatManager.Instance().SendMessage($"/ac {LuminaWrapper.GetActionName(actionID)}");
                };
                Nodes.Add(dragDropNode);

                var actionIndexNode = new TextNode
                {
                    IsVisible        = true,
                    Position         = new(-4),
                    String           = $"{index + 1}",
                    FontType         = FontType.MiedingerMed,
                    TextFlags        = TextFlags.Edge | TextFlags.Emboss,
                    TextColor        = ColorHelper.GetColor(50),
                    TextOutlineColor = ColorHelper.GetColor(28),
                };
                actionIndexNode.AttachNode(dragDropNode);

                currentRow.AddNode(dragDropNode);
                currentRow.AddDummy(4);
                currentRow.Width += dragDropNode.Size.X + 4;

                itemsInCurrentRow++;
            }

            if (itemsInCurrentRow > 0)
                container.AddNode(currentRow);

            container.AttachNode(this);
        }

        protected override unsafe void OnUpdate(AtkUnitBase* addon)
        {
            if (DService.Instance().KeyState[VirtualKey.ESCAPE])
            {
                Close();

                if (SystemMenu != null)
                    SystemMenu->Close(true);

                return;
            }

            if (ExecuteButton != null && TaskHelper.TryGetTarget(out var taskHelper))
                ExecuteButton.IsEnabled = Synthesis != null && !taskHelper.IsBusy;
        }
    }

    private record CaculationResult
    (
        List<uint>         Actions,
        uint               RecipeID,
        int                CraftPoint,
        int                Craftmanship,
        int                Control,
        DalamudLinkPayload CopyLinkPayload,
        DalamudLinkPayload PreviewLinkPayload
    )
    {
        public Recipe GetRecipe() =>
            LuminaGetter.GetRow<Recipe>(RecipeID).GetValueOrDefault();

        public ClassJob GetJob() =>
            LuminaGetter.GetRow<ClassJob>(GetRecipe().CraftType.RowId + 8).GetValueOrDefault();
    }

    private class Config : ModuleConfig
    {
        // 搜索清除
        public bool IsSearchClearButton = true;
        
        // 分类
        public bool IsCategoryButtons = true;
        
        // 显示其余配方
        public bool IsDisplayOthersButtons = true;

        // 材料来源
        public bool IsMaterialSourceButtons = true;
        
        // 切换职业
        public bool IsSwitchJobButton = true;
        
        // 求解配方
        public bool IsCaculateRecipeButton = true;
        
        // 突破简易制作上限
        public bool IsQuickSynthesisMore = true;
        
        // 突破制作练习初期品质上限
        public bool IsMorePraticeQuality = true;
    }
    
    #region IPC

    [IPCSubscriber("DailyRoutines.Modules.AutoShowItemNPCShopInfo.OpenByItemID")]
    private static IPCSubscriber<uint, bool> OpenShopListByItemIDIPC;

    #endregion
    
    #region 常量

    private static readonly FrozenDictionary<uint, List<Recipe>> SameItemRecipes =
        LuminaGetter.Get<Recipe>()
                    .GroupBy(x => x.ItemResult.RowId)
                    .DistinctBy(x => x.Key)
                    .Where(x => x.Key > 0 && x.Count() > 1)
                    .ToFrozenDictionary(x => x.Key, x => x.DistinctBy(d => d.CraftType.RowId).ToList());

    #endregion
}
