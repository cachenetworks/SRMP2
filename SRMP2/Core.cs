using MelonLoader;

[assembly: MelonInfo(typeof(SRMP2.Core), "SRMP2", "1.0.0", "lordp", null)]
[assembly: MelonGame("MonomiPark", "SlimeRancher2")]

namespace SRMP2
{
    public class Core : MelonMod
    {
        public override void OnInitializeMelon()
        {
            LoggerInstance.Msg("Initialized.");
        }
    }
}