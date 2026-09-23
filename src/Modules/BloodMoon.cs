namespace ServerMultipass.Modules
{
    public sealed class BloodMoonSettings
    {
        [Range(0, 23)] public int NotifyHour;
        [Range(0)] public int NotifyEveryDays;
    }

    public sealed class BloodMoon : Module<BloodMoonSettings>
    {
        private int lastNotifiedDay = -1;

        public override string Name => "BloodMoon";

        protected internal override void RegisterCommands(ChatCommands commands)
        {
            commands.Add("bm", OnBloodMoon);
        }

        protected internal override void OnSecond(Tick tick)
        {
            if (tick.BloodMoonDay <= 0 || tick.Hour != Settings.NotifyHour || tick.Day == lastNotifiedDay) return;
            lastNotifiedDay = tick.Day;
            var daysLeft = tick.BloodMoonDay - tick.Day;
            if (daysLeft <= 0) Broadcast("Tonight");
            else if (Settings.NotifyEveryDays > 0 && daysLeft % Settings.NotifyEveryDays == 0) Broadcast("DaysLeft", daysLeft);
        }

        private void OnBloodMoon(ChatContext context)
        {
            var tick = Tick.Capture();
            if (tick == null) return;
            if (tick.BloodMoonDay <= 0) Reply(context.Client, "Disabled");
            else if (tick.BloodMoon) Reply(context.Client, "Active");
            else if (tick.BloodMoonDay - tick.Day <= 0) Reply(context.Client, "Tonight");
            else Reply(context.Client, "DaysLeft", tick.BloodMoonDay - tick.Day);
        }
    }
}
