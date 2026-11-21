using UnityEngine;

namespace Jam.Gameplay
{
    /// <summary>Client-side cosmetic grenade. Stops on terrain and waits for the server's explosion.</summary>
    public class GrenadeVisual : MonoBehaviour
    {
        private Vector2 _origin;
        private Vector2 _velocity;
        private float _gravity;
        private float _age;
        private float _lifetime;
        private PhysicsScene2D _physics;
        private bool _stuck;

        public static GrenadeVisual Spawn(Vector2 origin, Vector2 velocity, WeaponDefinition weapon, float startAge, PhysicsScene2D physics)
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
            go.transform.position = Grenade.PositionAt(origin, velocity, g._gravity, startAge);
            return g;
        }

        private void Update()
        {
            if (_stuck)
                return;

            Vector2 prev = transform.position;
            _age += Time.deltaTime;
            Vector2 next = Grenade.PositionAt(_origin, _velocity, _gravity, _age);
            Vector2 delta = next - prev;

            RaycastHit2D hit = _physics.Raycast(prev, delta.normalized, delta.magnitude, Layers.GroundMask);
            if (hit.collider != null)
            {
                next = hit.point;
                _stuck = true;
            }
            transform.position = next;

            if (_age > _lifetime)
                Destroy(gameObject);
        }
    }
}
