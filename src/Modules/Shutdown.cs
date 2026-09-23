using System;
using System.Collections.Generic;
using System.Linq;

namespace ServerMultipass.Modules
{
    public sealed class ShutdownSettings
    {
        public string Schedule;
        [Range(1)] public int[] AlertMinutes;
        public bool AlertOnLogin;
        [Range(0)] public int JoinAlertMinutes;
        [Range(0)] public int FinalWarningMinutes;
        [Range(0)] public int KickLeadSeconds;
        [Range(0)] public int ShutdownGraceSeconds;
        [Range(0)] public int ShutdownTimeoutSeconds;
        public bool FreezeDuringBloodMoon;
        public string[] ChatCommands;
    }

    public sealed class Shutdown : Module<ShutdownSettings>
    {
        private readonly ShutdownSchedule schedule = new ShutdownSchedule();
        private State state;
        private DateTime kickTime;
        private string appliedSchedule;

        private enum State
        {
            Idle,
            Draining,
            Closing
        }

        public override string Name => "Shutdown";
        protected internal override IEnumerable<string> ConsoleCommands => new[] { "mp-shutdown" };

        protected override void OnEnable()
        {
            state = State.Idle;
            schedule.Configure(Settings.AlertMinutes, Settings.KickLeadSeconds);
            Apply(Settings.Schedule);
        }

        protected override void OnSettingsChanged()
        {
            schedule.Configure(Settings.AlertMinutes, Settings.KickLeadSeconds);
            if (state == State.Idle && Settings.Schedule != appliedSchedule) Apply(Settings.Schedule);
        }

        protected override void OnDisable()
        {
            schedule.Clear();
            state = State.Idle;
        }

        protected internal override void RegisterCommands(ChatCommands commands)
        {
            foreach (var name in Settings.ChatCommands) commands.Add(name, OnCheck);
        }

        protected internal override void OnSecond(Tick tick)
        {
            if (state != State.Idle)
            {
                Drain(tick.Now);
                return;
            }
            var result = schedule.Poll(tick.Now, Frozen(tick.BloodMoon));
            foreach (var minutes in result.Alerts)
                foreach (var client in Players.Online())
                    Alert(client, TimeSpan.FromMinutes(minutes));
            if (result.Fire) Begin();
        }

        protected internal override void OnPlayerSpawned(ClientInfo client, RespawnType reason)
        {
            if (reason != RespawnType.EnterMultiplayer && reason != RespawnType.JoinMultiplayer) return;
            if (state != State.Idle)
            {
                Players.Kick(client, Text(client, "KickReason"));
                return;
            }
            var left = schedule.Left(DateTime.Now);
            if (left == null) return;
            if (!Settings.AlertOnLogin && left.Value.TotalMinutes > Settings.JoinAlertMinutes) return;
            Alert(client, left.Value);
        }

        internal string Status()
        {
            if (state != State.Idle) return "The server is stopping right now";
            var left = schedule.Left(DateTime.Now);
            if (left == null) return "No restart is planned";
            var tick = Tick.Capture();
            var paused = tick != null && Frozen(tick.BloodMoon) ? ", the countdown is paused during the blood moon" : "";
            return $"Next restart at {schedule.Next:yyyy-MM-dd HH:mm:ss}, in {Durations.Format(left.Value)}{paused}";
        }

        internal string Reschedule(string value)
        {
            if (state != State.Idle) return "The server is already stopping";
            if (!schedule.Set(value, DateTime.Now)) return $"'{value}' is not valid. Use minutes from now (10), a time (03:00) or several times (03:00,15:00)";
            Info($"restart moved by the console to {schedule.Next:yyyy-MM-dd HH:mm:ss}");
            return Status();
        }

        internal string Cancel()
        {
            if (state != State.Idle) return "The server is already stopping";
            schedule.Clear();
            Info("planned restart cancelled by the console");
            return "The planned restart is cancelled until the server restarts or Schedule is changed in the settings";
        }

        private void OnCheck(ChatContext context)
        {
            var left = schedule.Left(DateTime.Now);
            if (left == null)
            {
                Reply(context.Client, "NoShutdown");
                return;
            }
            var time = Durations.Format(left.Value);
            var tick = Tick.Capture();
            if (tick != null && Frozen(tick.BloodMoon)) time += Text(context.Client, "FrozenSuffix");
            Reply(context.Client, "Alert", time);
            if (left.Value.TotalMinutes <= Settings.FinalWarningMinutes) Reply(context.Client, "LastMinuteWarning");
        }

        private void Apply(string value)
        {
            appliedSchedule = value;
            if (schedule.Set(value, DateTime.Now)) Info($"next restart at {schedule.Next:yyyy-MM-dd HH:mm:ss}");
            else Warn($"Schedule '{value}' is not valid, no restart is planned. Use minutes (240), a time (03:00) or several times (03:00,15:00)");
        }

        private void Alert(ClientInfo client, TimeSpan left)
        {
            Reply(client, "Alert", Durations.Format(left));
            if (left.TotalMinutes <= Settings.FinalWarningMinutes) Reply(client, "LastMinuteWarning");
        }

