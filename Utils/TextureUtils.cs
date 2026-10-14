using System.IO;
using UnityEngine;

namespace SimpleCrosshair.Utils
{
    public static class TextureUtils
    {
        public static Texture2D LoadTexture2DFromPath(string path)
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            ImageConversion.LoadImage(tex, File.ReadAllBytes(path));

            // don't let unity unload the texture on scene changes, we cache it
            tex.hideFlags = HideFlags.DontUnloadUnusedAsset;

            return tex;
        }
    }
}
