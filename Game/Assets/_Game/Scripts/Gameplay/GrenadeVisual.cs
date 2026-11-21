using UnityEngine;

namespace Jam.Gameplay
{
    /// <summary>
    /// Client-side cosmetic grenade. Stops on terrain and waits for the server's explosion.
    /// Observers receive grenades late, so the real grenade is already some way along its arc.
    /// Rather than jumping there (it would pop in away from the thrower), the visual starts at
    /// age 0 and plays the same arc with a slightly fast clock until it has caught up.
    /// That keeps the shape an exact parabola; only the speed is a little high for a moment.
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

        /// <summary>Seconds of arc still to absorb by running the clock fast.</summary>
        private float _behind;
        /// <summary>Clock rate while catching up (> 1).</summary>
        private float _catchUpRate = 1f;
        /// <summary>Thrower-as-drawn minus true origin; faded out while catching up.</summary>
        private Vector2 _offset;
        private float _offsetFade;
        private float _offsetElapsed;

        /// <param name="behindSeconds">How far along its arc the real grenade already is; absorbed over catchUpSeconds.</param>
        /// <param name="visualStart">Where the grenade should appear to start (the thrower as this client draws them).</param>
        public static GrenadeVisual Spawn(Vector2 origin, Vector2 velocity, WeaponDefinition weapon, PhysicsScene2D physics,
            float behindSeconds = 0f, float catchUpSeconds = 0f, Vector2? visualStart = null)
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
            // Give the server's explosion message time to arrive before giving up.
            g._lifetime = weapon.MaxLifetime + 1f;
            g._physics = physics;
            g._simPos = origin;
            if (behindSeconds > 0f && catchUpSeconds > 0f)
            {
                g._behind = behindSeconds;
                g._catchUpRate = 1f + behindSeconds / catchUpSeconds;
                g._offsetFade = catchUpSeconds;
                if (visualStart.HasValue)
                    g._offset = visualStart.Value - origin;
            }
            go.transform.position = g._simPos + g._offset;
            return g;
        }

        private void Update()
        {
            float dt = Time.deltaTime;
            if (_behind > 0f)
            {
                float extra = Mathf.Min(_behind, dt * (_catchUpRate - 1f));
                _behind -= extra;
                dt += extra;
            }
            _age += dt;

            if (!_stuck)
            {
                Vector2 prev = _simPos;
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

            // Fade the start offset out (smoothstep) over the catch-up period.
            float k = 0f;
            if (_offsetFade > 0f && _offsetElapsed < _offsetFade)
            {
                _offsetElapsed += Time.deltaTime;
                float t = Mathf.Clamp01(_offsetElapsed / _offsetFade);
                k = 1f - t * t * (3f - 2f * t);
            }
            transform.position = _simPos + _offset * k;

            if (_age > _lifetime)
                Destroy(gameObject);
        }
    }
}
