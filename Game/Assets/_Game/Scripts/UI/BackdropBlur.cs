using UnityEngine;

namespace Jam.UI
{
    /// <summary>
    /// A live blurred copy of the game view for behind modal menus (the shop). Each frame it's
    /// active, a hidden camera copying the main camera renders into a small texture; drawing
    /// that texture full-screen with bilinear filtering and a few offset taps blurs it.
    /// </summary>
    public class BackdropBlur
    {
        /// <summary>Render at 1/Downscale of the screen size: higher is blurrier.</summary>
        private const int Downscale = 8;
        private static readonly Color Tint = new(0.03f, 0.03f, 0.05f, 0.35f);

        private Camera _cam;
        private RenderTexture _rt;

        /// <summary>Call from LateUpdate while the backdrop is shown.</summary>
        public void Capture()
        {
            Camera main = Camera.main;
            if (main == null)
                return;
            int w = Mathf.Max(16, Screen.width / Downscale), h = Mathf.Max(9, Screen.height / Downscale);
            if (_rt == null || _rt.width != w || _rt.height != h)
            {
                if (_rt != null)
                    _rt.Release();
                _rt = new RenderTexture(w, h, 16) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
            }
            if (_cam == null)
            {
                GameObject go = new("Backdrop Blur Camera") { hideFlags = HideFlags.HideAndDontSave };
                _cam = go.AddComponent<Camera>();
                _cam.enabled = false;
            }
            _cam.CopyFrom(main);
            _cam.targetTexture = _rt;
            _cam.Render();
            _cam.targetTexture = null;
        }

        /// <summary>Call from OnGUI: covers the screen with the blurred view, then a dark tint.</summary>
        public void Draw()
        {
            if (_rt == null || Event.current.type != EventType.Repaint)
                return;
            Matrix4x4 old = GUI.matrix;
            Color oldColor = GUI.color;
            GUI.matrix = Matrix4x4.identity;
            Rect full = new(0f, 0f, Screen.width, Screen.height);
            GUI.DrawTexture(full, _rt, ScaleMode.StretchToFill, false);
            // Offset taps (a texel apart) soften the upscaled blocks into a smoother blur.
            float o = Downscale * 0.75f;
            GUI.color = new Color(1f, 1f, 1f, 0.35f);
            foreach (Vector2 d in new[] { new Vector2(o, 0f), new Vector2(-o, 0f), new Vector2(0f, o), new Vector2(0f, -o) })
                GUI.DrawTexture(new Rect(full.x + d.x, full.y + d.y, full.width, full.height), _rt, ScaleMode.StretchToFill, false);
            GUI.color = Tint;
            GUI.DrawTexture(full, Texture2D.whiteTexture);
            GUI.color = oldColor;
            GUI.matrix = old;
        }
    }
}
