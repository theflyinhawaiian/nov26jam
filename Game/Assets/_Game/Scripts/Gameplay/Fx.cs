using UnityEngine;

namespace Jam.Gameplay
{
    /// <summary>Throwaway placeholder visuals built from a 1x1-unit white sprite.</summary>
    public static class Fx
    {
        private static Sprite _whiteSprite;

        public static Sprite WhiteSprite
        {
            get
            {
                if (_whiteSprite == null)
                {
                    Texture2D tex = Texture2D.whiteTexture;
                    _whiteSprite = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), tex.width);
                }
                return _whiteSprite;
            }
        }

        public static void Tracer(Vector2 from, Vector2 to, Color color, float duration = 0.08f, float width = 0.08f)
        {
            if (Application.isBatchMode)
                return;
            Vector2 delta = to - from;
            GameObject go = Quad("Tracer", (from + to) * 0.5f, new Vector2(delta.magnitude, width), color, 20);
            go.transform.rotation = Quaternion.Euler(0f, 0f, Mathf.Atan2(delta.y, delta.x) * Mathf.Rad2Deg);
            Object.Destroy(go, duration);
        }

        public static void Ghost(Vector2 center, Vector2 size, Color color, float duration = 0.6f)
        {
            if (Application.isBatchMode)
                return;
            Object.Destroy(Quad("Ghost", center, size, color, 15), duration);
        }

        public static void Explosion(Vector2 center, float radius)
        {
            if (Application.isBatchMode)
                return;
            Object.Destroy(Quad("Explosion", center, Vector2.one * radius * 2f, new Color(1f, 0.55f, 0.1f, 0.45f), 19), 0.15f);
        }

        /// <summary>A brief translucent square showing an area (heal, teleport, slow field, bolt impact).</summary>
        public static void Ring(Vector2 center, float radius, Color color, float duration = 0.25f)
        {
            if (Application.isBatchMode)
                return;
            Object.Destroy(Quad("Ring", center, Vector2.one * radius * 2f, color, 19), duration);
        }

        private static GameObject Quad(string name, Vector2 center, Vector2 size, Color color, int sortingOrder)
        {
            GameObject go = new(name);
            go.transform.position = center;
            go.transform.localScale = new Vector3(size.x, size.y, 1f);
            SpriteRenderer sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = WhiteSprite;
            sr.color = color;
            sr.sortingOrder = sortingOrder;
            return go;
        }
    }
}
