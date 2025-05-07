using System.Collections.Generic;
using System.Numerics;
using DailyRoutines.Abstracts;
using DailyRoutines.Infos;
using DailyRoutines.Managers;
using DailyRoutines.Windows;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.Interop;
using Lumina.Excel.Sheets;
using EventHandler = FFXIVClientStructs.FFXIV.Client.Game.Event.EventHandler;

namespace DailyRoutines.Modules;

public unsafe class VoidInteract : DailyModuleBase
{
    private static readonly Dictionary<uint, Interactevent> Eventlist = new();
    private static IGameObject _gameObject;

    private static readonly List<INteractionEvents> Eventmap = new()
    {
        new WorkTicket(),          //工票交易
        new Mail(),                //邮箱
        new EmployeeBell(),        //雇员铃
        new MarketTradingBoard(),  //市场交易板
        new TroopCabinet(),        //部队柜
        new CollectiblesTrading(), //收藏品交易
        //
        new MilitaryTicketPreparation(),
        //
        new MilitaryBillTransactions()
    };

    public override ModuleInfo Info { get; } = new()

    {
        Title = GetLoc("VoidInteractTitle"),
        Description = GetLoc("VoidInteractDescription"),
        Category = ModuleCategories.General,
        Author = ["Xww"]
    };

    public override void Init()
    {
        FrameworkManager.Register(OnUpdate, throttleMS: 500);
        Overlay ??= new Overlay(this);
        Overlay.Size = new Vector2(200, 500);
    }

    public override void Uninit() { }

    private void OnUpdate(IFramework _)
    {
        DService.Log.Debug(Overlay!.IsOpen.ToString());
        if (DService.ClientState.LocalPlayer == null)
        {
            if (Overlay.IsOpen) Overlay.Toggle();
            return;
        }

        if (!TryInteract(out var interactobj))
        {
            if (Overlay.IsOpen) Overlay.Toggle();
            return;
        }

        Setevent(interactobj);
    }

    //因为移动可能会超过指定距离然后服务器判定过远
    private void Setevent(IGameObject interactobj)
    {
        Eventlist.Clear();
        foreach (var eve in EventFramework.Instance()->EventHandlerModule.EventHandlerMap)
        foreach (var e in Eventmap)
            if (e.Check(eve.Item1, eve.Item2))
                e.Build(eve.Item1, Eventlist);
        if (Eventlist.Count == 0)
            return;
        _gameObject = interactobj;
        if (Overlay!.IsOpen)
            return;
        Overlay.Toggle();
    }

    //寻找附近能交互的 EObjName 因为有一些obj发包的时候服务器会判定距离就一个一个匹配
    private static bool TryInteract(out IGameObject outobj)
    {
        outobj = null;
        if (DService.ClientState.LocalPlayer is not { } localPlayer) return false;
        //有些obj类型不能交互
        var objs = DService.ObjectTable
                           .Where(obj => obj.ObjectKind is ObjectKind.Aetheryte or ObjectKind.EventObj);
        foreach (var obj in objs)
        {
            if (obj.Name.ExtractText() == LuminaGetter.GetRow<EObjName>(2000072)!.Value.Singular.ExtractText())
            {
                if (Vector3.Distance(localPlayer.Position, obj.Position) < 3)
                {
                    outobj = obj;
                    return true;
                }
            }

            if (obj.Name.ExtractText() == LuminaGetter.GetRow<EObjName>(2000073)!.Value.Singular.ExtractText())
            {
                if (Vector3.Distance(localPlayer.Position, obj.Position) < 4.5)
                {
                    outobj = obj;
                    return true;
                }
            }

            if (obj.Name.ExtractText() == LuminaGetter.GetRow<EObjName>(2000151)!.Value.Singular.ExtractText() ||
                obj.Name.ExtractText() == LuminaGetter.GetRow<EObjName>(2003395)!.Value.Singular.ExtractText())
            {
                if (Vector3.Distance(localPlayer.Position, obj.Position) < 10)
                {
                    outobj = obj;
                    return true;
                }
            }

            if (obj.Name.ExtractText() == LuminaGetter.GetRow<EObjName>(2002737)!.Value.Singular.ExtractText() ||
                obj.Name.ExtractText() == LuminaGetter.GetRow<EObjName>(2002738)!.Value.Singular.ExtractText() ||
                obj.Name.ExtractText() == LuminaGetter.GetRow<Aetheryte>(0)!.Value.Singular.ExtractText())
            {
                if (Vector3.Distance(localPlayer.Position, obj.Position) < 20)
                {
                    outobj = obj;
                    return true;
                }
            }
        }

        return false;
    }

    public override void OverlayUI()
    {
        if (DService.ClientState.LocalPlayer is not { } localPlayer) return;
        if (_gameObject == null || Eventlist.Count == 0) Overlay.Toggle();
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(8, 8));
        ImGui.InvisibleButton("##dragArea", new Vector2(ImGui.GetContentRegionAvail().X, 20));
        if (ImGui.IsItemActive() && ImGui.IsMouseDragging(ImGuiMouseButton.Left))
        {
            var delta = ImGui.GetIO().MouseDelta;
            var pos = Overlay.Position.GetValueOrDefault();
            pos += delta;
            Overlay.Position = pos;
        }

