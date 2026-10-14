using System.Reflection;
using SPTushonka.Reflection.Patching;
using EFT;
using EFT.UI;
using EFT.UI.Screens;
using HarmonyLib;

namespace SimpleCrosshair.Patches
{
    /// <summary>
    /// BattleUIScreen.Show lives on a generic base class, which can't be reliably patched under il2cpp,
    /// so hook the raid screen controller's ShowAction instead, which is what shows the battle ui screen in raid.
    /// The hideout uses a different controller, so this is also not called in the hideout.
    /// </summary>
    internal class BattleUIScreenShowPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.Method(typeof(EftBattleUIScreen.RaidBattleUIScreenController),
                                      nameof(EftBattleUIScreen.RaidBattleUIScreenController.ShowAction));
        }

        [PatchPostfix]
        public static void PatchPostfix(BattleUIScreen<EftBattleUIScreen.EftBattleUIScreenController, EEftScreenType> screen)
        {
            // Don't attach if we're in the hideout, because OnRegisterPlayer isn't called in the hideout so other things don't work right
            if (!GameUtils.IsGameActive || screen == null) return;

            var eftScreen = screen.TryCast<EftBattleUIScreen>();
            if (eftScreen != null)
            {
                Plugin.Instance.TryAttachToBattleUIScreen(eftScreen);
            }
        }
    }
}
