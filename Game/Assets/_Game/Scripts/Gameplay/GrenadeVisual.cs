using UnityEngine;

namespace Jam.Gameplay
{
    /// <summary>
    /// Client-side cosmetic grenade. Stops on terrain and waits for the server's explosion.
    /// Observers receive grenades late and fast-forward them, which puts the true arc well ahead
    /// of the (interpolation-delayed) thrower they see; so the visual can start at a different
    /// point and blend onto the true arc over a short time.
    /// </summary>
    public class GrenadeVisual : MonoBehaviour
    {
        private Vector2 _origin;
        private Vector2 _velocity;
        private float _gravity;
        private float _age;
        private float _lifetime;
        private PhysicsScene2D _physics;
        private bool _stuck;
        private Vector2 _simPos;
        private Vector2 _offset;
        private float _blendTime;
        private float _blendElapsed;

        /// <param name="visualStart">Where the grenade should appear to start (e.g. the thrower as this client sees them).</param>
        /// <param name="blendTime">Seconds to converge from visualStart onto the true arc.</param>
        public static GrenadeVisual Spawn(Vector2 origin, Vector2 velocity, WeaponDefinition weapon, float startAge, PhysicsScene2D physics,
            Vector2? visualStart = null, float blendTime = 0f)
        {
            GameObject go = new("Grenade");
            go.transform.localScale = Vector3.one * 0.4f;
            SpriteRenderer sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = Fx.WhiteSprite;
            sr.color = new Color(1f, 0.6f, 0.15f);
            sr.sortingOrder = 18;

            GrenadeVisual g = go.AddComponent<GrenadeVisual>();
            g._origin = origin;
            g._velocity = velocity;
            g._gravity = weapon.ProjectileGravity;
            g._age = startAge;
            // Give the server's explosion message time to arrive before giving up.
            g._lifetime = weapon.MaxLifetime + 1f;
            g._physics = physics;
            g._simPos = Grenade.PositionAt(origin, velocity, g._gravity, startAge);
            if (visualStart.HasValue && blendTime > 0f)
            {
                g._offset = visualStart.Value - g._simPos;
                g._blendTime = blendTime;
            }
            go.transform.position = g._simPos + g._offset;
            return g;
        }

        private void Update()
        {
            if (!_stuck)
            {
                Vector2 prev = _simPos;
                _age += Time.deltaTime;
                Vector2 next = Grenade.PositionAt(_origin, _velocity, _gravity, _age);
                Vector2 delta = next - prev;

                RaycastHit2D hit = _physics.Raycast(prev, delta.normalized, delta.magnitude, Layers.GroundMask);
                if (hit.collider != null)
                {
                    next = hit.point;
                    _stuck = true;
                }
                _simPos = next;
            }
            else
            {
                _age += Time.deltaTime;
            }

            // Ease the start offset out (smoothstep), so the grenade leaves the thrower and catches up.
            float k = 0f;
            if (_blendTime > 0f && _blendElapsed < _blendTime)
            {
                _blendElapsed += Time.deltaTime;
                float t = Mathf.Clamp01(_blendElapsed / _blendTime);
                k = 1f - t * t * (3f - 2f * t);
            }
            transform.position = _simPos + _offset * k;

            if (_age > _lifetime)
                Destroy(gameObject);
        }
    }
}
