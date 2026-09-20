using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using OmenTools.OmenService.ZoneIndicator;
using ModuleBase = DailyRoutines.Common.Module.Abstractions.ModuleBase;

namespace DailyRoutines.ModulesPublic.Interface;

public class AutoDisplayMTQObject : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoDisplayMTQObjectTitle"),
        Description = Lang.Get("AutoDisplayMTQObjectDescription"),
        Category    = ModuleCategory.Interface
    };
    
    private static readonly List<IGameObject> QuestObjects = [];

    private ZoneIndicatorHandle? handle;

    protected override void Init()
    {
        IClientState.Instance().TerritoryChanged += OnTerritoryChanged;
        OnTerritoryChanged(0);
    }

    protected override void Uninit()
    {
        IClientState.Instance().TerritoryChanged -= OnTerritoryChanged;

        handle?.Unreg();
        handle = null;
    }

    private void OnTerritoryChanged
    (
        uint territoryType
    )
    {
        handle?.Unreg();

        handle = ZoneIndicatorRenderer.Instance().RegTemporary
        (
            GetQuestObjects,
            static x => x.Position,
            new()
            {
                TextGetter = static x => new()
                {
                    Text      = x.Name,
                    TextColor = KnownColor.LawnGreen.ToVector4(),
                    Image     = GetIcon(x.NamePlateIconID)
                }
            }
        );
    }

    private static unsafe List<IGameObject> GetQuestObjects()
    {
        QuestObjects.Clear();

        var questManager = QuestManager.Instance();
        if (questManager == null) 
            return QuestObjects;
        
        if (ICondition.Instance().IsOccupiedInEvent)
            return QuestObjects;

        var objectTable = IObjectTable.Instance();

        foreach (ref var pair in EventFramework.Instance()->EventHandlerModule.EventHandlerMap)
        {
            var eventHandler = pair.Item2.Value;
            if (eventHandler == null) continue;

            if (eventHandler->Info.EventId.ContentId                           != EventHandlerContent.Quest) continue;
            if (questManager->GetQuestById(eventHandler->Info.EventId.EntryId) == null) continue;

            foreach (var eventObject in eventHandler->EventObjects)
            {
                var gameObject = eventObject.Value;
                if (gameObject == null) continue;
                if (!gameObject->TargetableStatus.IsSet(ObjectTargetableFlags.ReadyToDraw)) continue;
                if (!eventHandler->IsActive(gameObject)) continue;

                if (objectTable[gameObject->ObjectIndex] is not { } reference) continue;
                if (reference.ToStruct() != gameObject || QuestObjects.Contains(reference)) continue;

                QuestObjects.Add(reference);
            }
        }

        return QuestObjects;
    }

    private static ZoneIndicatorText.TextImage? GetIcon
    (
        uint iconID
    )
    {
        if (!ITextureProvider.Instance().TryGetFromGameIcon(new(iconID), out var texture)) return null;

        return new()
        {
            Texture    = texture,
            SizeGetter = static () => new(ImGui.GetTextLineHeightWithSpacing())
        };
    }
}
