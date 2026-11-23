using System.Collections.Generic;
using UnityEngine;

namespace Jam.UI
{
    /// <summary>
    /// Emoji icons for the shop and HUD: PNGs in Resources/Icons (Twemoji, CC-BY 4.0; see
    /// Resources/Icons/CREDITS.txt). Images rather than font glyphs so they show in color on
    /// every platform, including WebGL, which has no OS emoji font.
    /// </summary>
    public static class Icons
    {
        private static readonly Dictionary<string, Texture2D> Cache = new();

        public static Texture2D Get(string name)
        {
            if (string.IsNullOrEmpty(name))
                return null;
            if (!Cache.TryGetValue(name, out Texture2D tex))
            {
                tex = Resources.Load<Texture2D>("Icons/" + name);
                Cache[name] = tex;
            }
            return tex;
        }
    }
}
