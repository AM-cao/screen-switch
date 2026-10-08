namespace ScreenSwitch
{
    public enum Transition { None, Take, Release }

    public class OwnershipTracker
    {
        readonly int debounce;
        int okStreak, failStreak;
        bool owned;

        public OwnershipTracker(int debounce, bool initialOwned)
        {
            this.debounce = debounce < 1 ? 1 : debounce;
            owned = initialOwned;
        }

        public bool Owned { get { return owned; } }

        public void Reset(bool owned)
        {
            this.owned = owned;
            okStreak = 0;
            failStreak = 0;
        }

        public Transition Feed(bool readOk)
        {
            if (readOk)
            {
                okStreak++; failStreak = 0;
                if (!owned && okStreak >= debounce) { owned = true; okStreak = 0; return Transition.Take; }
            }
            else
            {
                failStreak++; okStreak = 0;
                if (owned && failStreak >= debounce) { owned = false; failStreak = 0; return Transition.Release; }
            }
            return Transition.None;
        }
    }
}
