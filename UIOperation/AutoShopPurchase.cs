using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using DailyRoutines.Manager;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using OmenTools.Info.Game.Enums;
using OmenTools.Interop.Game.Helpers;
using OmenTools.OmenService;
using OmenTools.Threading;
using OmenTools.Threading.TaskHelper;
using Exception = System.Exception;

namespace DailyRoutines.ModulesPublic;

public class AutoShopPurchase : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoShopPurchaseTitle"),
        Description = Lang.Get("AutoShopPurchaseDescription"),
        Category    = ModuleCategory.UIOperation
    };

    public override ModulePermission Permission { get; } = new() { NeedAuth = true };

    private Config config = null!;
    
    private ShopPresetDisplayTable? presetDisplayTable;
    private List<AddonWithListInfo> scannedData = [];

    protected override void Init()
    {
        config             =   Config.Load(this) ?? new();
        presetDisplayTable ??= new(this);
    }
    
    protected override void Uninit()
    {
        config?.Save(this);

        presetDisplayTable?.Dispose();
        presetDisplayTable = null;

        ShopPresetExecutor.CancelAndDispose();
    }

    protected override void ConfigUI()
    {
        if (ImGuiOm.ButtonIconWithText(FontAwesomeIcon.FileImport, Lang.Get("Import")))
        {
            var clipboard = ImportFromClipboard<ShopPurchasePreset>();

            if (clipboard != null)
            {
                this.config.Presets.Add(clipboard);
                this.config.Save(this);
            }
        }

        presetDisplayTable.Draw();
    }
    
    private class Config : ModuleConfig
    {
        public List<ShopPurchasePreset> Presets = [];
    }

    public unsafe class AddonWithListInfo
    (
        string        addonName,
        HashSet<uint> listNodeIDs
    ) : IEquatable<AddonWithListInfo>
    {
        public string        AddonName   { get; } = addonName;
        public HashSet<uint> ListNodeIDs { get; } = listNodeIDs;

        public bool Equals(AddonWithListInfo? other)
        {
            if (ReferenceEquals(null, other)) return false;
            if (ReferenceEquals(this, other)) return true;
            return AddonName == other.AddonName;
        }

        public AtkUnitBase* GetAddon() => AddonHelper.GetByName(AddonName);

        public AtkComponentList* GetListByID(uint nodeID)
        {
            if (!ListNodeIDs.Contains(nodeID)) return null;
            var addon = GetAddon();
            if (!addon->IsAddonAndNodesReady()) return null;
            return addon->GetComponentListById(nodeID);
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
                if (!addon->IsAddonAndNodesReady()) continue;

                var info = new AddonWithListInfo(addon->NameString, []);

                addon->UldManager.SearchComponentsByType(ComponentType.List)
                                 .ForEach(x => info.ListNodeIDs.Add(((AtkComponentList*)x)->OwnerNode->NodeId));
                addon->UldManager.SearchComponentsByType(ComponentType.TreeList)
                                 .ForEach(x => info.ListNodeIDs.Add(((AtkComponentTreeList*)x)->OwnerNode->NodeId));

                if (info.ListNodeIDs.Count > 0)
                    list.Add(info);
            }

            return [..list];
        }
    }

    public unsafe class ShopPurchasePreset
    (
        string addonName
    ) : IEquatable<ShopPurchasePreset>
    {
        public string                  Name        { get; set; } = string.Empty;
        public string                  AddonName   { get; set; } = addonName;
        public string                  TargetName  { get; set; } = string.Empty;
        public KeyValuePair<uint, int> ClickRoute  { get; set; } // ListComponent Node ID - Index
        public KeyValuePair<bool, int> NumberRoute { get; set; } // IsNeedToSetNumber - Number

        public bool Equals(ShopPurchasePreset? other)
        {
            if (ReferenceEquals(null, other)) return false;
            if (ReferenceEquals(this, other)) return true;

            return AddonName == other.AddonName && ClickRoute.Equals(other.ClickRoute) && NumberRoute.Equals(other.NumberRoute);
        }

        public AtkUnitBase* GetAddon() =>
            AddonHelper.GetByName(AddonName);

        public bool IsAddonValid() =>
            GetAddon()->IsAddonAndNodesReady();

        public AtkComponentList* GetListNode() =>
            !IsAddonValid() ? null : GetAddon()->GetComponentListById(ClickRoute.Key);

        public AtkComponentNumericInput* GetNumberNode()
        {
            if (!NumberRoute.Key) return null;

            var listNode = GetListNode();
            if (listNode == null || listNode->ListLength < ClickRoute.Value) return null;

            var numberNode =
                listNode->ItemRendererList[ClickRoute.Value].AtkComponentListItemRenderer->UldManager
                    .SearchComponentByType<AtkComponentNumericInput>(ComponentType.NumericInput);

            return numberNode;
        }

        public bool IsNodeValid() =>
            GetListNode() != null && (!NumberRoute.Key || NumberRoute.Key && GetNumberNode() != null);

        public bool IsTargetValid() =>
            string.IsNullOrWhiteSpace(TargetName) ||
            (!string.IsNullOrWhiteSpace(TargetName) &&
             (TargetManager.Target?.Name.ToString() ?? string.Empty) == TargetName);

        public List<Func<bool>> GetTasks()
        {
            try
            {
                var list = new List<Func<bool>>();
                if (!IsTargetValid())
                    throw new Exception(Lang.Get("AutoShopPurchase-Exception-PresetTargetInvalid"));
                if (!IsAddonValid())
                    throw new Exception(Lang.Get("AutoShopPurchase-Exception-PresetAddonInvalid"));
                if (!IsNodeValid())
                    throw new Exception(Lang.Get("AutoShopPurchase-Exception-PresetNodeInvalid"));

                if (NumberRoute.Key)
                {
                    list.Add
                    (() =>
                        {
                            var numberNode = GetNumberNode();
                            if (numberNode == null) return false;
                            numberNode->SetValue(NumberRoute.Value);
                            return true;
                        }
                    );
                }

                list.Add
                (() =>
                    {
                        var listNode = GetListNode();
                        if (listNode == null) return false;

                        listNode->DispatchItemEvent(ClickRoute.Value, AtkEventType.ListItemClick);
                        return true;
                    }
                );

                return list;
            }
            catch (Exception ex)
            {
                NotifyHelper.Instance().NotificationError($"{Lang.Get("Error")}: {ex.Message}");
                return [];
            }
        }

        public override bool Equals(object? obj)
        {
            if (ReferenceEquals(null, obj)) return false;
            if (ReferenceEquals(this, obj)) return true;
            if (obj.GetType() != GetType()) return false;
            return Equals((ShopPurchasePreset)obj);
        }

        public override int GetHashCode() =>
            HashCode.Combine(AddonName, ClickRoute, NumberRoute);

        public override string ToString() =>
            $"{Name}_{AddonName}_Click:{ClickRoute.Key}-{ClickRoute.Value}_Number:{NumberRoute.Key}-{NumberRoute.Value}";
    }

    private class ShopPresetDisplayTable : IDisposable
    {
        private readonly AutoShopPurchase module;
        
        private string nameInput       = string.Empty;
        private string targetNameInput = Lang.Get("AutoShopPurchase-UI-UnknownTarget");
        private string addonNameInput  = string.Empty;
        private uint   listComponentNodeIDInput;
        private int    clickIndexInput;
        private bool   isSetNumberInput;
        private int    setNumberInput;
        private bool   isAddNewPresetWindowOpen;

        public ShopPresetDisplayTable(AutoShopPurchase module)
        {
            this.module                       =  module;
            WindowManager.Instance().PostDraw += WindowRenderAddNewPreset;
        }

        private static unsafe AtkUnitList FocusedList =>
            RaptureAtkUnitManager.Instance()->FocusedUnitsList;

        public void Dispose() =>
            WindowManager.Instance().PostDraw -= WindowRenderAddNewPreset;

        public void Draw()
        {
            var       tableSize = ImGui.GetContentRegionAvail() with { Y = 0 };
            using var table     = ImRaii.Table("ShopPresetDisplayTable", 7, ImGuiTableFlags.Borders, tableSize);
            if (!table) return;

            TableSetupColumns();
            TableRenderHeaderRow();

            for (var i = 0; i < module.config.Presets.Count; i++)
                TableRenderRow(i, module.config.Presets[i]);
        }

        private static void TableSetupColumns()
        {
            ImGui.TableSetupColumn("序号", ImGuiTableColumnFlags.WidthFixed, ImGui.CalcTextSize("1234").X);
            ImGui.TableSetupColumn("名称", ImGuiTableColumnFlags.None,       25);
            ImGui.TableSetupColumn("对象", ImGuiTableColumnFlags.None,       20);
            ImGui.TableSetupColumn("界面", ImGuiTableColumnFlags.None,       20);
            ImGui.TableSetupColumn("路径", ImGuiTableColumnFlags.None,       15);
            ImGui.TableSetupColumn("数量", ImGuiTableColumnFlags.WidthFixed, ImGui.CalcTextSize("1234").X);
            ImGui.TableSetupColumn("操作", ImGuiTableColumnFlags.None,       30);
        }

        private void TableRenderHeaderRow()
        {
            ImGui.TableNextRow(ImGuiTableRowFlags.Headers);

            ImGui.TableNextColumn();

            if (ImGuiOm.ButtonIconSelectable("OpenAddNewPresetWindow", FontAwesomeIcon.Plus))
            {
                module.scannedData              =  AddonWithListInfo.ScanAddons(FocusedList);
                isAddNewPresetWindowOpen ^= true;
            }

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(Lang.Get("Name"));

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(Lang.Get("Target"));

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(Lang.Get("Addon"));

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(Lang.Get("Route"));

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(Lang.Get("Amount"));

            ImGui.TableNextColumn();
        }

        private void TableRenderRow(int counter, ShopPurchasePreset preset)
        {
            using var id = ImRaii.PushId(preset.ToString());
            ImGui.TableNextRow();

            ImGui.TableNextColumn();
            ImGui.TextUnformatted($"{counter + 1}");

            ImGui.TableNextColumn();
            ImGui.TextUnformatted($"{preset.Name}");

            ImGui.TableNextColumn();
            ImGui.TextUnformatted($"{preset.TargetName}");

            ImGui.TableNextColumn();
            ImGui.TextUnformatted($"{preset.AddonName}");

            ImGui.TableNextColumn();
            ImGui.TextUnformatted($"{preset.ClickRoute.Key} -> {preset.ClickRoute.Value}");

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(preset.NumberRoute.Key ? $"{preset.NumberRoute.Value}" : $"({Lang.Get("None")})");

            ImGui.TableNextColumn();
            PresetRunTimesInputComponent.Using(preset).Draw();

            ImGui.SameLine();
            if (ImGuiOm.ButtonIcon($"Pause_{preset}", FontAwesomeIcon.Stop, Lang.Get("Stop")))
                ShopPresetExecutor.CancelAndDispose();

            ImGui.SameLine();
            if (ImGuiOm.ButtonIcon($"Export_{preset}", FontAwesomeIcon.FileExport, Lang.Get("Export")))
                ExportToClipboard(preset);

            ImGui.SameLine();

            if (ImGuiOm.ButtonIcon($"DeletePreset_{preset}", FontAwesomeIcon.Trash, $"{Lang.Get("Delete")} (Ctrl)"))
            {
                if (ImGui.IsKeyDown(ImGuiKey.LeftCtrl))
                    module.config.Presets.Remove(preset);
            }
        }

        private void WindowRenderAddNewPreset()
        {
            if (!isAddNewPresetWindowOpen) return;
            RefreshWindowInfo();

            using (FontManager.Instance().UIFont.Push())
            {
                if (ImGui.Begin($"{Lang.Get("AutoShopPurchase-UI-AddNewPreset")}###AutoShopPurchase-AddNewPreset", ref isAddNewPresetWindowOpen))
                {
                    using (ImRaii.Group())
                        WindowRenderPresetInfoInput();

                    ImGui.SameLine();
                    ImGuiOm.ScaledDummy(8f);

                    ImGui.SameLine();

                    using (ImRaii.Group())
                    {
                        foreach (var data in module.scannedData.ToList())
                            WindowRenderAddonInfo(data);
                    }

                    ImGui.End();
                }
            }
        }

        private void RefreshWindowInfo()
        {
            if (!Throttler.Shared.Throttle("AutoShopPurchase-RefreshFocusedAddonsInfo", 2000)) return;
            AddonWithListInfo.ScanAddons(FocusedList).ForEach
            (x =>
                {
                    if (!module.scannedData.Contains(x))
                        module.scannedData.Add(x);
                }
            );

            if (!string.IsNullOrWhiteSpace(targetNameInput) && TargetManager.Target is { } target)
                targetNameInput = target.Name.ToString();
        }

        private void WindowRenderPresetInfoInput()
        {
            ImGuiOm.CompLabelLeft
            (
                $"{Lang.Get("Name")}:",
                200f * GlobalUIScale,
                () => ImGui.InputText("###NameInput", ref nameInput, 256)
            );

            ImGuiOm.CompLabelLeft
            (
                $"{Lang.Get("Target")}:",
                200f * GlobalUIScale,
                () => ImGui.InputText("###TargetNameInput", ref targetNameInput, 256, ImGuiInputTextFlags.ReadOnly)
            );
            ImGuiOm.TooltipHover(Lang.Get("AutoShopPurchase-Tooltip-EmptyTargetInput"), 30f);

            ImGuiOm.CompLabelLeft
            (
                $"{Lang.Get("Addon")}:",
                200f * GlobalUIScale,
                () => ImGui.InputText("###AddonNameInput", ref addonNameInput, 128, ImGuiInputTextFlags.ReadOnly)
            );

            ImGuiOm.CompLabelLeft
            (
                $"{Lang.Get("List")}:",
                200f * GlobalUIScale,
                () => ImGui.InputUInt("###ListComponentNodeIDInput", ref listComponentNodeIDInput, flags: ImGuiInputTextFlags.ReadOnly)
            );

            ImGuiOm.CompLabelLeft
            (
                $"{Lang.Get("Click")}:",
                200f * GlobalUIScale,
                () =>
                {
                    var result = ImGui.InputInt("###ClickIndexInput", ref clickIndexInput, flags: ImGuiInputTextFlags.ReadOnly);
                    if (result)
                        clickIndexInput = Math.Max(1, clickIndexInput);
                    return result;
                }
            );

            ImGui.Checkbox(Lang.Get("AutoShopPurchase-UI-IsSetNumber"), ref isSetNumberInput);

            if (isSetNumberInput)
            {
                ImGuiOm.CompLabelLeft
                (
                    $"{Lang.Get("Number")}:",
                    200f * GlobalUIScale,
                    () =>
                    {
                        var result = ImGui.InputInt("###SetNumberInput", ref setNumberInput, 0, 100);
                        if (result)
                            setNumberInput = Math.Max(1, setNumberInput);
                        return result;
                    }
                );
            }

            if (ImGuiOm.ButtonIconWithText(FontAwesomeIcon.Plus, Lang.Get("Add")))
            {
                if (!string.IsNullOrWhiteSpace(addonNameInput) && !string.IsNullOrWhiteSpace(nameInput))
                {
                    var preset = new ShopPurchasePreset(addonNameInput)
                    {
                        ClickRoute  = new(listComponentNodeIDInput, clickIndexInput),
                        NumberRoute = new(isSetNumberInput, setNumberInput),
                        Name        = nameInput,
                        TargetName  = targetNameInput
                    };

                    if (!module.config.Presets.Contains(preset))
                    {
                        module.config.Presets.Add(preset);
                        NotifyHelper.Instance().NotificationSuccess(Lang.Get("AutoShopPurchase-Tooltip-AddPresetSuccess", nameInput));
                    }
                }
            }
        }

        private unsafe void WindowRenderAddonInfo(AddonWithListInfo data)
        {
            var addon = data.GetAddon();

            if (!addon->IsAddonAndNodesReady())
            {
                module.scannedData.Remove(data);
                return;
            }

            if (ImGui.CollapsingHeader(Lang.Get("AutoShopPurchase-UI-AddonInfoHeader", data.AddonName, data.ListNodeIDs.Count), ImGuiTreeNodeFlags.DefaultOpen))
            {
                foreach (var nodeID in data.ListNodeIDs)
                    WindowRenderListComponentNodeInfo(data, nodeID);
            }
        }

        private unsafe void WindowRenderListComponentNodeInfo(AddonWithListInfo data, uint nodeID)
        {
            var node = data.GetListByID(nodeID);
            if (node == null) return;


            using (var treeNode = ImRaii.TreeNode($"{Lang.Get("List")} {nodeID}", ImGuiTreeNodeFlags.DefaultOpen))
            {
                if (ImGui.IsItemHovered())
                    ((AtkResNode*)node->OwnerNode)->OutlineNode();

                if (treeNode)
                {
                    for (var i = 0; i < node->ListLength; i++)
                    {
                        if (i % 11 != 0)
                            ImGui.SameLine();

                        if (ImGui.Button($"{i:D3}##{data.AddonName}-{nodeID}"))
                        {
                            addonNameInput           = data.AddonName;
                            listComponentNodeIDInput = nodeID;
                            clickIndexInput          = i;
                            node->DispatchItemEvent(i, AtkEventType.ListItemClick);
                        }

                        if (node->ItemRendererList == null) continue;
                        var isHovered = ImGui.IsItemHovered();
                        var listItem  = node->ItemRendererList[i];

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
                ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), Lang.Get("AutoShopPurchase-UI-InvisibleList"));
            }
        }

        private class PresetRunTimesInputComponent
        (
            ShopPurchasePreset preset
        )
        {
            private static readonly Dictionary<ShopPurchasePreset, PresetRunTimesInputComponent> Cache = [];

            private int timesInput = 1;

            public static PresetRunTimesInputComponent Using(ShopPurchasePreset preset)
            {
                if (Cache.TryGetValue(preset, out var instance))
                    return instance;

                instance      = new(preset);
                Cache[preset] = instance;
                return instance;
            }

            public void Draw()
            {
                using var disabled = ImRaii.Disabled(ShopPresetExecutor.IsRunning);
                using var group    = ImRaii.Group();

                if (ImGuiOm.ButtonIcon($"RunPreset_{preset}", FontAwesomeIcon.Play, Lang.Get("Run")))
                    DService.Instance().Framework.RunOnTick(async () => await ShopPresetExecutor.TryExecuteAsync(preset, timesInput).ConfigureAwait(false));

                ImGui.SameLine(0, 2f * GlobalUIScale);

                ImGui.SetNextItemWidth(50f * GlobalUIScale);
                if (ImGui.InputInt($"###RunPresetTimesInput{preset}", ref timesInput))
                    timesInput = Math.Max(1, timesInput);
            }
        }
    }

    public class ShopPresetExecutor : IDisposable
    {
        private static          ShopPresetExecutor?        Instance;
        private static readonly Lock                       Lock             = new();
        private readonly        CancellationTokenSource    CancelSource     = new();
        private readonly        TaskCompletionSource<bool> CompletionSource = new();
        private                 int                        currentLoopCount;

        private bool IsWaitingRefresh;

        private ShopPresetExecutor(ShopPurchasePreset preset, int loopCount)
        {
            Preset    = preset;
            LoopCount = loopCount;
            DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PostSetup, ["SelectYesno", "ShopExchangeItemDialog"], OnAddonYesno);
            ExecuteCommandManager.Instance().RegPost(OnReceiveCommand);
        }

        private TaskHelper         TaskHelper { get; init; } = new() { TimeoutMS = 10_000 };
        private ShopPurchasePreset Preset     { get; init; }
        private int                LoopCount  { get; init; }

        public static bool   IsRunning         => Instance != null;
        public static string CurrentPresetName => Instance?.Preset.Name ?? string.Empty;

        public void Dispose()
        {
            ExecuteCommandManager.Instance().Unreg(OnReceiveCommand);
            DService.Instance().AddonLifecycle.UnregisterListener(OnAddonYesno);

            TaskHelper.Dispose();
            IsWaitingRefresh = false;

            CancelSource.Cancel();
            CancelSource.Dispose();
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

            tasks.ForEach
            (x =>
                {
                    TaskHelper.Enqueue
                    (
                        () =>
                        {
                            CancelSource.Token.ThrowIfCancellationRequested();
                            if (Request->IsAddonAndNodesReady()) return false;

                            IsWaitingRefresh = true;
                            x();
                            return true;
                        },
                        weight: 2
                    );
                }
            );

            TaskHelper.DelayNext(1_000, "防止卡住", 1);
            TaskHelper.Enqueue(() => OnReceiveCommand(ExecuteCommandFlag.InventoryRefresh, 0, 0, 0, 0));
        }

        private unsafe void OnAddonYesno(AddonEvent type, AddonArgs args)
        {
            if (!TaskHelper.IsBusy && !IsWaitingRefresh || args.Addon == nint.Zero) return;

            var addon = args.Addon.ToStruct();
            addon->Callback(0);
        }

        private void OnReceiveCommand(ExecuteCommandFlag command, uint param1, uint param2, uint param3, uint param4)
        {
            if (!IsWaitingRefresh || command != ExecuteCommandFlag.InventoryRefresh) return;

            IsWaitingRefresh = false;
            TaskHelper.RemoveQueue(1);
            TaskHelper.Enqueue
            (
                () =>
                {
                    currentLoopCount++;
                    Execute();
                },
                weight: 2
            );
        }
    }
}
