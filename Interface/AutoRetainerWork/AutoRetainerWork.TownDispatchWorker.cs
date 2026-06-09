using DailyRoutines.Extensions;
using FFXIVClientStructs.FFXIV.Client.UI;
using OmenTools.Interop.Game.AddonEvent;
using OmenTools.OmenService;
using OmenTools.Threading.TaskHelper;

namespace DailyRoutines.ModulesPublic.Interface;

public unsafe partial class AutoRetainerWork
{
    private class TownDispatchWorker
    (
        AutoRetainerWork module
    ) : RetainerWorkerBase(module)
    {
        private TaskHelper? TaskHelper;

        public override bool DrawConfigCondition() => true;

        public override bool IsWorkerBusy() => TaskHelper?.IsBusy ?? false;

        public override void Init() => TaskHelper ??= new() { TimeoutMS = 15_000 };

        public override void Uninit()
        {
            TaskHelper?.Abort();
            TaskHelper?.Dispose();
            TaskHelper = null;
        }

        public override void DrawConfig()
        {
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), Lang.Get("AutoRetainerWork-Dispatch-Title"));

            var imageState = ImageHelper.Instance().TryGetImage
            (
                "https://gh.atmoomen.top/StaticAssets/main/DailyRoutines/image/AutoRetainersDispatch-1.png",
                out var imageHandle
            );
            ImGui.SameLine();
            ImGui.TextDisabled(FontAwesomeIcon.InfoCircle.ToIconString());

            if (ImGui.IsItemHovered())
            {
                using (ImRaii.Tooltip())
                {
                    ImGui.TextUnformatted(Lang.Get("AutoRetainerWork-Dispatch-Description"));
                    if (imageState)
                        ImGui.Image(imageHandle.Handle, imageHandle.Size * 0.8f);
                }
            }

            using var indent = ImRaii.PushIndent();

            if (ImGui.Button(Lang.Get("Start")))
                EnqueueRetainersDispatch();

            ImGui.SameLine();
            if (ImGui.Button(Lang.Get("Stop")))
                TaskHelper.Abort();
        }

        private void EnqueueRetainersDispatch()
        {
            if (TaskHelper.AbortByConflictKey(Module)) return;
            if (Module.IsAnyOtherWorkerBusy(typeof(TownDispatchWorker))) return;

            var addon = (AddonSelectString*)SelectString;
            if (addon == null) return;

            var entryCount = addon->PopupMenu.PopupMenu.EntryCount;
            if (entryCount - 1 <= 0) return;

            for (var i = 0; i < entryCount - 1; i++)
            {
                var tempI = i;
                TaskHelper.Enqueue
                (
                    () =>
                    {
                        if (TaskHelper.AbortByConflictKey(Module)) return true;
                        return AddonSelectStringEvent.Select(tempI);
                    },
                    $"点击第 {tempI} 位雇员, 拉起市场变更请求"
                );
                TaskHelper.Enqueue
                (
                    () =>
                    {
                        if (TaskHelper.AbortByConflictKey(Module)) return true;
                        return AddonSelectYesnoEvent.ClickYes();
                    },
                    "确认市场变更"
                );
            }
        }
    }
}
