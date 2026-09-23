namespace ServerMultipass
{
    public class MultipassMod : IModApi
    {
        public void InitMod(Mod _modInstance)
        {
            Multipass.Init(_modInstance);
        }
    }
}
