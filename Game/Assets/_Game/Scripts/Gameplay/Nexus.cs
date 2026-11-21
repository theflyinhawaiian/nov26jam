using Jam.Net;
using UnityEngine;

namespace Jam.Gameplay
{
    /// <summary>
    /// Scene marker on each nexus block (Ground layer, so it blocks movement and shots).
    /// Health lives in the match's MatchState; clients fade the block as it drops.
    /// </summary>
    [RequireComponent(typeof(BoxCollider2D), typeof(SpriteRenderer))]
    public class Nexus : MonoBehaviour
    {
        public int Team;

        private SpriteRenderer _sprite;
        private Color _baseColor;
        private MatchState _state;

        public Collider2D Collider { get; private set; }

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
            _sprite.color = Color.Lerp(new Color(0.15f, 0.15f, 0.15f), _baseColor, t);
        }
    }
}