        private bool Frozen(bool bloodMoon)
        {
            return Settings.FreezeDuringBloodMoon && bloodMoon;
        }

        private void Begin()
        {
            state = State.Draining;
            kickTime = DateTime.Now;
            Info("scheduled restart, disconnecting players");
            foreach (var client in Players.Online()) Players.Kick(client, Text(client, "KickReason"));
        }

        private void Drain(DateTime now)
        {
            if (state != State.Draining) return;
            var elapsed = (now - kickTime).TotalSeconds;
            if (elapsed < Settings.ShutdownGraceSeconds) return;
            var connected = ConnectionManager.Instance?.Clients?.Count ?? 0;
            if (connected > 0 && elapsed < Math.Max(Settings.ShutdownTimeoutSeconds, Settings.ShutdownGraceSeconds)) return;
            if (connected > 0) Warn($"{connected} client(s) still connected after {Settings.ShutdownTimeoutSeconds}s, stopping the server anyway");
            else Info("all players disconnected, stopping the server");
            state = State.Closing;
            UnityEngine.Application.Quit();
        }
    }

    internal sealed class ShutdownSchedule
    {
        private readonly HashSet<int> fired = new HashSet<int>();
        private int[] alertMinutes = new int[0];
        private int leadSeconds;
        private DateTime? lastPoll;
        private bool firing;

        public DateTime? Next { get; private set; }

        public void Configure(int[] alerts, int kickLeadSeconds)
        {
            alertMinutes = alerts ?? new int[0];
            leadSeconds = Math.Max(0, kickLeadSeconds);
        }

        public bool Set(string value, DateTime now)
        {
            var next = Parse(value, now);
            if (next == null) return false;
            Next = next;
            fired.Clear();
            lastPoll = null;
            firing = false;
            return true;
        }

        public void Clear()
        {
            Next = null;
            fired.Clear();
            lastPoll = null;
            firing = false;
        }

        public TimeSpan? Left(DateTime now)
        {
            if (Next == null) return null;
            var left = Next.Value - now;
            return left < TimeSpan.Zero ? TimeSpan.Zero : left;
        }

        public (bool Fire, List<int> Alerts) Poll(DateTime now, bool frozen)
        {
            var alerts = new List<int>();
            var previous = lastPoll;
            lastPoll = now;
            if (Next == null) return (false, alerts);
            if (frozen)
            {
                if (previous.HasValue && now > previous.Value) Next = Next.Value + (now - previous.Value);
                return (false, alerts);
            }
            var seconds = (Next.Value - now).TotalSeconds;
            if (seconds <= leadSeconds)
            {
                if (firing) return (false, alerts);
                firing = true;
                return (true, alerts);
            }
            var minutes = seconds / 60d;
            foreach (var mark in alertMinutes.Distinct())
            {
                if (fired.Contains(mark) || minutes > mark || minutes <= mark - 1) continue;
                fired.Add(mark);
                alerts.Add(mark);
            }
            return (false, alerts);
        }

        public static DateTime? Parse(string value, DateTime now)
        {
            var text = (value ?? "").Trim();
            if (text.Length == 0) return null;
            if (text.IndexOf(':') < 0) return int.TryParse(text, out var minutes) && minutes > 0 ? now.AddMinutes(minutes) : (DateTime?)null;
            DateTime? best = null;
            foreach (var entry in text.Split(','))
            {
                var parts = entry.Trim().Split(':');
                if (parts.Length != 2 || !int.TryParse(parts[0], out var hour) || !int.TryParse(parts[1], out var minute)
                    || hour < 0 || hour > 23 || minute < 0 || minute > 59)
                    return null;
                var candidate = now.Date.AddHours(hour).AddMinutes(minute);
                if (candidate <= now) candidate = candidate.AddDays(1);
                if (best == null || candidate < best.Value) best = candidate;
            }
            return best;
        }
    }

    public class ShutdownConsole : ConsoleCmdAbstract
    {
        public override int DefaultPermissionLevel => 0;

        public override string[] getCommands()
        {
            return new[] { "mp-shutdown" };
        }

        public override string getDescription()
        {
            return "Server Multipass: time until the scheduled restart, move or cancel it";
        }

        public override string getHelp()
        {
            return "Usage:\n" +
                   "  mp-shutdown             time until the next restart\n" +
                   "  mp-shutdown <schedule>  plan the next restart: minutes from now (10), a time (03:00) or several times (03:00,15:00)\n" +
                   "  mp-shutdown off         cancel the planned restart until the server restarts or Schedule is changed in the settings";
        }

        public override void Execute(List<string> _params, CommandSenderInfo _senderInfo)
        {
            var module = Multipass.Get<Shutdown>();
            if (module == null || !module.Ready)
            {
                SdtdConsole.Instance.Output("Module Shutdown is off. Turn it on with: mp enable Shutdown");
                return;
            }
            string result;
            if (_params.Count == 0) result = module.Status();
            else if (_params[0].Equals("off", StringComparison.OrdinalIgnoreCase)) result = module.Cancel();
            else result = module.Reschedule(string.Join("", _params));
            SdtdConsole.Instance.Output(result);
        }
    }
}
