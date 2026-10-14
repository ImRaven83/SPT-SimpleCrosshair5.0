using BepInEx;
using BepInEx.Unity.IL2CPP.Configuration;
using UnityEngine;

namespace SimpleCrosshair.Utils
{
    public static class UIUtils
    {
        // under il2cpp, `transform as RectTransform` doesn't work since the wrapper is a Transform, so TryCast instead
        public static RectTransform GetRectTransform(this GameObject gameObject)
        {
            return gameObject.transform.TryCast<RectTransform>();
        }

        public static RectTransform GetRectTransform(this Component component)
        {
            return component.transform.TryCast<RectTransform>();
        }

        public static void ResetTransform(this GameObject gameObject)
        {
            gameObject.transform.localPosition = Vector3.zero;
            gameObject.transform.localRotation = Quaternion.identity;
            gameObject.transform.localScale = Vector3.one;
        }

        /// <summary>
        /// KeyboardShortcut.IsPressed is awful and doesn't allow other buttons to be pressed during
        /// </summary>
        public static bool BetterIsPressed(this KeyboardShortcut key)
        {
            if (!UnityInput.Current.GetKey(key.MainKey))
            {
                return false;
            }

            foreach (var modifier in key.Modifiers)
            {
                if (!UnityInput.Current.GetKey(modifier))
                {
                    return false;
                }
            }

            return true;
        }
    }
}
