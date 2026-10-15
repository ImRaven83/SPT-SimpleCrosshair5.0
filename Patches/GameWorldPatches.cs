using System.Reflection;
using EFT;
using HarmonyLib;
using SPTushonka.Reflection.Patching;

namespace SimpleCrosshair.Patches
{
    internal class GameWorldRegisterPlayerPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.Method(typeof(GameWorld), nameof(GameWorld.RegisterPlayer));
        }

        [PatchPostfix]
        public static void PatchPostfix(IPlayer iPlayer)
        {
            OnRegisterPlayer(iPlayer);
        }

        internal static void OnRegisterPlayer(IPlayer player)
        {
            if (player == null || !player.IsYourPlayer)
            {
                return;
            }

            Plugin.Instance.SimpleCrosshairComponent?.OnRegisterMainPlayer();
        }
    }

    /// <summary>
    /// In raid the game world is a ClientGameWorld, which overrides RegisterPlayer, and under il2cpp the base
    /// method may not go through the GameWorld detour, so patch the override too
    /// </summary>
    internal class ClientGameWorldRegisterPlayerPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.Method(typeof(ClientGameWorld), nameof(ClientGameWorld.RegisterPlayer));
        }

        [PatchPostfix]
        public static void PatchPostfix(IPlayer iPlayer)
        {
            GameWorldRegisterPlayerPatch.OnRegisterPlayer(iPlayer);
        }
    }

    internal class GameWorldUnregisterPlayerPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.Method(typeof(GameWorld), nameof(GameWorld.UnregisterPlayer));
        }

        [PatchPostfix]
        public static void PatchPostfix(IPlayer iPlayer)
        {
            OnUnregisterPlayer(iPlayer);
        }

        internal static void OnUnregisterPlayer(IPlayer player)
        {
            if (player == null || !player.IsYourPlayer)
            {
                return;
            }

            Plugin.Instance.SimpleCrosshairComponent?.OnUnregisterMainPlayer();
            Plugin.Instance.DestroyCrosshair();
        }
    }

    internal class ClientGameWorldUnregisterPlayerPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.Method(typeof(ClientGameWorld), nameof(ClientGameWorld.UnregisterPlayer));
        }

        // the override names its parameter "player", unlike the base method
        [PatchPostfix]
        public static void PatchPostfix(IPlayer player)
        {
            GameWorldUnregisterPlayerPatch.OnUnregisterPlayer(player);
        }
    }
}
