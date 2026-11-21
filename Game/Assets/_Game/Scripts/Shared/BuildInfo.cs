using UnityEngine;

namespace Jam
{
    /// <summary>
    /// Build stamp ("&lt;git hash&gt;[-dirty] &lt;UTC time&gt;") written to Resources/BuildInfo.txt by
    /// BuildScripts right before each player build. Shows "editor" when running in the editor.
    /// </summary>
    public static class BuildInfo
    {
        public const string ResourceName = "BuildInfo";

        private static string _version;

        public static string Version
        {
            get
            {
                if (_version == null)
                {
                    TextAsset asset = Application.isEditor ? null : Resources.Load<TextAsset>(ResourceName);
                    _version = asset != null ? asset.text.Trim() : "editor";
                }
                return _version;
            }
        }
    }
}
