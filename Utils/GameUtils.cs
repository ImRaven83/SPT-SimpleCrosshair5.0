using Comfort.Common;
using EFT;

namespace SimpleCrosshair
{
    public static class GameUtils
    {
        public static Player Player => Singleton<GameWorld>.Instantiated ? Singleton<GameWorld>.Instance.MainPlayer : null;

        // under il2cpp the wrapper is of the requested type, so `is` won't work, check the actual il2cpp type instead
        public static bool IsGameActive => Singleton<AbstractGame>.Instantiated &&
                                           Singleton<AbstractGame>.Instance.TryCast<HideoutGame>() == null;
    }
}
