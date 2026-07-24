using System.Numerics;
using DailyRoutines.Common.Module.Abstractions;

namespace DailyRoutines.ModulesPublic.Interface.CustomizeMapMarker;

public partial class CustomizeMapMarker
{
    private sealed class Config : ModuleConfig
    {
        public List<MarkerRecord> Markers { get; set; } = [];
    }

    private sealed class MarkerRecord
    {
        public Guid     ID              { get; set; } = Guid.NewGuid();
        public uint     TerritoryID     { get; set; }
        public uint     MapID           { get; set; }
        public Vector2  TexturePosition { get; set; }
        public string   Name            { get; set; } = string.Empty;
        public string   Group           { get; set; } = Lang.Get("CustomizeMapMarker-DefaultGroup");
        public string   Description     { get; set; } = string.Empty;
        public uint     IconID          { get; set; } = DEFAULT_ICON_ID;
        public DateTime CreatedAt       { get; set; } = DateTime.UtcNow;
    }

    private sealed class MarkerPackage
    {
        public int Version { get; set; } = 1;
        public List<MarkerRecord> Markers { get; set; } = [];
    }
}
