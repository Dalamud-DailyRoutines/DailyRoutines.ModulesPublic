using Lumina.Excel.Sheets;
using OmenTools.Interop.Game.Lumina;

namespace DailyRoutines.ModulesPublic;

public unsafe partial class UnifiedGlamourManager
{
    #region 职业筛选

    private static readonly JobFilterOption[] JOB_FILTER_OPTIONS = CreateJobFilterOptions();

    private static readonly string[] JOB_FILTER_NAMES = JOB_FILTER_OPTIONS.Select(x => x.Label).ToArray();

    private static JobFilterOption[] CreateJobFilterOptions()
    {
        var jobNames = GetClassJobNames();

        return
        [
            new(Lang.Get("UnifiedGlamourManager-JobFilter-AllJobs"), []),
            NativeJobFilter(jobNames, JobKind.GLA, JobKind.PLD),
            NativeJobFilter(jobNames, JobKind.MRD, JobKind.WAR),
            NativeJobFilter(jobNames, JobKind.DRK),
            NativeJobFilter(jobNames, JobKind.GNB),
            NativeJobFilter(jobNames, JobKind.CNJ, JobKind.WHM),
            NativeJobFilter(jobNames, JobKind.SCH),
            NativeJobFilter(jobNames, JobKind.AST),
            NativeJobFilter(jobNames, JobKind.SGE),
            NativeJobFilter(jobNames, JobKind.PGL, JobKind.MNK),
            NativeJobFilter(jobNames, JobKind.LNC, JobKind.DRG),
            NativeJobFilter(jobNames, JobKind.ROG, JobKind.NIN),
            NativeJobFilter(jobNames, JobKind.SAM),
            NativeJobFilter(jobNames, JobKind.RPR),
            NativeJobFilter(jobNames, JobKind.VPR),
            NativeJobFilter(jobNames, JobKind.ARC, JobKind.BRD),
            NativeJobFilter(jobNames, JobKind.MCH),
            NativeJobFilter(jobNames, JobKind.DNC),
            NativeJobFilter(jobNames, JobKind.THM, JobKind.BLM),
            NativeJobFilter(jobNames, JobKind.ACN, JobKind.SMN),
            NativeJobFilter(jobNames, JobKind.RDM),
            NativeJobFilter(jobNames, JobKind.BLU),
            NativeJobFilter(jobNames, JobKind.PCT),
            NativeJobFilter(jobNames, JobKind.CRP, JobKind.BSM, JobKind.ARM, JobKind.GSM, JobKind.LTW, JobKind.WVR, JobKind.ALC, JobKind.CUL),
            NativeJobFilter(jobNames, JobKind.MIN, JobKind.BTN, JobKind.FSH)
        ];
    }

    private static Dictionary<JobKind, string> GetClassJobNames()
    {
        Dictionary<JobKind, string> result = [];

        foreach (var job in LuminaGetter.Get<ClassJob>())
        {
            var abbreviation = job.Abbreviation.ExtractText();
            if (string.IsNullOrWhiteSpace(abbreviation))
                continue;

            if (!Enum.TryParse<JobKind>(abbreviation, out var kind))
                continue;

            var name = job.Name.ExtractText();
            result[kind] = string.IsNullOrWhiteSpace(name) ? abbreviation : name;
        }

        return result;
    }

    private static JobFilterOption NativeJobFilter(IReadOnlyDictionary<JobKind, string> jobNames, params JobKind[] jobs)
        => JobFilter(string.Join(" / ", jobs.Select(x => GetClassJobName(jobNames, x))), jobs);

    private static JobFilterOption JobFilter(string label, params JobKind[] jobs)
        => new(label, jobs);

    private static string GetClassJobName(IReadOnlyDictionary<JobKind, string> jobNames, JobKind job)
        => jobNames.TryGetValue(job, out var name) && !string.IsNullOrWhiteSpace(name)
            ? name
            : job.ToString();

    #endregion

    #region 投影模板槽位

    private static readonly PlateSlotDefinition[] PLATE_SLOT_DEFINITIONS =
    [
        new(0, "UnifiedGlamourManager-Slot-MainHand", x => x.MainHand != 0),
        new(1, "UnifiedGlamourManager-Slot-OffHand", x => x.OffHand != 0 && x.MainHand == 0),
        new(2, "UnifiedGlamourManager-Slot-Head", x => x.Head != 0),
        new(3, "UnifiedGlamourManager-Slot-Body", x => x.Body != 0),
        new(4, "UnifiedGlamourManager-Slot-Hands", x => x.Gloves != 0),
        new(5, "UnifiedGlamourManager-Slot-Legs", x => x.Legs != 0),
        new(6, "UnifiedGlamourManager-Slot-Feet", x => x.Feet != 0),
        new(7, "UnifiedGlamourManager-Slot-Earrings", x => x.Ears != 0),
        new(8, "UnifiedGlamourManager-Slot-Necklace", x => x.Neck != 0),
        new(9, "UnifiedGlamourManager-Slot-Bracelets", x => x.Wrists != 0),
        new(10, "UnifiedGlamourManager-Slot-LeftRing", x => x.FingerL != 0 || x.FingerR != 0),
        new(11, "UnifiedGlamourManager-Slot-RightRing", x => x.FingerL != 0 || x.FingerR != 0)
    ];

    #region 装备槽位

    private static readonly EquipSlotKind[] GLAMOUR_EQUIP_SLOT_KINDS =
    [
        EquipSlotKind.MainHand,
        EquipSlotKind.OffHand,
        EquipSlotKind.Head,
        EquipSlotKind.Body,
        EquipSlotKind.Hands,
        EquipSlotKind.Legs,
        EquipSlotKind.Feet,
        EquipSlotKind.Ears,
        EquipSlotKind.Neck,
        EquipSlotKind.Wrists,
        EquipSlotKind.Ring
    ];

    #endregion

    #endregion

    #region 排序

    private static readonly string[] SORT_MODE_NAMES =
    [
        Lang.Get("UnifiedGlamourManager-Sort-FavoriteThenNameAsc"),
        Lang.Get("UnifiedGlamourManager-Sort-NameAsc"),
        Lang.Get("UnifiedGlamourManager-Sort-NameDesc"),
        Lang.Get("UnifiedGlamourManager-Sort-LevelAsc"),
        Lang.Get("UnifiedGlamourManager-Sort-LevelDesc")
    ];

    #endregion

    #region 套装筛选

    private static readonly string[] SET_RELATION_FILTER_NAMES =
    [
        Lang.Get("UnifiedGlamourManager-SetFilter-All"),
        Lang.Get("UnifiedGlamourManager-SetFilter-SetRelatedOnly"),
        Lang.Get("UnifiedGlamourManager-SetFilter-NonSetOnly")
    ];

    private static readonly SetRelationFilter[] SET_RELATION_FILTER_VALUES =
    [
        SetRelationFilter.All,
        SetRelationFilter.SetRelatedOnly,
        SetRelationFilter.NonSetOnly
    ];

    #endregion
}
