namespace ServerMultipass.Modules
{
    public sealed class Welcome : Module
    {
        public override string Name => "Welcome";

        protected internal override void OnPlayerSpawned(ClientInfo client, RespawnType reason)
        {
            if (reason != RespawnType.EnterMultiplayer) return;
            var name = Players.Name(client);
            foreach (var other in Players.Online()) Chat.Send(other, Text(other, "Welcome", name));
        }
    }
}