        foreach (var eve in Eventlist)
            if (ImGui.Button(eve.Value.Name, new Vector2(ImGui.GetContentRegionAvail().X, 30)))
            {
                if (eve.Value.Tag == Tag.Me)
                {
                    new EventStartPackt(localPlayer.GameObjectId, eve.Key).Send();
                    return;
                }

                new EventStartPackt(_gameObject!.GameObjectId, eve.Key).Send();
            }
    }

    private enum Tag
    {
        Me,
        To
    }

    private struct Interactevent(Tag tag, string name)
    {
        public readonly string Name = name;
        public readonly Tag Tag = tag;
    }

    //定义用来处理交互的 
    private interface INteractionEvents
    {
        string Name { get; set; }
        bool Check(uint id, Pointer<EventHandler> handler);
        void Build(uint id, Dictionary<uint, Interactevent> eventlist);
    }

    private class TroopCabinet : INteractionEvents
    {
        public string Name { get; set; } = "部队柜";

        //想在这里加一个判断 如果没有加入部队或者不在原始服务器
        public bool Check(uint id, Pointer<EventHandler> handler)
        {
            return id == 720995;
        }

        public void Build(uint id, Dictionary<uint, Interactevent> eventlist)
        {
            eventlist.Add(id, new Interactevent(Tag.To, Name));
        }
    }

    private class WorkTicket : INteractionEvents
    {
        public string Name { get; set; } = "工票交易";

        public bool Check(uint id, Pointer<EventHandler> handler)
        {
            if (handler.Value->Info.EventId.ContentId == EventHandlerType.PreHandler)
            {
                foreach (var obj in handler.Value->EventObjects)
                    if (obj.Value->NameString == "工票交易员")
                        return true;
            }

            return false;
        }

        public void Build(uint id, Dictionary<uint, Interactevent> eventlist)
        {
            eventlist.Add(id, new Interactevent(Tag.Me, Name));
        }
    }

    private class Mail : INteractionEvents
    {
        public string Name { get; set; } = "邮箱";

        public bool Check(uint id, Pointer<EventHandler> handler)
        {
            return id == 720898;
        }

        public void Build(uint id, Dictionary<uint, Interactevent> eventlist)
        {
            eventlist.Add(id, new Interactevent(Tag.Me, Name));
        }
    }

    private class EmployeeBell : INteractionEvents
    {
        public string Name { get; set; } = "雇员铃";

        public bool Check(uint id, Pointer<EventHandler> handler)
        {
            return id == 721440;
        }

        public void Build(uint id, Dictionary<uint, Interactevent> eventlist)
        {
            eventlist.Add(id, new Interactevent(Tag.To, Name));
        }
    }

    private class MarketTradingBoard : INteractionEvents
    {
        public string Name { get; set; } = "市场交易板";

        public bool Check(uint id, Pointer<EventHandler> handler)
        {
            return id == 720935;
        }

        public void Build(uint id, Dictionary<uint, Interactevent> eventlist)
        {
            eventlist.Add(id, new Interactevent(Tag.To, Name));
        }
    }

    private class CollectiblesTrading : INteractionEvents
    {
        public string Name { get; set; } = "收藏品交易";

        public bool Check(uint id, Pointer<EventHandler> handler)
        {
            return id == 721585;
        }

        public void Build(uint id, Dictionary<uint, Interactevent> eventlist)
        {
            eventlist.Add(id, new Interactevent(Tag.Me, Name));
        }
    }

    private class MilitaryTicketPreparation : INteractionEvents
    {
        public string Name { get; set; } = "军票筹备";

        public bool Check(uint id, Pointer<EventHandler> handler)
        {
            if (!handler.Value->UnkString0.ToString().Contains("ComDefGCSupplyDuty"))
                return false;
            foreach (var obj in handler.Value->EventObjects)
                if (obj.Value->NameString.Contains("人事负责人"))
                    return true;
            return false;
        }

        public void Build(uint id, Dictionary<uint, Interactevent> eventlist)
        {
            eventlist.Add(id, new Interactevent(Tag.Me, Name));
        }
    }

    private class MilitaryBillTransactions : INteractionEvents
    {
        public string Name { get; set; } = "军票交易";

        public bool Check(uint id, Pointer<EventHandler> handler)
        {
            if (handler.Value->Info.EventId.ContentId != EventHandlerType.GrandCompanyShop)
                return false;
            foreach (var obj in handler.Value->EventObjects)
                if (obj.Value->NameString.Contains("补给负责人"))
                    return true;
            return false;
        }

        public void Build(uint id, Dictionary<uint, Interactevent> eventlist)
        {
            eventlist.Add(id, new Interactevent(Tag.Me, Name));
        }
    }
}
