using DailyRoutines.Abstracts;
using DailyRoutines.Infos;
using DailyRoutines.Managers;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine.Layer;
using Lumina.Excel.Sheets;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;

namespace DailyRoutines.ModulesPublic.General
{
    public class QuickChangeAdjoiningArea : DailyModuleBase
    {
        public Config? Config;
        public override ModuleInfo Info { get; } = new()
        {
            Title = "Quick Change Adjoining Area",
            Description = "Quick Change Adjoining Area in anywhere if can change",
            Category = ModuleCategories.General,
            Author = ["Sbago"],
        };
        protected override void Init()
        {
            Config = LoadConfig<Config>() ?? new();
        }
        protected override void ConfigUI()
        {
            unsafe
            {
                if (LayoutWorld.Instance()->ActiveLayout->InstancesByType.TryGetValuePointer(InstanceType.ExitRange, out var exitRanges))
                {
                    foreach(var ExitRange in exitRanges->Value->Values)
                    {
                        var pExitRange = (ExitRangeLayoutInstance*)ExitRange.Value;
                        var pPopRange = pExitRange->PopRangeLayoutInstance;
                        if(pPopRange != null)
                        {
                            ImGui.Text($"{LuminaGetter.Get<TerritoryType>().GetRow(pExitRange->TerritoryType).PlaceName.Value.Name} {*pPopRange->Base.GetTranslationImpl()}");
                            ImGui.SameLine();
                            if(ImGui.Button($"Change###{(long)pExitRange:X}"))
                                PopRangeManager.Instance()->PopRange((ILayoutInstance*)pExitRange);
                        }
                    }
                }
            }
        }

    }
    public class Config : ModuleConfiguration
    {
        public Dictionary<uint, List<uint>> Record = new();
    }
    [StructLayout(LayoutKind.Explicit, Size = 0x100)]
    public unsafe struct PopRangeManager
    {
        [FieldOffset(0x00)] public int State;
        [FieldOffset(0x04)] public float Time;
        [FieldOffset(0x10)] public ExitRangeLayoutInstance* ExitLayoutInstance;//Set if LocalPlayer is inside ExitRangeLayoutInstance's collider
        [FieldOffset(0x20)] public Vector3 Position;
        [FieldOffset(0x30)] public Vector3 RecoveredPosition;//When check position failure,recovered position
        public static PopRangeManager* Instance() => (PopRangeManager*)DService.SigScanner.GetStaticAddressFromSig("83 3D ?? ?? ?? ?? ?? 77 ??");
        public void PopRange(ILayoutInstance* exit)
        {
            ExitLayoutInstance = (ExitRangeLayoutInstance*)exit;
            var pop = ExitLayoutInstance->PopRangeLayoutInstance;
            Position = *pop->Base.GetTranslationImpl();
            var Recovered = Position + *pop->AddPos;
            //Set LocalPlayer's position near the collision object to escape position checking
            DService.ClientState.LocalPlayer.Struct()->SetPosition(Recovered.X, Recovered.Y, Recovered.Z);
            RecoveredPosition = Recovered;
            State = 2;
        }
    }
    [StructLayout(LayoutKind.Explicit, Size = 0xA0)]
    public unsafe struct ExitRangeLayoutInstance
    {
        [FieldOffset(0x0)] public TriggerBoxLayoutInstance Base;
        [FieldOffset(0x86)] public ushort TerritoryType;
        [FieldOffset(0x90)] public uint PopRangeLayoutInstanceId;
        public PopRangeLayoutInstance* PopRangeLayoutInstance => (PopRangeLayoutInstance*)LayoutWorld.GetLayoutInstance(InstanceType.PopRange, PopRangeLayoutInstanceId);
    }
    [StructLayout(LayoutKind.Explicit, Size = 0xA0)]
    public unsafe struct PopRangeLayoutInstance
    {
        [FieldOffset(0x0)] public TriggerBoxLayoutInstance Base;
        [FieldOffset(0x80)] public Vector3* AddPos;
    }
    file static class Extension
    {
        public static unsafe BattleChara* Struct(this Dalamud.Game.ClientState.Objects.SubKinds.IPlayerCharacter player)
        {
            return (BattleChara*)player.Address;
        }
    }
}
