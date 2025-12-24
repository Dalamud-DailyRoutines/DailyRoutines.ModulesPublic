using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading.Tasks;
using DailyRoutines.Abstracts;
using DailyRoutines.IPC;
using DailyRoutines.Managers;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit;
using KamiToolKit.Classes.Timelines;
using KamiToolKit.Nodes;
using Lumina.Excel.Sheets;
using ActionKind = FFXIVClientStructs.FFXIV.Client.UI.Agent.ActionKind;

namespace DailyRoutines.ModulesPublic;

public class OptimizedRecipeNote : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title               = GetLoc("OptimizedRecipeNoteTitle"),
        Description         = GetLoc("OptimizedRecipeNoteDescription"),
        Category            = ModuleCategories.UIOptimization,
        ModulesPrerequisite = ["BetterMarketBoard", "BetterTeleport"]
    };
    
    public override ModulePermission Permission { get; } = new() { AllDefaultEnabled = true };

    private static readonly Dictionary<uint, CaculationResult> CaculationResults = [];

    private static readonly Dictionary<uint, List<Recipe>> SameItemRecipes =
        LuminaGetter.Get<Recipe>()
                    .GroupBy(x => x.ItemResult.RowId)
                    .DistinctBy(x => x.Key)
                    .Where(x => x.Key > 0 && x.Count() > 1)
                    .ToDictionary(x => x.Key, x => x.DistinctBy(d => d.CraftType.RowId).ToList());
    
    private static TextButtonNode?    RecipeCaculationButton;
    private static TextButtonNode?    SwitchJobButton;
    private static TextureButtonNode? ClearSearchButton;
    
    private static TextButtonNode?      DisplayOthersButton;
    private static HorizontalListNode?  DisplayOthersIconsLayout;
    private static List<IconButtonNode> DisplayOthersJobButtons = [];

    private static List<IconButtonNode> GetShopInfoButtons = [];

    private static TextButtonNode? LevelRecipeButton;
    private static TextButtonNode? SpecialRecipeButton;
    private static TextButtonNode? MasterRecipeButton;
    
    private static DalamudLinkPayload? InstallRaphaelLinkPayload;
    private static Task?               InstallRaphaelTask;

    private static uint LastRecipeID;
    
    protected override void Init()
    {
        TaskHelper ??= new() { TimeLimitMS = 15_000 };
        
        DService.AddonLifecycle.RegisterListener(AddonEvent.PostSetup,           "RecipeNote", OnAddon);
        DService.AddonLifecycle.RegisterListener(AddonEvent.PostDraw,            "RecipeNote", OnAddon);
        DService.AddonLifecycle.RegisterListener(AddonEvent.PostRequestedUpdate, "RecipeNote", OnAddon);
        DService.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize,         "RecipeNote", OnAddon);
    }

    protected override void Uninit()
    {
        DService.AddonLifecycle.UnregisterListener(OnAddon);
        OnAddon(AddonEvent.PreFinalize, null);
        
        AddonActionsPreview.Addon?.Dispose();
        AddonActionsPreview.Addon = null;
        
        AddonShopsPreview.Addon?.Dispose();
        AddonShopsPreview.Addon = null;
        
        CaculationResults.Values.ForEach(x =>
        {
            LinkPayloadManager.Unregister(x.CopyLinkPayload.CommandId);
            LinkPayloadManager.Unregister(x.PreviewLinkPayload.CommandId);
        });
        CaculationResults.Clear();

        if (InstallRaphaelLinkPayload != null)
            LinkPayloadManager.Unregister(InstallRaphaelLinkPayload.CommandId);
        InstallRaphaelTask = null;
    }
    
    private unsafe void OnAddon(AddonEvent type, AddonArgs args)
    {
        switch (type)
        {
            case AddonEvent.PreFinalize:
                RecipeCaculationButton?.DetachNode();
                RecipeCaculationButton = null;
                
                SwitchJobButton?.DetachNode();
                SwitchJobButton = null;
                
                DisplayOthersButton?.DetachNode();
                DisplayOthersButton = null;
                
                DisplayOthersIconsLayout?.DetachNode();
                DisplayOthersIconsLayout = null;
                
                DisplayOthersJobButtons.ForEach(x => x?.DetachNode());
                DisplayOthersJobButtons.Clear();
                
                ClearSearchButton?.DetachNode();
                ClearSearchButton = null;
                
                GetShopInfoButtons.ForEach(x => x?.DetachNode());
                GetShopInfoButtons.Clear();
                
                LevelRecipeButton?.DetachNode();
                LevelRecipeButton = null;
                
                SpecialRecipeButton?.DetachNode();
                SpecialRecipeButton = null;
                
                MasterRecipeButton?.DetachNode();
                MasterRecipeButton = null;

                if (InfosOm.RecipeNote != null)
                {
                    var resNode0 = InfosOm.RecipeNote->GetNodeById(95);
                    if (resNode0 != null)
                        resNode0->SetXFloat(46);

                    var resNode1 = InfosOm.RecipeNote->GetNodeById(88);
                    if (resNode1 != null)
                        resNode1->SetXFloat(0);
                    
                    var resNode2 = InfosOm.RecipeNote->GetNodeById(84);
                    if (resNode2 != null)
                        resNode2->SetXFloat(0);
                }
                break;
            case AddonEvent.PostSetup:
                if (AddonActionsPreview.Addon?.Nodes is not { Count: > 0 } nodes) return;
                    nodes.ForEach(x => x.Alpha = 1);
                break;
            case AddonEvent.PostRequestedUpdate:
                try
                {
                    if (!AgentRecipeNote.Instance()->RecipeSearchOpen)
                        LastRecipeID = GetCurrentRecipeID();
                    
                    UpdateRecipeAddonButton();
                }
                catch
                {
                    // ignored
                }
                break;
            case AddonEvent.PostDraw:
                if (InfosOm.RecipeNote == null) return;

                if (RecipeCaculationButton == null)
                {
                    RecipeCaculationButton = new()
                    {
                        Position = new(228, 490),
                        Size     = new(140, 32),
                        SeString = GetLoc("OptimizedRecipeNote-Button-CaculateRecipe"),
                    };
                    RecipeCaculationButton.OnClick = () =>
                    {
                        if (!IsPluginEnabled(RaphaelIPC.InternalName))
                        {
                            PrintInstallRaphaelPluginMessage();
                            return;
                        }

                        var recipeID = GetCurrentRecipeID();
                        if (recipeID == 0 || !LuminaGetter.TryGetRow(recipeID, out Recipe recipe)) return;

                        // 职业不对
                        if (recipe.CraftType.RowId != LocalPlayerState.ClassJob - 8) return;
                        
                        var craftPoint    = PlayerState.Instance()->GetAttributeByIndex(PlayerAttribute.CraftingPoints);
                        var craftsmanship = PlayerState.Instance()->GetAttributeByIndex(PlayerAttribute.Craftsmanship);
                        var control       = PlayerState.Instance()->GetAttributeByIndex(PlayerAttribute.Control);
                        var id            = RaphaelIPC.StartCalculation();
                        RecipeCaculationButton.IsEnabled = false;
                        TaskHelper.Enqueue(() =>
                        {
                            var response = RaphaelIPC.GetCalculationStatus(id);
                            switch (response.Status)
                            {
                                case RaphaelCalculationStatus.Success:
                                    RecipeCaculationButton.IsEnabled = true;
                                    
                                    var copyLinkPayload    = LinkPayloadManager.Register(OnClickCopyPayload,    out _);
                                    var previewLinkPayload = LinkPayloadManager.Register(OnClickPreviewPayload, out _);
                                    CaculationResults[id] = new(response.Actions,
                                                                recipeID,
                                                                craftPoint,
                                                                craftsmanship,
                                                                control,
                                                                copyLinkPayload,
                                                                previewLinkPayload);
                                    PrintActionsMessage(CaculationResults[id]);
                                    return true;
                                case RaphaelCalculationStatus.Failed:
                                    RecipeCaculationButton.IsEnabled = true;
                                    TaskHelper.Abort();
                                    return true;
                                default:
                                    return false;
                            }
                        }, "请求技能数据");
                    };
                    
                    RecipeCaculationButton.AttachNode(InfosOm.RecipeNote->GetNodeById(57));
                }

                if (SwitchJobButton == null)
                {
                    SwitchJobButton = new()
                    {
                        Position = new(228, 490),
                        Size     = new(140, 32),
                        SeString = GetLoc("OptimizedRecipeNote-Button-SwitchJob"),
                    };
                    SwitchJobButton.OnClick = () =>
                    {
                        if (!IsPluginEnabled(RaphaelIPC.InternalName))
                        {
                            PrintInstallRaphaelPluginMessage();
                            return;
                        }

                        var recipeID = GetCurrentRecipeID();
                        if (recipeID == 0 || !LuminaGetter.TryGetRow(recipeID, out Recipe recipe)) return;
                        // 职业对了
                        if (recipe.CraftType.RowId == LocalPlayerState.ClassJob - 8) return;

                        // 能直接切换
                        if (!DService.Condition[ConditionFlag.PreparingToCraft])
                        {
                            LocalPlayerState.SwitchGearset(recipe.CraftType.RowId + 8);
                            return;
                        }
                        
                        TaskHelper.Enqueue(() => AgentRecipeNote.Instance()->Hide());
                        TaskHelper.Enqueue(() => !DService.Condition[ConditionFlag.PreparingToCraft]);
                        TaskHelper.Enqueue(() => LocalPlayerState.SwitchGearset(recipe.CraftType.RowId + 8));
                        TaskHelper.Enqueue(() => AgentRecipeNote.Instance()->OpenRecipeByRecipeId(recipeID));
                    };
                    
                    SwitchJobButton.AttachNode(InfosOm.RecipeNote->GetNodeById(57));
                }

                if (DisplayOthersButton == null)
                {
                    DisplayOthersButton = new()
                    {
                        Position  = new(0, -32),
                        Size      = new(140, 32),
                        SeString  = GetLoc("OptimizedRecipeNote-Button-ShowOtherRecipes"),
                        IsVisible = true,
                        OnClick = () =>
                        {
                            if (!IsPluginEnabled(RaphaelIPC.InternalName))
                            {
                                PrintInstallRaphaelPluginMessage();
                                return;
                            }

                            var recipeID = GetCurrentRecipeID();
                            if (recipeID == 0                                            ||
                                !LuminaGetter.TryGetRow(recipeID, out Recipe recipe)     ||
                                recipe.ItemResult.Value is not { RowId: > 0 } resultItem ||
                                !SameItemRecipes.TryGetValue(resultItem.RowId, out _))
                                return;

                            AgentRecipeNote.Instance()->SearchRecipeByItemId(resultItem.RowId);
                        }
                    };

                    var labelNode = DisplayOthersButton.LabelNode;
                    while (labelNode.FontSize > 1 && labelNode.GetTextDrawSize(labelNode.SeString).X > labelNode.Size.X)
                        labelNode.FontSize--;
                    
                    DisplayOthersButton.AttachNode(InfosOm.RecipeNote->GetNodeById(57));
                }

                if (DisplayOthersIconsLayout == null)
                {
                    DisplayOthersIconsLayout = new()
                    {
                        IsVisible   = true,
                        ItemSpacing = 5,
                        Size        = new(220, 24),
                        Position    = new(145, -28),
                    };

                    for (var i = 0U; i < 8; i++)
                    {
                        var iconButtonNode = new IconButtonNode
                        {
                            IconId    = 62008 + i,
                            Size      = new(24),
                            IsVisible = true,
                        };
                        
                        var iconNode = new IconImageNode
                        {
                            IconId    = 62008 + i,
                            Size      = new(24),
                            IsVisible = true,
                            ImageNodeFlags = ImageNodeFlags.AutoFit
                        };

                        iconNode.AddTimeline(new TimelineBuilder()
                                             .AddFrameSetWithFrame(1,  10, 1,  position: Vector2.Zero, alpha: 255, multiplyColor: new(100.0f))
                                             .AddFrameSetWithFrame(11, 17, 11, position: Vector2.Zero, alpha: 255, multiplyColor: new(100.0f))
                                             .AddFrameSetWithFrame(18, 26, 18, position: Vector2.Zero + new Vector2(0.0f, 1.0f), alpha: 255,
                                                                   multiplyColor: new(100.0f))
                                             .AddFrameSetWithFrame(27, 36, 27, position: Vector2.Zero, alpha: 153, multiplyColor: new(80.0f))
                                             .AddFrameSetWithFrame(37, 46, 37, position: Vector2.Zero, alpha: 255, multiplyColor: new(100.0f))
                                             .AddFrameSetWithFrame(47, 53, 47, position: Vector2.Zero, alpha: 255, multiplyColor: new(100.0f))
                                             .Build());
                        
                        iconButtonNode.BackgroundNode.IsVisible = false;
                        iconButtonNode.ImageNode.IsVisible      = false;
                        iconNode.AttachNode(iconButtonNode);
                        
                        DisplayOthersJobButtons.Add(iconButtonNode);
                        DisplayOthersIconsLayout.AddNode(iconButtonNode);
                    }
                    
                    DisplayOthersIconsLayout.AttachNode(InfosOm.RecipeNote->GetNodeById(57));
                }

                if (ClearSearchButton == null)
                {
                    ClearSearchButton = new()
                    {
                        IsVisible          = true,
                        Position           = new(130, 25),
                        Size               = new(28),
                        TexturePath        = "ui/uld/WindowA_Button_hr1.tex",
                        TextureCoordinates = Vector2.Zero,
                        TextureSize        = new(28),
                        OnClick = () =>
                        {
                            if (LastRecipeID == 0) return;

                            var agent = AgentRecipeNote.Instance();
                            if (!agent->RecipeSearchOpen) return;
                            
                            agent->OpenRecipeByRecipeId(LastRecipeID);
                        }
                    };
                    
                    ClearSearchButton.AttachNode(InfosOm.RecipeNote->GetNodeById(24));
                }

                if (GetShopInfoButtons.Count == 0)
                {
                    for (var i = 0U; i < 6; i++)
                    {
                        var componentNode = InfosOm.RecipeNote->GetComponentNodeById(89 + i);

                        var index = i;
                        var buttonNode = new IconButtonNode
                        {
                            IconId    = 60412,
                            Size      = new(32),
                            IsVisible = true,
                            Position  = new(-26, 8f),
                            OnClick = () =>
                            {
                                if (!IsPluginEnabled(RaphaelIPC.InternalName))
                                {
                                    PrintInstallRaphaelPluginMessage();
                                    return;
                                }

                                var recipeID = GetCurrentRecipeID();
                                if (recipeID == 0                                        ||
                                    !LuminaGetter.TryGetRow(recipeID, out Recipe recipe) ||
                                    !recipe.Ingredient[(int)index].IsValid)
                                    return;

                                var item     = recipe.Ingredient[(int)index].Value;
                                var itemInfo = ItemShopInfo.GetItemInfo(item.RowId);

                                // 既能 NPC 买到又能市场布告板
                                if (item.ItemSearchCategory.RowId > 0 && itemInfo != null)
                                {
                                    if (!IsConflictKeyPressed())
                                        AddonShopsPreview.OpenWithData(itemInfo);
                                    else
                                        ChatManager.SendMessage($"/pdr market {item.Name}");
                                }
                                else if (itemInfo != null)
                                    AddonShopsPreview.OpenWithData(itemInfo);
                                else if (item.ItemSearchCategory.RowId > 0)
                                    ChatManager.SendMessage($"/pdr market {item.Name}");
                            }
                        };
                        
                        var backgroundNode = (SimpleNineGridNode)buttonNode.BackgroundNode;

                        backgroundNode.TexturePath        = "ui/uld/partyfinder_hr1.tex";
                        backgroundNode.TextureCoordinates = new(38, 2);
                        backgroundNode.TextureSize        = new(32, 34);
                        backgroundNode.LeftOffset         = 0;
                        backgroundNode.RightOffset        = 0;
                        
                        GetShopInfoButtons.Add(buttonNode);
                        buttonNode.AttachNode(componentNode);
                    }
                }

                if (LevelRecipeButton == null)
                {
                    LevelRecipeButton = new()
                    {
                        IsVisible = true,
                        Position  = new(0, 32),
                        Size      = new(58, 38),
                        Tooltip = LuminaWrapper.GetAddonText(1710),
                        OnClick = () =>
                        {
                            AgentRecipeNote.Instance()->SelectedRecipeCategoryPage = 2;
                            var button = InfosOm.RecipeNote->GetComponentButtonById(35);
                            if (button != null)
                            {
                                DService.Framework.Run(() => button->ClickAddonButton(InfosOm.RecipeNote));
                                DService.Framework.Run(() => button->ClickAddonButton(InfosOm.RecipeNote));
                                DService.Framework.Run(() => button->ClickAddonButton(InfosOm.RecipeNote));
                                DService.Framework.Run(() => button->ClickAddonButton(InfosOm.RecipeNote));
                            }
                        }
                    };
                    LevelRecipeButton.BackgroundNode.IsVisible = false;
                    LevelRecipeButton.AttachNode(InfosOm.RecipeNote->GetNodeById(32));
                }
                
                if (SpecialRecipeButton == null)
                {
                    SpecialRecipeButton = new()
                    {
                        IsVisible = true,
                        Position  = new(50, 32),
                        Size      = new(58, 38),
                        Tooltip   = LuminaWrapper.GetAddonText(1711),
                        OnClick = () =>
                        {
                            AgentRecipeNote.Instance()->SelectedRecipeCategoryPage = 0;
                            var button = InfosOm.RecipeNote->GetComponentButtonById(35);
                            if (button != null)
                            {
                                DService.Framework.Run(() => button->ClickAddonButton(InfosOm.RecipeNote));
                                DService.Framework.Run(() => button->ClickAddonButton(InfosOm.RecipeNote));
                                DService.Framework.Run(() => button->ClickAddonButton(InfosOm.RecipeNote));
                                DService.Framework.Run(() => button->ClickAddonButton(InfosOm.RecipeNote));
                            }
                        }
                    };
                    SpecialRecipeButton.BackgroundNode.IsVisible = false;
                    SpecialRecipeButton.AttachNode(InfosOm.RecipeNote->GetNodeById(32));
                }
                
                if (MasterRecipeButton == null)
                {
                    MasterRecipeButton = new()
                    {
                        IsVisible = true,
                        Position  = new(102, 32),
                        Size      = new(58, 38),
                        Tooltip   = LuminaWrapper.GetAddonText(14212),
                        OnClick = () =>
                        {
                            AgentRecipeNote.Instance()->SelectedRecipeCategoryPage = 1;
                            var button = InfosOm.RecipeNote->GetComponentButtonById(35);
                            if (button != null)
                            {
                                DService.Framework.Run(() => button->ClickAddonButton(InfosOm.RecipeNote));
                                DService.Framework.Run(() => button->ClickAddonButton(InfosOm.RecipeNote));
                                DService.Framework.Run(() => button->ClickAddonButton(InfosOm.RecipeNote));
                                DService.Framework.Run(() => button->ClickAddonButton(InfosOm.RecipeNote));
                            }
                        }
                    };
                    MasterRecipeButton.BackgroundNode.IsVisible = false;
                    MasterRecipeButton.AttachNode(InfosOm.RecipeNote->GetNodeById(32));
                }

                if (Throttler.Throttle("OptimizedRecipeNote-UpdateAddon", 1000))
                    UpdateRecipeAddonButton();
                
                break;
        }
    }

    private static void OnClickInstallRaphaelPayload(uint id, SeString _)
    {
        if (DService.PI.InstalledPlugins.Any(x => x.InternalName == "Raphael.Dalamud"))
        {
            ChatManager.SendMessage("/xlenableplugin Raphael.Dalamud");
            return;
        }
        
        if (InstallRaphaelTask != null) return;

        InstallRaphaelTask = DService.Framework
                                     .RunOnTick(async () => await AddPlugin("https://raw.githubusercontent.com/AtmoOmen/DalamudPlugins/main/pluginmaster.json",
                                                                            "Raphael.Dalamud"))
                                     .ContinueWith(_ => InstallRaphaelTask = null);
    }

    private void OnClickPreviewPayload(uint id, SeString _)
    {
        if (CaculationResults.FirstOrDefault(x => x.Value.PreviewLinkPayload.CommandId == id) is not { Value.RecipeID: > 0 } result) return;
        
        AddonActionsPreview.OpenWithActions(TaskHelper, result.Value);
    }

    private static void OnClickCopyPayload(uint id, SeString _)
    {
        if (CaculationResults.FirstOrDefault(x => x.Value.CopyLinkPayload.CommandId == id) is not { Value.RecipeID: > 0 } result) return;

        var builder = new StringBuilder();
        foreach (var action in result.Value.Actions)
            builder.AppendLine($"/ac {LuminaWrapper.GetActionName(action)} <wait.3>");
        ImGui.SetClipboardText(builder.ToString());
        
        NotificationSuccess($"{GetLoc("CopiedToClipboard")}");
    }

    private static unsafe void UpdateRecipeAddonButton()
    {
        if (InfosOm.RecipeNote == null) return;
        if (!IsPluginEnabled(RaphaelIPC.InternalName)) return;
        
        ClearSearchButton.IsVisible = AgentRecipeNote.Instance()->RecipeSearchOpen && LastRecipeID != 0;
        
        var recipeID = GetCurrentRecipeID();
        if (recipeID == 0 || !LuminaGetter.TryGetRow(recipeID, out Recipe recipe))
        {
            RecipeCaculationButton.IsVisible   = false;
            SwitchJobButton.IsVisible          = false;
            DisplayOthersButton.IsVisible      = false;
            DisplayOthersIconsLayout.IsVisible = false;
            return;
        }

        var maxIngredientAmount = 0;
        for (var i = 0; i < recipe.Ingredient.Count; i++)
        {
            if (recipe.Ingredient[i] is not { IsValid:true, RowId: > 100 }) continue;
            if (recipe.AmountIngredient[i] is var amount && amount > maxIngredientAmount)
                maxIngredientAmount = amount;
        }
        
        var appendOffset = maxIngredientAmount >= 10 ? 30 : 10;
        
        var resNode0 = InfosOm.RecipeNote->GetNodeById(95);
        if (resNode0 != null)
            resNode0->SetXFloat(46 + appendOffset);

        var resNode1 = InfosOm.RecipeNote->GetNodeById(88);
        if (resNode1 != null)
            resNode1->SetXFloat(0 + appendOffset);
                    
        var resNode2 = InfosOm.RecipeNote->GetNodeById(84);
        if (resNode2 != null)
            resNode2->SetXFloat(0 + appendOffset);
        
        var resNodeProgress = InfosOm.RecipeNote->GetNodeById(2);
        if (resNodeProgress != null)
            resNodeProgress->SetAlpha(0);
        
        for (var d = 0; d < GetShopInfoButtons.Count; d++)
        {
            if (!recipe.Ingredient[d].IsValid) break;
            
            var item     = recipe.Ingredient[d].Value;
            var itemInfo = ItemShopInfo.GetItemInfo(item.RowId);

            var button     = GetShopInfoButtons[d];
            var sourceText = string.Empty;

            button.X = -6 + (maxIngredientAmount >= 10 ? -20 : 0);
            
            // 既能 NPC 买到又能市场布告板
            if (item.ItemSearchCategory.RowId > 0 && itemInfo != null)
            {
                button.IconId = 60412;
                sourceText    = $"{LuminaWrapper.GetAddonText(350)} / {LuminaWrapper.GetAddonText(548)} [{GetLoc("ConflictKey")}]";
            }
            else if (itemInfo != null)
            {
                button.IconId = 60412;
                sourceText = $"{LuminaWrapper.GetAddonText(350)}";
            }
            else if (item.ItemSearchCategory.RowId > 0)
            {
                button.IconId = 60570;
                sourceText = $"{LuminaWrapper.GetAddonText(548)}";
            }
            else
                sourceText = string.Empty;
            
            if (sourceText == string.Empty)
                button.IsVisible = false;
            else
            {
                button.IsVisible = true;
                button.Tooltip   = sourceText;
            }
        }

        if (SameItemRecipes.TryGetValue(recipe.ItemResult.RowId, out var allRecipes))
        {
            DisplayOthersButton.IsVisible      = true;
            DisplayOthersIconsLayout.IsVisible = true;
            
            var allCraftTypes = allRecipes.ToDictionary(x => x.CraftType.RowId, x => x.RowId);
            for (var i = 0U; i < 8; i++)
            {
                var node = DisplayOthersJobButtons[(int)i];
                if (allCraftTypes.TryGetValue(i, out var otherRecipeID))
                {
                    node.Alpha     = 1;
                    node.IsEnabled = true;
                    node.OnClick = () =>
                    {
                        var agent = AgentRecipeNote.Instance();
                        if (GetCurrentRecipeID() == otherRecipeID) return;

                        agent->OpenRecipeByRecipeId(otherRecipeID);
                    };
                }
                else
                {
                    node.Alpha     = 0.2f;
                    node.IsEnabled = false;
                    node.OnClick = () => { };
                }
            }
        }
        else
        {
            DisplayOthersButton.IsVisible      = false;
            DisplayOthersIconsLayout.IsVisible = false;
        }

        if (recipe.CraftType.RowId != LocalPlayerState.ClassJob - 8)
        {
            RecipeCaculationButton.IsVisible = false;
            SwitchJobButton.IsVisible        = true;

            for (var i = 102U; i < 105; i++)
            {
                var buttonNode = InfosOm.RecipeNote->GetComponentButtonById(i);
                if (buttonNode != null)
                    buttonNode->SetEnabledState(false);
            }
        }
        else
        {
            RecipeCaculationButton.IsVisible = true;
            SwitchJobButton.IsVisible        = false;
            
            for (var i = 102U; i < 105; i++)
            {
                var buttonNode = InfosOm.RecipeNote->GetComponentButtonById(i);
                if (buttonNode != null)
                    buttonNode->SetEnabledState(true);
            }
        }
    }

    private static void PrintActionsMessage(CaculationResult result)
    {
        var builder = new SeStringBuilder();
        builder.AddText($"{GetLoc("OptimizedRecipeNote-Message-CaculationResult")}")
               .Add(NewLinePayload.Payload)
               .AddText($"{GetLoc("Recipe")}: ")
               .AddItemLink(result.GetRecipe().ItemResult.RowId)
               .AddText(" (")
               .AddIcon(result.GetJob().ToBitmapFontIcon())
               .AddText(result.GetJob().Name.ExtractText())
               .AddText(")")
               .Add(NewLinePayload.Payload)
               .AddText($"{GetLoc("Step")}: ")
               .AddText($"{GetLoc("OptimizedRecipeNote-Message-StepsInfo", result.Actions.Count, result.Actions.Count * 3)}")
               .Add(NewLinePayload.Payload)
               .AddText($"{GetLoc("Operation")}: ")
               .Add(RawPayload.LinkTerminator)
               .Add(result.CopyLinkPayload)
               .AddText("[")
               .AddUiForeground(35)
               .AddText($"{GetLoc("Copy")}")
               .AddUiForegroundOff()
               .AddText("]")
               .Add(RawPayload.LinkTerminator)
               .AddText(" / ")
               .Add(RawPayload.LinkTerminator)
               .Add(result.PreviewLinkPayload)
               .AddText("[")
               .AddUiForeground(35)
               .AddText($"{GetLoc("Preview")}")
               .AddUiForegroundOff()
               .AddText("]")
               .Add(RawPayload.LinkTerminator);
        Chat(builder.Build());
    }

    private static void PrintInstallRaphaelPluginMessage()
    {
        InstallRaphaelLinkPayload ??= LinkPayloadManager.Register(OnClickInstallRaphaelPayload, out _);
                            
        var message = new SeStringBuilder().AddIcon(BitmapFontIcon.Warning)
                                           .AddText($" {GetLoc("OptimizedRecipeNote-Message-InstallRapheal")}")
                                           .Add(NewLinePayload.Payload)
                                           .AddText($"{GetLoc("Operation")}: ")
                                           .Add(RawPayload.LinkTerminator)
                                           .Add(InstallRaphaelLinkPayload)
                                           .AddText("[")
                                           .AddUiForeground(35)
                                           .AddText($"{GetLoc("Enable")} / {GetLoc("Install")}")
                                           .AddUiForegroundOff()
                                           .AddText("]")
                                           .Add(RawPayload.LinkTerminator)
                                           .Build();
        Chat(message);
    }

    private static unsafe uint GetCurrentRecipeID()
    {
        var data = UIState.Instance()->RecipeNote.RecipeList->SelectedRecipe;
        if (data == null) return 0;

        return data->RecipeId;
    }

    private class AddonActionsPreview(TaskHelper taskHelper, CaculationResult result) : NativeAddon
    {
        public static AddonActionsPreview? Addon  { get; set; }
        public        CaculationResult     Result { get; private set; } = result;
        public        List<DragDropNode>   Nodes  { get; set; }         = [];
        
        public WeakReference<TaskHelper> TaskHelper { get; private set; } = new(taskHelper);
        
        public TextButtonNode ExecuteButton { get; private set; }

        private static Task? OpenAddonTask;
        
        public static void OpenWithActions(TaskHelper taskHelper, CaculationResult result)
        {
            if (OpenAddonTask != null) return;
            
            var isAddonExisted = Addon?.IsOpen ?? false;
            if (Addon != null)
            {
                Addon.Dispose();
                Addon = null;
            }

            OpenAddonTask = DService.Framework.RunOnTick(() =>
            {
                var rowCount = MathF.Ceiling(result.Actions.Count / 10f);
                Addon ??= new(taskHelper, result)
                {
                    InternalName = "DRRecipeNoteActionsPreview",
                    Title        = $"{GetLoc("OptimizedRecipeNote-AddonTitle")}",
                    Subtitle     = $"{GetLoc("OptimizedRecipeNote-Message-StepsInfo", result.Actions.Count, result.Actions.Count * 3)}",
                    Size         = new(500f, 160f + (50f                                                                         * (rowCount - 1))),
                };
                Addon.Open();
            }, TimeSpan.FromMilliseconds(isAddonExisted ? 500 : 0)).ContinueWith(_ => OpenAddonTask = null);
        }
        
        protected override unsafe void OnSetup(AtkUnitBase* addon)
        {
            if (Result.Actions.Count == 0) return;

            var statsRow = new HorizontalListNode()
            {
                IsVisible = true,
                Position  = new(12, 40),
                Size      = new(0, 44)
            };

            var jobTextNode = new TextNode
            {
                IsVisible = true,
                TextFlags = TextFlags.AutoAdjustNodeSize,
                SeString = new SeStringBuilder()
                           .AddText($"{LuminaWrapper.GetAddonText(294)}: ")
                           .AddIcon(Result.GetJob().ToBitmapFontIcon())
                           .AddText(Result.GetJob().Name.ExtractText())
                           .Build()
                           .Encode()
            };
            jobTextNode.Size =  jobTextNode.GetTextDrawSize($"{jobTextNode.SeString}123");
            statsRow.Width   += jobTextNode.Width;
            statsRow.AddNode(jobTextNode);

            var craftmanshipTextNode = new TextNode
            {
                IsVisible = true,
                TextFlags = TextFlags.AutoAdjustNodeSize,
                SeString  = $"{LuminaWrapper.GetAddonText(3261)}: {Result.Craftmanship}"
            };
            statsRow.Width += craftmanshipTextNode.Width;
            statsRow.AddNode(craftmanshipTextNode);
            
            statsRow.Width += 4;
            statsRow.AddDummy(4);
            
            var controlTextNode = new TextNode
            {
                IsVisible = true,
                TextFlags = TextFlags.AutoAdjustNodeSize,
                SeString  = $"{LuminaWrapper.GetAddonText(3262)}: {Result.Control}"
            };
            statsRow.Width       += controlTextNode.Width;
            statsRow.AddNode(controlTextNode);
            
            statsRow.Width += 4;
            statsRow.AddDummy(4);
            
            var craftPointTextNode = new TextNode
            {
                IsVisible = true,
                TextFlags = TextFlags.AutoAdjustNodeSize,
                SeString  = $"{LuminaWrapper.GetAddonText(3223)}: {Result.CraftPoint}"
            };
            statsRow.Width          += craftPointTextNode.Width;
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
                SeString  = GetLoc("Execute"),
                OnClick = () =>
                {
                    if (Synthesis == null) return;
                    if (Result.Actions is not { Count: > 0 } actions) return;
                    if (!TaskHelper.TryGetTarget(out var taskHelper)) return;

                    for (var index = 0; index < actions.Count; index++)
                    {
                        var x = actions[index];
                        var i = index;
                        taskHelper.Enqueue(() =>
                        {
                            if (DService.Condition[ConditionFlag.ExecutingCraftingAction]) return true;

                            ChatManager.SendMessage($"/ac {LuminaWrapper.GetActionName(x)}");
                            return false;
                        });
                        taskHelper.Enqueue(() => Nodes[i].Alpha = 0.2f);
                        taskHelper.Enqueue(() => !DService.Condition[ConditionFlag.ExecutingCraftingAction]);
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
                    SeString  = GetLoc("OptimizedRecipeNote-Button-CopyMacro", macroIndex + 1),
                    OnClick = () =>
                    {
                        var startIndex = macroIndex * 15;
                        var endIndex   = Math.Min(startIndex + 15, Result.Actions.Count);
                        var actionsForMacro = Result.Actions.Skip(startIndex).Take(endIndex - startIndex);
                        
                        var builder = new StringBuilder();
                        foreach (var action in actionsForMacro)
                            builder.AppendLine($"/ac {LuminaWrapper.GetActionName(action)} <wait.3>");
                        ImGui.SetClipboardText(builder.ToString());
                        
                        NotificationSuccess($"{GetLoc("CopiedToClipboard")}");
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
                        Int2 = (int)actionID,
                    },
                    OnRollOver = node =>
                        node.ShowTooltip(AtkTooltipManager.AtkTooltipType.Action, actionID > 10_0000 ? ActionKind.CraftingAction : ActionKind.Action),
                    OnRollOut = node => node.HideTooltip(),
                };
                dragDropNode.OnClicked = _ =>
                {
                    if (DService.Condition[ConditionFlag.ExecutingCraftingAction] ||
                        (TaskHelper.TryGetTarget(out var taskHelper) && taskHelper.IsBusy))
                        return;

                    if (Synthesis != null)
                        dragDropNode.Alpha = 0.2f;
                    ChatManager.SendMessage($"/ac {LuminaWrapper.GetActionName(actionID)}");
                };
                Nodes.Add(dragDropNode);

                var actionIndexNode = new TextNode
                {
                    IsVisible        = true,
                    Position         = new(-4),
                    SeString         = $"{index + 1}",
                    FontType         = FontType.MiedingerMed,
                    TextFlags        = TextFlags.Edge,
                    TextOutlineColor = KnownColor.OrangeRed.ToVector4()
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
            if (DService.KeyState[VirtualKey.ESCAPE])
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

    private class AddonShopsPreview : NativeAddon
    {
        public static AddonShopsPreview? Addon  { get; set; }
        
        private static Task? OpenAddonTask;
        
        public static void OpenWithData(ItemShopInfo shopInfo)
        {
            if (shopInfo is not { NPCInfos.Count: > 0 }) return;
            if (OpenAddonTask != null) return;
            
            var isAddonExisted = Addon?.IsOpen ?? false;
            if (Addon != null)
            {
                Addon.Dispose();
                Addon = null;
            }

            OpenAddonTask = DService.Framework.RunOnTick(() =>
            {
                Addon ??= new(shopInfo)
                {
                    InternalName = "DRRecipeNoteShopsPreview",
                    Title        = GetLoc("OptimizedRecipeNote-ShopList"),
                    Size         = new(550f, 400f),
                };
                Addon.Open();
            }, TimeSpan.FromMilliseconds(isAddonExisted ? 500 : 0)).ContinueWith(_ => OpenAddonTask = null);
        }
        
        public ItemShopInfo ShopInfo { get; set; }
        
        private AddonShopsPreview(ItemShopInfo shopInfo) => 
            ShopInfo = shopInfo;
        
        protected override unsafe void OnSetup(AtkUnitBase* addon)
        {
            var itemInfoRow = new HorizontalListNode
            {
                IsVisible   = true,
                Size        = new Vector2(ContentSize.X - 5, 48),
                Position    = ContentStartPosition + new Vector2(5, 0),
                ItemSpacing = 5
            };
            itemInfoRow.AttachNode(this);

            var itemIconNode = new IconImageNode
            {
                IsVisible      = true,
                Size           = new(48),
                IconId         = LuminaWrapper.GetItemIconID(ShopInfo.ItemID),
                ImageNodeFlags = ImageNodeFlags.AutoFit
            };
            itemInfoRow.AddNode(itemIconNode);

            var itemNameNode = new TextNode
            {
                IsVisible = true,
                TextFlags = TextFlags.AutoAdjustNodeSize,
                SeString  = LuminaWrapper.GetItemName(ShopInfo.ItemID),
                FontSize  = 18,
                Position  = new(0, 6)
            };
            itemInfoRow.AddNode(itemNameNode);

            if (ShopInfo.GetItem().ItemSearchCategory.RowId > 0)
            {
                var marketButtonNode = new IconButtonNode
                {
                    IconId    = 60570,
                    Tooltip   = LuminaWrapper.GetAddonText(548),
                    Size      = new(32),
                    Position  = itemInfoRow.Position + new Vector2(itemNameNode.GetTextDrawSize(itemNameNode.SeString).X + itemIconNode.Size.X + 15f, 2),
                    IsVisible = true,
                    OnClick   = () => ChatManager.SendMessage($"/pdr market {ShopInfo.GetItem().Name}")
                };
                marketButtonNode.AttachNode(this);
            }

            var scrollingAreaNode = new ScrollingAreaNode<VerticalListNode>
            {
                Position          = ContentStartPosition + new Vector2(5, 48),
                Size              = ContentSize          - new Vector2(5, 48),
                ContentHeight     = ShopInfo.NPCInfos.Count(x => x.Location != null) * 33,
                ScrollSpeed       = 100,
                IsVisible         = true,
                AutoHideScrollBar = true,
            };
            scrollingAreaNode.AttachNode(this);

            var contentNode = scrollingAreaNode.ContentNode;
            contentNode.ItemSpacing = 3;
            
            contentNode.AddDummy(5);

            var testTextNode = new TextNode();

            var longestLocationText = ShopInfo.NPCInfos
                                              .Where(x => x.Location != null)
                                              .Select(x => x.Location.GetTerritory().ExtractPlaceName())
                                              .MaxBy(x => x.Length);
            var locationColumnWidth = testTextNode.GetTextDrawSize(longestLocationText).X + 40f;
            
            var longestNameText = ShopInfo.NPCInfos
                                          .Select(x => x.Name)
                                          .MaxBy(x => x.Length);
            var nameColumnWidth = testTextNode.GetTextDrawSize(longestNameText).X + 5f;
            
            foreach (var npcInfo in ShopInfo.NPCInfos.OrderBy(x => x.Location?.TerritoryID == 282))
            {
                if (npcInfo.Location == null)
                    continue;
                
                var row = new ResNode
                {
                    IsVisible   = true,
                    Size        = new(contentNode.Width, 30),
                };
                contentNode.AddNode(row);

                var npcNameNode = new TextNode
                {
                    IsVisible = true,
                    SeString  = npcInfo.Name,
                    Position  = new(0, 4)
                };
                npcNameNode.AttachNode(row);

                var locationName = npcInfo.Location.TerritoryID == 282 ? LuminaWrapper.GetAddonText(8495) : npcInfo.Location.GetTerritory().ExtractPlaceName();
                var npcLocationNode = new TextButtonNode
                {
                    IsVisible = true,
                    SeString  = locationName,
                    Size      = new(locationColumnWidth, 28f),
                    Position  = new(nameColumnWidth, 0),
                    IsEnabled = npcInfo.Location.TerritoryID != 282,
                    Tooltip   = $"{GetLoc("Mark")} / {LuminaWrapper.GetAddonText(168)} [{GetLoc("ConflictKey")}]",
                    OnClick = () =>
                    {
                        var pos = MapToWorld(new(npcInfo.Location.MapPosition.X, npcInfo.Location.MapPosition.Y), npcInfo.Location.GetMap()).ToVector3(0);

                        var instance = AgentMap.Instance();
                        instance->SetFlagMapMarker(npcInfo.Location.TerritoryID, npcInfo.Location.MapID, pos);

                        if (!IsConflictKeyPressed())
                            instance->OpenMap(npcInfo.Location.MapID, npcInfo.Location.TerritoryID, npcInfo.Name);
                        else
                        {
                            var aetheryte = MovementManager.GetNearestAetheryte(pos, npcInfo.Location.TerritoryID);
                            if (aetheryte != null)
                                ChatManager.SendMessage($"/pdrtelepo {aetheryte.Name}");
                        }
                    }
                };
                npcLocationNode.AttachNode(row);

                var costInfoComponent = new HorizontalListNode
                {
                    IsVisible   = true,
                    Position    = new(nameColumnWidth + locationColumnWidth + 10f, 4),
                    Size        = new(100, 28),
                    ItemSpacing = 10
                };

                foreach (var costInfo in npcInfo.CostInfos)
                {
                    var costIconNode = new IconImageNode
                    {
                        IsVisible = true,
                        Size      = new(28),
                        IconId    = LuminaWrapper.GetItemIconID(costInfo.ItemID),
                        Position  = new(-10, -4),
                        Tooltip = $"{LuminaWrapper.GetItemName(costInfo.ItemID)}"
                    };
                    costInfoComponent.AddNode(costIconNode);

                    var costNode = new TextNode
                    {
                        IsVisible = true,
                        TextFlags = TextFlags.AutoAdjustNodeSize,
                        SeString  = costInfo.ToString().Replace(LuminaWrapper.GetItemName(costInfo.ItemID), string.Empty).Trim(),
                        Position  = new(4, 6)
                    };
                    costInfoComponent.AddNode(costNode);
                }
                
                costInfoComponent.AttachNode(row);
            }
        }

        protected override unsafe void OnUpdate(AtkUnitBase* addon)
        {
            if (DService.KeyState[VirtualKey.ESCAPE])
            {
                Close();
                
                if (SystemMenu != null)
                    SystemMenu->Close(true);
            }
        }
    }

    private record CaculationResult(
        List<uint>         Actions,
        uint               RecipeID,
        int                CraftPoint,
        int                Craftmanship,
        int                Control,
        DalamudLinkPayload CopyLinkPayload,
        DalamudLinkPayload PreviewLinkPayload)
    {
        public Recipe GetRecipe() =>
            LuminaGetter.GetRow<Recipe>(RecipeID).GetValueOrDefault();

        public ClassJob GetJob() =>
            LuminaGetter.GetRow<ClassJob>(GetRecipe().CraftType.RowId + 8).GetValueOrDefault();
    }
}
