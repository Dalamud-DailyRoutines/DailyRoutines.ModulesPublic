namespace DailyRoutines.ModulesPublic;

public partial class AutoMarksFinder
{
    private sealed class HuntSelection
    {
        private NotoriousMonsterFound? monster;

        public NotoriousMonsterList? Train { get; private set; }

        public int MonsterIndex
        {
            get;
            set
            {
                field   = Math.Clamp(value, 0, Math.Max(0, (Train?.MonstersFound.Count ?? 0) - 1));
                monster = Train?.MonstersFound.ElementAtOrDefault(field);
            }
        }

        public event Action? Changed;

        public void Select(NotoriousMonsterList? train)
        {
            if (ReferenceEquals(Train, train))
                return;

            if (Train != null)
                Train.Changed -= OnTrainChanged;

            Train = train;
            if (train != null)
                train.Changed += OnTrainChanged;

            MonsterIndex = 0;
            Changed?.Invoke();
        }

        private void OnTrainChanged()
        {
            var index = 0;

            if (Train != null)
            {
                for (var candidate = 0; candidate < Train.MonstersFound.Count; candidate++)
                {
                    if (!ReferenceEquals(Train.MonstersFound[candidate], monster))
                        continue;

                    index = candidate;
                    break;
                }
            }

            MonsterIndex = index;

            Changed?.Invoke();
        }
    }
}
