using Jam.Net;
using UnityEngine;

namespace Jam.Gameplay
{
    /// <summary>
    /// Scene marker on each nexus block (Ground layer, so it blocks movement and shots).
    /// Health lives in the match's MatchState. It's shielded (no damage) while any of its team's
    /// towers stand. Clients fade the block as it drops and tint it pale while shielded.
    /// </summary>
    [RequireComponent(typeof(BoxCollider2D), typeof(SpriteRenderer))]
    public class Nexus : MonoBehaviour, IDamageable
    {
        public int Team;

        private SpriteRenderer _sprite;
        private Color _baseColor;
        private MatchState _state;

        public Collider2D Collider { get; private set; }
        /// <summary>Server only: the match whose arena copy this nexus is in.</summary>
        public Match Match { get; set; }

        int IDamageable.TeamId => Team;
        TargetKind IDamageable.Kind => TargetKind.Structure;
        bool IDamageable.Attackable => Match != null && Match.State != null && Match.State.IsLive
                                       && Match.State.NexusHealth(Team) > 0 && !Match.State.NexusShielded(Team);
        Rect IDamageable.Hitbox
        {
            get
            {
                Bounds b = Collider.bounds;
                return new Rect(b.min, b.size);
            }
        }

        public void ApplyDamage(int amount, DamageSource source)
        {
            if (source.Team == Team || Match == null || Match.State == null)
                return;
            Match.State.DamageNexus(Team, amount);
        }

        private void Awake()
        {
            Collider = GetComponent<BoxCollider2D>();
            _sprite = GetComponent<SpriteRenderer>();
            _baseColor = _sprite.color;
        }

        private void Update()
        {
            if (Application.isBatchMode)
                return;
            if (_state == null)
            {
                // A client only ever has its own match's state.
                _state = MatchState.Local;
                if (_state == null)
                    return;
            }
            float t = (float)_state.NexusHealth(Team) / MatchState.NexusMaxHealth;
            Color c = Color.Lerp(new Color(0.15f, 0.15f, 0.15f), _baseColor, t);
            if (_state.NexusShielded(Team))
                c = Color.Lerp(c, Color.white, 0.35f);
            _sprite.color = c;
        }
    }
}
