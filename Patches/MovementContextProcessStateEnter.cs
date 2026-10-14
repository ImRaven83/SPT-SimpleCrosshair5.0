using System.Reflection;
using EFT;
using HarmonyLib;
using SPTushonka.Reflection.Patching;

namespace SimpleCrosshair.Patches
{
    internal class MovementContextProcessStateEnterPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.Method(typeof(MovementContext), nameof(MovementContext.ProcessStateEnter));
        }

        [PatchPostfix]
        public static void PatchPostfix(MovementContext __instance)
        {
            // il2cpp wrappers aren't guaranteed to be the same managed object, so compare native pointers
            var player = GameUtils.Player;
            if (player == null || player.MovementContext == null ||
                __instance.Pointer != player.MovementContext.Pointer)
            {
                return;
            }

            Plugin.Instance.SimpleCrosshairComponent?.OnMovementStateChanged(__instance.CurrentState.Name);
        }
    }
}
