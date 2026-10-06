namespace ServerMultipass.Modules
{
    public sealed class WelcomeSettings
    {
        public bool GreetNewPlayers;
        public bool GreetReturningPlayers;
    }

    public sealed class Welcome : Module<WelcomeSettings>
    {
        public override string Name => "Welcome";

        protected internal override void OnPlayerSpawned(ClientInfo client, RespawnType reason)
        {
            var name = Players.Name(client);
            if (reason == RespawnType.EnterMultiplayer && Settings.GreetNewPlayers) Broadcast("Welcome", name);
            else if (reason == RespawnType.JoinMultiplayer && Settings.GreetReturningPlayers) Reply(client, "WelcomeBack", name);
        }
    }
}
