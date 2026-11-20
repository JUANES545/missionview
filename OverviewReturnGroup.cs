namespace WindowScatter
{
    internal sealed class OverviewReturnGroup
    {
        private readonly bool[] returned;
        private readonly Func<bool> isCurrent;
        private readonly Action complete;
        private bool completed;

        public OverviewReturnGroup(int views, Func<bool> isCurrent, Action complete)
        {
            if (views <= 0) throw new ArgumentOutOfRangeException(nameof(views));
            returned = new bool[views];
            this.isCurrent = isCurrent;
            this.complete = complete;
        }

        public void Returned(int index)
        {
            if (completed || !isCurrent()) return;
            returned[index] = true;
            if (returned.All(x => x)) Finish();
        }

        public void Finish()
        {
            if (completed || !isCurrent()) return;
            completed = true;
            complete();
        }
    }
}
