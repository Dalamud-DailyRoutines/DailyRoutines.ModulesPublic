using DailyRoutines.Common.Module.Abstractions;

namespace DailyRoutines.ModulesPublic;

public partial class AutoMarksFinder
{
    private sealed class AutoMarksFinderConfig : ModuleConfig
    {
        public List<string> RelayCommands = [];

        public string AutoRelayBeforeStartPattern = string.Empty;
        public string AutoRelayAfterEndPattern    = string.Empty;
        public string RelayPositionPattern        = Lang.Get("AutoMarksFinder-Relay-Default-Position");
        public string RelayWaitAetherytePattern   = Lang.Get("AutoMarksFinder-Relay-Default-WaitAetheryte");

        public Dictionary<uint, List<NotoriousMonsterScanPoint>> ScanPoints    = [];
        public HashSet<uint>                                     SelectedZones = [];
        public List<NotoriousMonsterList>                        Trains        = [];
    }
}
