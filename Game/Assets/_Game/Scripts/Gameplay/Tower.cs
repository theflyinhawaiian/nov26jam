using Jam.Net;
using UnityEngine;

namespace Jam.Gameplay
{
    /// <summary>
    /// Scene marker on each tower block (visual only: no collider, so friendly players and
    /// minions walk through; enemy minions stop to attack it). Health lives in the match's
    /// MatchState; the server's LaneDirector does the shooting. Clients fade the block as it
    /// drops and leave a dim stump once destroyed.
    /// </summary>
    [RequireComponent(typeof(SpriteRenderer))]
    public class Tower : MonoBehaviour, IDamageable
    {
        public int Team;
        public Lane Lane;

        private SpriteRenderer _sprite;
        private Color _baseColor;
        private MatchState _state;

        /// <summary>Server only: the match whose arena copy this tower is in.</summary>
        public Match Match { get; set; }
        public int Index => LaneLayout.TowerIndex(Team, Lane);
        private ArenaDef _arena;
        public ArenaDef Arena => _arena ??= Arenas.For(gameObject);
        public Rect Rect => Arena.TowerRect(Team, Lane);
        /// <summary>Where shots come from: near the top.</summary>
        public Vector2 Muzzle => Arena.TowerMuzzle(Team, Lane);

        private MatchState State => Match != null ? Match.State : MatchState.Local;
        public bool Alive => State != null && State.TowerHealth.Count > Index && State.TowerHealth[Index] > 0;

        int IDamageable.TeamId => Team;
        TargetKind IDamageable.Kind => TargetKind.Structure;
        bool IDamageable.Attackable => Alive && State.IsLive;
        Rect IDamageable.Hitbox => Rect;

        public void ApplyDamage(int amount, DamageSource source)
        {
            if (source.Team == Team || Match == null || Match.State == null)
                return;
            Match.State.DamageTower(Index, amount);
        }

        private void Awake()
        {
            _sprite = GetComponent<SpriteRenderer>();
            _baseColor = _sprite.color;
        }

        private void Update()
        {
            if (Application.isBatchMode)
                return;
            if (_state == null)
                _state = MatchState.Local;
            if (_state == null || _state.TowerHealth.Count <= Index)
                return;
            int hp = _state.TowerHealth[Index];
            float t = (float)hp / MatchState.TowerMaxHealth;
            _sprite.color = hp > 0
                ? Color.Lerp(new Color(0.2f, 0.2f, 0.2f), _baseColor, 0.35f + 0.65f * t)
                : new Color(0.15f, 0.15f, 0.15f, 0.5f);
            transform.localScale = new Vector3(LaneLayout.TowerWidth, hp > 0 ? LaneLayout.TowerHeight : 0.8f, 1f);
            Vector2 c = Arena.TowerCenter(Team, Lane);
            transform.position = hp > 0 ? c : new Vector2(c.x, Arena.FloorY(Lane) + 0.4f);
        }
    }
}
