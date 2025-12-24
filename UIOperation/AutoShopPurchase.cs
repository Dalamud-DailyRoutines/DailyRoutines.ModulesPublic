using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DailyRoutines.Abstracts;
using DailyRoutines.Infos;
using DailyRoutines.Managers;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Exception = System.Exception;

namespace DailyRoutines.ModulesPublic;

public class AutoShopPurchase : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title = GetLoc("AutoShopPurchaseTitle"),
        Description = GetLoc("AutoShopPurchaseDescription"),
        Category = ModuleCategories.UIOperation,
    };

    public override ModulePermission Permission { get; } = new() { NeedAuth = true };

    private static Config? ModuleConfig;
    private static ShopPresetDisplayTable? PresetDisplayTable;

    private static List<AddonWithListInfo> ScannedData = [];

    protected override void Init()
    {
        ModuleConfig = LoadConfig<Config>() ?? new();
        PresetDisplayTable ??= new();
    }

    protected override void ConfigUI()
    {
        if (ImGuiOm.ButtonIconWithText(FontAwesomeIcon.FileImport, GetLoc("Import")))
        {
            var config = ImportFromClipboard<ShopPurchasePreset>();
            if (config != null)
            {
                ModuleConfig.Presets.Add(config);
                ModuleConfig.Save(this);
            }
        }

        PresetDisplayTable.Draw();
    }

    protected override void Uninit()
    {
        if (ModuleConfig != null)
            SaveConfig(ModuleConfig);

        PresetDisplayTable?.Dispose();
        PresetDisplayTable = null;

        ShopPresetExecutor.CancelAndDispose();
    }

    private class Config : ModuleConfiguration
    {
        public List<ShopPurchasePreset> Presets = [];
    }

    public unsafe class AddonWithListInfo(string addonName, HashSet<uint> listNodeIDs) : IEquatable<AddonWithListInfo>
    {
        public string        AddonName   { get; } = addonName;
        public HashSet<uint> ListNodeIDs { get; } = listNodeIDs;

        public AtkUnitBase* GetAddon() => GetAddonByName(AddonName);

        public AtkComponentList* GetListByID(uint nodeID)
        {
            if (!ListNodeIDs.Contains(nodeID)) return null;
            var addon = GetAddon();
            if (!IsAddonAndNodesReady(addon)) return null;
            return addon->GetComponentListById(nodeID);
        }

        public bool Equals(AddonWithListInfo? other)
        {
            if (ReferenceEquals(null, other)) return false;
            if (ReferenceEquals(this, other)) return true;
            return AddonName == other.AddonName;
        }

        public override bool Equals(object? obj)
        {
            if (ReferenceEquals(null, obj)) return false;
            if (ReferenceEquals(this, obj)) return true;
            if (obj.GetType() != GetType()) return false;
            return Equals((AddonWithListInfo)obj);
        }

        public override int GetHashCode() => AddonName.GetHashCode();

        public static bool operator ==(AddonWithListInfo left, AddonWithListInfo right) => Equals(left, right);

        public static bool operator !=(AddonWithListInfo left, AddonWithListInfo right) => !Equals(left, right);

        public static List<AddonWithListInfo> ScanAddons(AtkUnitList managerList)
        {
            var list = new HashSet<AddonWithListInfo>();

            var addons = managerList.Entries;
            if (addons.Length == 0 || addons.IsEmpty || addons.Length == 0) return [..list];

            foreach (var entry in addons)
            {
                var addon = entry.Value;
                if (!IsAddonAndNodesReady(addon)) continue;

                var info = new AddonWithListInfo(addon->NameString, []);

                addon->UldManager.SearchComponentNodesByType(ComponentType.List)
                                 .ForEach(x => info.ListNodeIDs.Add(((AtkComponentList*)x)->OwnerNode->NodeId));
                addon->UldManager.SearchComponentNodesByType(ComponentType.TreeList)
                                 .ForEach(x => info.ListNodeIDs.Add(((AtkComponentTreeList*)x)->OwnerNode->NodeId));

                if (info.ListNodeIDs.Count > 0) 
                    list.Add(info);
            }

            return [..list];
        }
    }

    public unsafe class ShopPurchasePreset(string addonName) : IEquatable<ShopPurchasePreset>
    {
        public string                  Name        { get; set; } = string.Empty;
        public string                  AddonName   { get; set; } = addonName;
        public string                  TargetName  { get; set; } = string.Empty;
        public KeyValuePair<uint, int> ClickRoute  { get; set; } // ListComponent Node ID - Index
        public KeyValuePair<bool, int> NumberRoute { get; set; } // IsNeedToSetNumber - Number

        public AtkUnitBase* GetAddon() => GetAddonByName(AddonName);
        public bool IsAddonValid() => IsAddonAndNodesReady(GetAddon());
        public AtkComponentList* GetListNode() => !IsAddonValid() ? null : GetAddon()->GetComponentListById(ClickRoute.Key);
        public AtkComponentNumericInput* GetNumberNode()
        {
            if (!NumberRoute.Key) return null;

            var listNode = GetListNode();
            if (listNode == null || listNode->ListLength < ClickRoute.Value) return null;

            var numberNode =
                listNode->ItemRendererList[ClickRoute.Value].AtkComponentListItemRenderer->UldManager
                    .SearchComponentNodeByType<AtkComponentNumericInput>(ComponentType.NumericInput);

            return numberNode;
        }
        public bool IsNodeValid() => GetListNode() != null && (!NumberRoute.Key || (NumberRoute.Key && GetNumberNode() != null));
        public bool IsTargetValid() => string.IsNullOrWhiteSpace(TargetName) ||
                                       (!string.IsNullOrWhiteSpace(TargetName) &&
                                        (DService.Targets.Target?.Name.TextValue ?? string.Empty) == TargetName);

        public List<Func<bool?>> GetTasks()
        {
            try
            {
                var list = new List<Func<bool?>>();
                if (!IsTargetValid())
                    throw new Exception(GetLoc("AutoShopPurchase-Exception-PresetTargetInvalid"));
                if (!IsAddonValid())
                    throw new Exception(GetLoc("AutoShopPurchase-Exception-PresetAddonInvalid"));
                if (!IsNodeValid())
                    throw new Exception(GetLoc("AutoShopPurchase-Exception-PresetNodeInvalid"));

                if (NumberRoute.Key)
                {
                    list.Add(() =>
                    {
                        var numberNode = GetNumberNode();
                        if (numberNode == null) return false;
                        numberNode->SetValue(NumberRoute.Value);
                        return true;
                    });
                }

                list.Add(() =>
                {
                    var listNode = GetListNode();
                    if (listNode == null) return false;
                    listNode->DispatchItemEvent(ClickRoute.Value, AtkEventType.ListItemClick);
                    return true;
                });

                return list;
            }
            catch (Exception ex)
            {
                NotificationError($"{GetLoc("Error")}: {ex.Message}");
                return [];
            }
        }

        public bool Equals(ShopPurchasePreset? other)
        {
            if(ReferenceEquals(null, other)) return false;
            if(ReferenceEquals(this, other)) return true;
            return AddonName == other.AddonName && ClickRoute.Equals(other.ClickRoute) && NumberRoute.Equals(other.NumberRoute);
        }

        public override bool Equals(object? obj)
        {
            if(ReferenceEquals(null, obj)) return false;
            if(ReferenceEquals(this, obj)) return true;
            if(obj.GetType() != GetType()) return false;
            return Equals((ShopPurchasePreset)obj);
        }

        public override int GetHashCode() => HashCode.Combine(AddonName, ClickRoute, NumberRoute);

        public override string ToString() 
            => $"{Name}_{AddonName}_Click:{ClickRoute.Key}-{ClickRoute.Value}_Number:{NumberRoute.Key}-{NumberRoute.Value}";
    }

    public class ShopPresetDisplayTable : IDisposable
    {
        private static unsafe AtkUnitList FocusedList => RaptureAtkUnitManager.Instance()->FocusedUnitsList;

        private bool IsAddNewPresetWindowOpen;

        private static string NameInput = string.Empty;
        private static string TargetNameInput = GetLoc("AutoShopPurchase-UI-UnknownTarget");
        private static string AddonNameInput = string.Empty;
        private static uint ListComponentNodeIDInput;
        private static int ClickIndexInput;
        private static bool IsSetNumberInput;
        private static int SetNumberInput;

        public ShopPresetDisplayTable() => 
            WindowManager.Draw += WindowRenderAddNewPreset;

        public void Dispose() => 
            WindowManager.Draw -= WindowRenderAddNewPreset;

        public void Draw()
        {
            var tableSize = ImGui.GetContentRegionAvail() with { Y = 0 };
            using var table = ImRaii.Table("ShopPresetDisplayTable", 7, ImGuiTableFlags.Borders, tableSize);
            if (!table) return;

            TableSetupColumns();
            TableRenderHeaderRow();

            for (var i = 0; i < ModuleConfig.Presets.Count; i++)
                TableRenderRow(i, ModuleConfig.Presets[i]);
        }

        private void TableSetupColumns()
        {
            ImGui.TableSetupColumn("序号", ImGuiTableColumnFlags.WidthFixed, ImGui.CalcTextSize("1234").X);
            ImGui.TableSetupColumn("名称", ImGuiTableColumnFlags.None, 25);
            ImGui.TableSetupColumn("对象", ImGuiTableColumnFlags.None, 20);
            ImGui.TableSetupColumn("界面", ImGuiTableColumnFlags.None, 20);
            ImGui.TableSetupColumn("路径", ImGuiTableColumnFlags.None, 15);
            ImGui.TableSetupColumn("数量", ImGuiTableColumnFlags.WidthFixed, ImGui.CalcTextSize("1234").X);
            ImGui.TableSetupColumn("操作", ImGuiTableColumnFlags.None, 30);
        }

        private void TableRenderHeaderRow()
        {
            ImGui.TableNextRow(ImGuiTableRowFlags.Headers);

            ImGui.TableNextColumn();
            if (ImGuiOm.ButtonIconSelectable("OpenAddNewPresetWindow", FontAwesomeIcon.Plus))
            {
                ScannedData = AddonWithListInfo.ScanAddons(FocusedList);
                IsAddNewPresetWindowOpen ^= true;
            }

            ImGui.TableNextColumn();
            ImGui.Text(GetLoc("Name"));

            ImGui.TableNextColumn();
            ImGui.Text(GetLoc("Target"));

            ImGui.TableNextColumn();
            ImGui.Text(GetLoc("Addon"));

            ImGui.TableNextColumn();
            ImGui.Text(GetLoc("Route"));

            ImGui.TableNextColumn();
            ImGui.Text(GetLoc("Amount"));

            ImGui.TableNextColumn();
        }

        private void TableRenderRow(int counter, ShopPurchasePreset preset)
        {
            using var id = ImRaii.PushId(preset.ToString());
            ImGui.TableNextRow();

            ImGui.TableNextColumn();
            ImGui.Text($"{counter + 1}");

            ImGui.TableNextColumn();
            ImGui.Text($"{preset.Name}");

            ImGui.TableNextColumn();
            ImGui.Text($"{preset.TargetName}");

            ImGui.TableNextColumn();
            ImGui.Text($"{preset.AddonName}");

            ImGui.TableNextColumn();
            ImGui.Text($"{preset.ClickRoute.Key} -> {preset.ClickRoute.Value}");

            ImGui.TableNextColumn();
            ImGui.Text(preset.NumberRoute.Key ? $"{preset.NumberRoute.Value}" : $"({GetLoc("None")})");

            ImGui.TableNextColumn();
            PresetRunTimesInputComponent.Using(preset).Draw();

            ImGui.SameLine();
            if (ImGuiOm.ButtonIcon($"Pause_{preset}", FontAwesomeIcon.Stop, GetLoc("Stop")))
                ShopPresetExecutor.CancelAndDispose();

            ImGui.SameLine();
            if (ImGuiOm.ButtonIcon($"Export_{preset}", FontAwesomeIcon.FileExport, GetLoc("Export")))
                ExportToClipboard(preset);

            ImGui.SameLine();
            if (ImGuiOm.ButtonIcon($"DeletePreset_{preset}", FontAwesomeIcon.Trash, $"{GetLoc("Delete")} (Ctrl)"))
            {
                if (ImGui.IsKeyDown(ImGuiKey.LeftCtrl))
                    ModuleConfig.Presets.Remove(preset);
            }
        }

        private void WindowRenderAddNewPreset()
        {
            if (!IsAddNewPresetWindowOpen) return;
            RefreshWindowInfo();

            using (FontManager.UIFont.Push())
            {
                if (ImGui.Begin($"{GetLoc("AutoShopPurchase-UI-AddNewPreset")}###AutoShopPurchase-AddNewPreset", ref IsAddNewPresetWindowOpen))
                {
                    using (ImRaii.Group())
                        WindowRenderPresetInfoInput();

                    ImGui.SameLine();
                    ScaledDummy(8f);

                    ImGui.SameLine();
                    using (ImRaii.Group())
                    {
                        foreach (var data in ScannedData.ToList())
                            WindowRenderAddonInfo(data);
                    }

                    ImGui.End();
                }
            }
        }

        private void RefreshWindowInfo()
        {
            if (!Throttler.Throttle("AutoShopPurchase-RefreshFocusedAddonsInfo", 2000)) return;
            AddonWithListInfo.ScanAddons(FocusedList).ForEach(x =>
            {
                if (!ScannedData.Contains(x))
                    ScannedData.Add(x);
            });

            if (!string.IsNullOrWhiteSpace(TargetNameInput) && DService.Targets.Target is { } target)
                TargetNameInput = target.Name.TextValue;
        }

        private void WindowRenderPresetInfoInput()
        {
            ImGuiOm.CompLabelLeft(
                $"{GetLoc("Name")}:", 200f * GlobalFontScale,
                () => ImGui.InputText("###NameInput", ref NameInput, 256));

            ImGuiOm.CompLabelLeft(
                $"{GetLoc("Target")}:", 200f * GlobalFontScale,
                () => ImGui.InputText("###TargetNameInput", ref TargetNameInput, 256, ImGuiInputTextFlags.ReadOnly));
            ImGuiOm.TooltipHover(GetLoc("AutoShopPurchase-Tooltip-EmptyTargetInput"), 30f);

            ImGuiOm.CompLabelLeft(
                $"{GetLoc("Addon")}:", 200f * GlobalFontScale,
                () => ImGui.InputText("###AddonNameInput", ref AddonNameInput, 128, ImGuiInputTextFlags.ReadOnly));

            ImGuiOm.CompLabelLeft(
                $"{GetLoc("List")}:", 200f * GlobalFontScale,
                () => ImGui.InputUInt("###ListComponentNodeIDInput", ref ListComponentNodeIDInput, flags: ImGuiInputTextFlags.ReadOnly));

            ImGuiOm.CompLabelLeft(
                $"{GetLoc("Click")}:", 200f * GlobalFontScale,
                () =>
                {
                    var result = ImGui.InputInt("###ClickIndexInput", ref ClickIndexInput, flags: ImGuiInputTextFlags.ReadOnly);
                    if (result) 
                        ClickIndexInput = Math.Max(1, ClickIndexInput);
                    return result;
                });

            ImGui.Checkbox(GetLoc("AutoShopPurchase-UI-IsSetNumber"), ref IsSetNumberInput);

            if (IsSetNumberInput)
            {
                ImGuiOm.CompLabelLeft(
                    $"{GetLoc("Number")}:", 200f * GlobalFontScale,
                    () =>
                    {
                        var result = ImGui.InputInt("###SetNumberInput", ref SetNumberInput, 0, 100);
                        if (result) 
                            SetNumberInput = Math.Max(1, SetNumberInput);
                        return result;
                    });
            }

            if (ImGuiOm.ButtonIconWithText(FontAwesomeIcon.Plus, Lang.Get("Add")))
            {
                if (!string.IsNullOrWhiteSpace(AddonNameInput) && !string.IsNullOrWhiteSpace(NameInput))
                {
                    var preset = new ShopPurchasePreset(AddonNameInput)
                    {
                        ClickRoute = new(ListComponentNodeIDInput, ClickIndexInput),
                        NumberRoute = new(IsSetNumberInput, SetNumberInput),
                        Name = NameInput,
                        TargetName = TargetNameInput
                    };

                    if (!ModuleConfig.Presets.Contains(preset))
                    {
                        ModuleConfig.Presets.Add(preset);
                        NotificationSuccess(GetLoc("AutoShopPurchase-Tooltip-AddPresetSuccess", NameInput));
                    }
                }
            }
        }

        private unsafe void WindowRenderAddonInfo(AddonWithListInfo data)
        {
            var addon = data.GetAddon();
            if (!IsAddonAndNodesReady(addon))
            {
                ScannedData.Remove(data);
                return;
            }

            if (ImGui.CollapsingHeader(GetLoc("AutoShopPurchase-UI-AddonInfoHeader", data.AddonName, data.ListNodeIDs.Count), ImGuiTreeNodeFlags.DefaultOpen))
            {
                foreach (var nodeID in data.ListNodeIDs)
                    WindowRenderListComponentNodeInfo(data, nodeID);
            }
        }

        private static unsafe void WindowRenderListComponentNodeInfo(AddonWithListInfo data, uint nodeID)
        {
            var node = data.GetListByID(nodeID);
            if (node == null) return;

            
            using (var treeNode = ImRaii.TreeNode($"{GetLoc("List")} {nodeID}", ImGuiTreeNodeFlags.DefaultOpen))
            {
                if (ImGui.IsItemHovered())
                    OutlineNode((AtkResNode*)node->OwnerNode);

                if (treeNode)
                {
                    for (var i = 0; i < node->ListLength; i++)
                    {
                        if (i % 11 != 0) 
                            ImGui.SameLine();
                        if (ImGui.Button($"{i:D3}##{data.AddonName}-{nodeID}"))
                        {
                            AddonNameInput = data.AddonName;
                            ListComponentNodeIDInput = nodeID;
                            ClickIndexInput = i;
                            node->DispatchItemEvent(i, AtkEventType.ListItemClick);
                        }

                        if (node->ItemRendererList == null) continue;
                        var isHovered = ImGui.IsItemHovered();
                        var listItem = node->ItemRendererList[i];
                        switch (isHovered)
                        {
                            case true when !listItem.IsHighlighted:
                                node->SetItemHighlightedState(i, true);
                                break;
                            case false when listItem.IsHighlighted:
                                node->SetItemHighlightedState(i, false);
                                break;
                        }
                    }
                }
            }

            if (!node->OwnerNode->IsVisible())
            {
                ImGui.SameLine();
                ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), GetLoc("AutoShopPurchase-UI-InvisibleList"));
            }
        }

        private class PresetRunTimesInputComponent(ShopPurchasePreset preset)
        {
            public static PresetRunTimesInputComponent Using(ShopPurchasePreset preset)
            {
                if (Cache.TryGetValue(preset, out var instance))
                    return instance;

                instance = new(preset);
                Cache[preset] = instance;
                return instance;
            }

            private static readonly Dictionary<ShopPurchasePreset, PresetRunTimesInputComponent> Cache = [];

            private int TimesInput = 1;

            public void Draw()
            {
                ImGuiOm.DisableZoneWithHelp(DrawComponent,
                                            [
                                                new(ShopPresetExecutor.IsRunning,
                                                    GetLoc("AutoShopPurchase-Tooltip-RunningPreset", ShopPresetExecutor.CurrentPresetName))
                                            ], Lang.Get("DisableZoneHeader"));
                
            }

            private void DrawComponent()
            {
                using var group = ImRaii.Group();
                if (ImGuiOm.ButtonIcon($"RunPreset_{preset}", FontAwesomeIcon.Play, GetLoc("Run")))
                    DService.Framework.RunOnTick(async () => await ShopPresetExecutor.TryExecuteAsync(preset, TimesInput).ConfigureAwait(false));

                ImGui.SameLine(0, 2f * GlobalFontScale);

                ImGui.SetNextItemWidth(50f * GlobalFontScale);
                if (ImGui.InputInt($"###RunPresetTimesInput{preset}", ref TimesInput))
                    TimesInput = Math.Max(1, TimesInput);
            }
        }
    }

    public class ShopPresetExecutor : IDisposable
    {
        private TaskHelper         TaskHelper { get; init; } = new() { TimeLimitMS = 10_000 };
        private ShopPurchasePreset Preset     { get; init; }
        private int                LoopCount  { get; init; }

        public static bool IsRunning => Instance != null;
        public static string CurrentPresetName => Instance?.Preset.Name ?? string.Empty;

        private static ShopPresetExecutor? Instance;
        private static readonly object Lock = new();
        private readonly CancellationTokenSource CancelSource = new();
        private readonly TaskCompletionSource<bool> CompletionSource = new();

        private bool IsWaitingRefresh;
        private int currentLoopCount;

        private ShopPresetExecutor(ShopPurchasePreset preset, int loopCount)
        {
            Preset = preset;
            LoopCount = loopCount;
            DService.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, ["SelectYesno", "ShopExchangeItemDialog"], OnAddonYesno);
            ExecuteCommandManager.RegPost(OnReceiveCommand);
        }

        public static async Task<bool> TryExecuteAsync(ShopPurchasePreset preset, int loopCount)
        {
            lock (Lock)
            {
                if (Instance != null) return false;
                Instance = new ShopPresetExecutor(preset, loopCount);
            }

            try
            {
                await Instance.ExecuteAsync();
                return true;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
            finally
            {
                lock (Lock)
                {
                    Instance?.Dispose();
                    Instance = null;
                }
            }
        }

        public static bool CancelAndDispose()
        {
            lock (Lock)
            {
                if (Instance == null) return false;

                Instance.CancelSource.Cancel();
                Instance.Dispose();
                Instance = null;
                return true;
            }
        }

        private async Task ExecuteAsync()
        {
            try
            {
                Execute();
                await CompletionSource.Task;
            }
            catch (OperationCanceledException)
            {
                CompletionSource.TrySetCanceled();
                throw;
            }
        }

        private unsafe void Execute()
        {
            CancelSource.Token.ThrowIfCancellationRequested();

            var tasks = Preset.GetTasks();
            if (currentLoopCount == LoopCount || tasks.Count <= 0)
            {
                CompletionSource.TrySetResult(true);
                return;
            }
            tasks.ForEach(x =>
            {
                TaskHelper.Enqueue(() =>
                {
                    CancelSource.Token.ThrowIfCancellationRequested();
                    if (IsAddonAndNodesReady(Request)) return false;

                    IsWaitingRefresh = true;
                    x();
                    return true;
                }, weight: 2);
            });

            TaskHelper.DelayNext(1_000, "防止卡住", false, 1);
            TaskHelper.Enqueue(() => OnReceiveCommand(ExecuteCommandFlag.InventoryRefresh, 0, 0, 0, 0));
        }

        private unsafe void OnAddonYesno(AddonEvent type, AddonArgs args)
        {
            if ((!TaskHelper.IsBusy && !IsWaitingRefresh) || args.Addon == nint.Zero) return;

            var addon = args.Addon.ToAtkUnitBase();
            Callback(addon, true, 0);
        }

        private void OnReceiveCommand(ExecuteCommandFlag command, uint param1, uint param2, uint param3, uint param4)
        {
            if (!IsWaitingRefresh || command != ExecuteCommandFlag.InventoryRefresh) return;

            IsWaitingRefresh = false;
            TaskHelper.RemoveQueue(1);
            TaskHelper.Enqueue(() =>
            {
                currentLoopCount++;
                Execute();
            }, weight: 2);
        }

        public void Dispose()
        {
            ExecuteCommandManager.Unreg(OnReceiveCommand);
            DService.AddonLifecycle.UnregisterListener(OnAddonYesno);

            TaskHelper.Abort();
            IsWaitingRefresh = false;

            CancelSource.Cancel();
            CancelSource.Dispose();
        }
    }
}
