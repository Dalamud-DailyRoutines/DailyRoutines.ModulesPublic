using DailyRoutines.Common.Module.Abstractions;

namespace DailyRoutines.ModulesPublic;

public partial class AutoMarksFinder
{
    private sealed class Config : ModuleConfig
    {
        public List<string> RelayCommands = [];

        public string AutoRelayBeforeStartPattern = string.Empty;
        public string AutoRelayAfterEndPattern    = string.Empty;
        public string RelayPositionPattern        = Lang.Get("AutoMarksFinder-Relay-Default-Position");
        public string RelayWaitAetherytePattern   = Lang.Get("AutoMarksFinder-Relay-Default-WaitAetheryte");

        public HuntNavigationMode NavigationMode = HuntNavigationMode.Nearby;
        public float              NearbyDistance = 30;

        public bool MoveAfterBroadcast       = true;
        public bool PreferAetheryteBroadcast = true;

        public Dictionary<uint, List<NotoriousMonsterScanPoint>> ScanPoints    = [];
        public HashSet<uint>                                     SelectedZones = [];
        public List<NotoriousMonsterList>                        Trains        = [];
    }
}
