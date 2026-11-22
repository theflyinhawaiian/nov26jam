using System.Collections.Generic;
using FishNet.Object;
using FishNet.Object.Synchronizing;
using Jam.Net;
using Jam.UI;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Jam.Gameplay
{
    /// <summary>
    /// The player's Q ability (see <see cref="Abilities"/>) and their shop offers. The owner
    /// presses Q to aim at the cursor; the server validates (holding it, alive, not stunned or
    /// silenced, off cooldown) and applies the effect. Grenade goes through PlayerWeapons'
    /// projectile path (its weapon slot 1); the rest are resolved here.
    /// </summary>
    [RequireComponent(typeof(PlayerAvatar))]
    public class PlayerAbilities : NetworkBehaviour
    {
        /// <summary>The weapon slot holding the GrenadeLauncher definition.</summary>
        public const int GrenadeWeaponIndex = 1;
        /// <summary>Server accepts a use this early, for clock jitter between owner and server.</summary>
        private const float CooldownSlackSeconds = 0.2f;

        public readonly SyncVar<AbilityId> Current = new(AbilityId.Grenade);
        /// <summary>The two abilities the shop currently offers this player.</summary>
        public readonly SyncVar<AbilityId> OfferA = new();
        public readonly SyncVar<AbilityId> OfferB = new();

        private PlayerAvatar _avatar;
        /// <summary>Owner: when Q is next usable (local prediction). Server: when it's next allowed.</summary>
        private float _readyAt;
        /// <summary>Owner: cooldown length of the last use, for the HUD sweep.</summary>
        private float _lastCooldown = 1f;

        private struct Bolt
        {
            public ushort Id;
            public Vector2 Origin;
            public Vector2 Dir;
            public float Travelled;
        }

        private readonly List<Bolt> _serverBolts = new();
        private ushort _nextBoltId;
        private readonly Dictionary<ushort, (GameObject go, Vector2 origin, Vector2 velocity, float startTime)> _boltVisuals = new();

        /// <summary>Server only: uses refused or applied, for logs and smoke tests.</summary>
        public int Uses { get; private set; }

        public AbilityDef CurrentDef => Abilities.Get(Current.Value);
        public AbilityId Offer(int slot) => slot == 0 ? OfferA.Value : OfferB.Value;

        /// <summary>Owner: seconds until Q is ready (Grenade uses the weapon's own cooldown).</summary>
        public float CooldownRemaining => Current.Value == AbilityId.Grenade
            ? _avatar.Weapons.CooldownRemaining(GrenadeWeaponIndex)
            : Mathf.Max(0f, _readyAt - Time.time);

        public float CooldownTotal => Current.Value == AbilityId.Grenade
            ? _avatar.Weapons.Get(GrenadeWeaponIndex).Cooldown
            : _lastCooldown;

        private void Awake() => _avatar = GetComponent<PlayerAvatar>();

        public override void OnStartServer()
        {
            RollOffers(rollA: true, rollB: true);
            TimeManager.OnTick += SimulateBolts;
        }

        public override void OnStopServer() => TimeManager.OnTick -= SimulateBolts;

        public override void OnStopClient()
        {
            foreach (var v in _boltVisuals.Values)
                if (v.go != null)
                    Destroy(v.go);
            _boltVisuals.Clear();
        }

        private void Update()
        {
            UpdateBoltVisuals();
            if (!IsOwner || _avatar.ScriptedInput.HasValue)
                return;
            Keyboard k = Keyboard.current;
            Mouse mouse = Mouse.current;
            Camera cam = Camera.main;
            if (k != null && k.qKey.wasPressedThisFrame && mouse != null && cam != null && !GameMenu.CapturesPointer)
                TryUse(cam.ScreenToWorldPoint(mouse.position.ReadValue()));
        }

        #region Using (owner → server)
        /// <summary>Owner only: use the Q ability aimed at a world point. Public for the smoke-test driver.</summary>
        public bool TryUse(Vector2 aim)
        {
            if (!IsOwner || _avatar.IsDead.Value || !_avatar.MatchLive || _avatar.Stunned || _avatar.Silenced)
                return false;
            AbilityId id = Current.Value;
            if (id == AbilityId.Grenade)
                return _avatar.Weapons.FireAt(aim, GrenadeWeaponIndex);
            if (Time.time < _readyAt)
                return false;

            PlayerAvatar target = null;
            if (id == AbilityId.Silence)
            {
                target = PickSilenceTarget(aim);
                if (target == null)
                    return false;
            }
            AbilityDef def = Abilities.Get(id);
            _readyAt = Time.time + def.Cooldown;
            _lastCooldown = def.Cooldown;
            ServerUse(id, transform.position, aim, target);
            return true;
        }

        /// <summary>The enemy player nearest the cursor, if within the pick radius and silence range.</summary>
        private PlayerAvatar PickSilenceTarget(Vector2 aim)
        {
            PlayerAvatar best = null;
            float bestD = Abilities.SilencePickRadius;
            foreach (PlayerAvatar a in FindObjectsByType<PlayerAvatar>(FindObjectsSortMode.None))
            {
                if (a == _avatar || a.Team.Value == _avatar.Team.Value || a.IsDead.Value)
                    continue;
                Vector2 p = a.transform.position;
                float d = Vector2.Distance(p, aim);
                if (d <= bestD && Vector2.Distance(p, transform.position) <= Abilities.SilenceRange)
                {
                    bestD = d;
                    best = a;
                }
            }
            return best;
        }

        [ServerRpc]
        private void ServerUse(AbilityId id, Vector2 origin, Vector2 aim, PlayerAvatar target)
        {
            Match match = _avatar.Match;
            string refuse = null;
            if (match == null || match.Ended || !_avatar.MatchLive || _avatar.IsDead.Value)
                refuse = "not live";
            else if (id != Current.Value || id == AbilityId.Grenade)
                refuse = $"doesn't hold {id}";
            else if (_avatar.Stunned || _avatar.Silenced)
                refuse = "stunned/silenced";
            else if (Time.time < _readyAt - CooldownSlackSeconds)
                refuse = $"cooldown ({_readyAt - Time.time:0.00}s left)";
            if (refuse != null)
            {
                Debug.Log($"[Ability] {_avatar.Nickname.Value}: {id} refused, {refuse}");
                return;
            }

            // Measure from the server's view of us, like weapon fire does.
            Vector2 from = _avatar.PendingTeleport ?? (Vector2)transform.position;
            bool used = id switch
            {
                AbilityId.Heal => UseHeal(),
                AbilityId.Teleport => UseTeleport(match, from, aim),
                AbilityId.Slow => UseSlow(match, from, aim),
                AbilityId.Silence => UseSilence(from, target),
                AbilityId.Stun => UseStun(from, aim),
                _ => false,
            };
            if (!used)
                return;
            Uses++;
            _readyAt = Time.time + Abilities.Get(id).Cooldown;
        }

        private bool UseHeal()
        {
            int before = _avatar.Health.Value;
            _avatar.Health.Value = Mathf.Min(_avatar.MaxHp, before + Abilities.HealAmount);
            Debug.Log($"[Ability] {_avatar.Nickname.Value}: Heal {before} -> {_avatar.Health.Value}");
            ObserversRing(transform.position, 1.2f, new Color(0.35f, 1f, 0.45f, 0.45f));
            return true;
        }

        /// <summary>Up to TeleportRange toward the aim point, backing off until the body fits (not inside solid ground).</summary>
        private bool UseTeleport(Match match, Vector2 from, Vector2 aim)
        {
            Vector2 delta = Vector2.ClampMagnitude(aim - from, Abilities.TeleportRange);
            Rect bounds = match.Arena.Bounds;
            Vector2 size = _avatar.Motor.Size;
            float len = delta.magnitude;
            Vector2 dir = len > 1e-4f ? delta / len : Vector2.zero;
            for (float d = len; d >= 0f; d -= 0.25f)
            {
                Vector2 p = from + dir * d;
                p.x = Mathf.Clamp(p.x, bounds.xMin + size.x, bounds.xMax - size.x);
                p.y = Mathf.Clamp(p.y, bounds.yMin + size.y, bounds.yMax - size.y);
                if (match.Physics.OverlapBox(p, size, 0f, Layers.GroundMask) != null)
                    continue;
                Debug.Log($"[Ability] {_avatar.Nickname.Value}: Teleport {from} -> {p}");
                ObserversRing(from, 0.8f, new Color(0.6f, 0.8f, 1f, 0.5f));
                ObserversRing(p, 0.8f, new Color(0.6f, 0.8f, 1f, 0.5f));
                _avatar.ForceTeleport(p, correction: false);
                return true;
            }
            return false;
        }

        private bool UseSlow(Match match, Vector2 from, Vector2 aim)
        {
            Vector2 center = from + Vector2.ClampMagnitude(aim - from, Abilities.SlowRange);
            int team = _avatar.Team.Value;
            int hits = 0;
            foreach (PlayerAvatar a in match.Avatars)
            {
                if (a == null || a.Team.Value == team || a.IsDead.Value || !a.IsSpawned)
                    continue;
                if (DamageMath.DistanceTo(a.HitboxAt(a.transform.position), center) <= Abilities.SlowRadius)
                {
                    a.ApplyStatus(StatusEffect.Slow, Abilities.SlowSeconds);
                    hits++;
                }
            }
            foreach (Minion m in match.Minions)
            {
                if (m == null || m.Team.Value == team || m.Health.Value <= 0)
                    continue;
                if (DamageMath.DistanceTo(m.HitboxAt(m.transform.position), center) <= Abilities.SlowRadius)
                {
                    m.ApplyStatus(StatusEffect.Slow, Abilities.SlowSeconds);
                    hits++;
                }
            }
            Debug.Log($"[Ability] {_avatar.Nickname.Value}: Slow at {center}, {hits} slowed");
            ObserversRing(center, Abilities.SlowRadius, new Color(0.4f, 0.6f, 1f, 0.3f), 0.5f);
            return true;
        }

        private bool UseSilence(Vector2 from, PlayerAvatar target)
        {
            // Undodgeable: it lands on whoever the owner targeted, as long as they're a live
            // enemy in this match and (with some slack for latency) in range.
            if (target == null || target.Match != _avatar.Match || target.Team.Value == _avatar.Team.Value || target.IsDead.Value
                || Vector2.Distance(target.transform.position, from) > Abilities.SilenceRange + 3f)
            {
                Debug.Log($"[Ability] {_avatar.Nickname.Value}: Silence refused, bad target");
                return false;
            }
            target.ApplyStatus(StatusEffect.Silence, Abilities.SilenceSeconds);
            Debug.Log($"[Ability] {_avatar.Nickname.Value}: Silenced {target.Nickname.Value}");
            ObserversSilence(from, target.transform.position);
            return true;
        }

        private bool UseStun(Vector2 from, Vector2 aim)
        {
            Vector2 dir = aim - from;
            if (dir.sqrMagnitude < 1e-4f)
                return false;
            dir.Normalize();
            ushort id = _nextBoltId++;
            _serverBolts.Add(new Bolt { Id = id, Origin = from, Dir = dir });
            ObserversSpawnBolt(id, from, dir * Abilities.StunSpeed);
            return true;
        }
        #endregion

        #region Stun bolt (server simulation, client visuals)
        private void SimulateBolts()
        {
            Match match = _avatar.Match;
            if (match == null || _serverBolts.Count == 0)
                return;
            float step = Abilities.StunSpeed * (float)TimeManager.TickDelta;
            int team = _avatar.Team.Value;
            for (int i = _serverBolts.Count - 1; i >= 0; i--)
            {
                Bolt b = _serverBolts[i];
                Vector2 from = b.Origin + b.Dir * b.Travelled;
                float len = Mathf.Min(step, Abilities.StunRange - b.Travelled);
                Vector2 to = from + b.Dir * len;

                float best = float.PositiveInfinity;
                object hit = null;
                if (Grenade.CastTerrain(match.Physics, from, to, out float terrain))
                    best = terrain;
                void Test(Rect box, object who)
                {
                    box.min -= Vector2.one * Abilities.StunRadius;
                    box.max += Vector2.one * Abilities.StunRadius;
                    if (LagCompensation.RayIntersectsRect(from, b.Dir, box, out float d) && d <= len && d < best)
                    {
                        best = d;
                        hit = who;
                    }
                }
                foreach (PlayerAvatar a in match.Avatars)
                    if (a != null && a.Team.Value != team && a.IsSpawned && !a.IsDead.Value)
                        Test(a.HitboxAt(a.transform.position), a);
                foreach (Minion m in match.Minions)
                    if (m != null && m.Team.Value != team && m.Health.Value > 0)
                        Test(m.HitboxAt(m.transform.position), m);

                b.Travelled += len;
                if (!float.IsPositiveInfinity(best) || b.Travelled >= Abilities.StunRange - 1e-3f)
                {
                    Vector2 end = float.IsPositiveInfinity(best) ? to : from + b.Dir * best;
                    if (hit is PlayerAvatar pa)
                    {
                        pa.ApplyStatus(StatusEffect.Stun, Abilities.StunSeconds);
                        Debug.Log($"[Ability] {_avatar.Nickname.Value}: Stun bolt hit {pa.Nickname.Value}");
                    }
                    else if (hit is Minion mi)
                    {
                        mi.ApplyStatus(StatusEffect.Stun, Abilities.StunSeconds);
                        Debug.Log($"[Ability] {_avatar.Nickname.Value}: Stun bolt hit a minion");
                    }
                    else
                        Debug.Log($"[Ability] {_avatar.Nickname.Value}: Stun bolt ended at {end} without a hit");
                    ObserversEndBolt(b.Id, end, hit != null);
                    _serverBolts.RemoveAt(i);
                    continue;
                }
                _serverBolts[i] = b;
            }
        }

        [ObserversRpc]
        private void ObserversSpawnBolt(ushort id, Vector2 origin, Vector2 velocity)
        {
            if (Application.isBatchMode)
                return;
            GameObject go = new("Stun Bolt");
            go.transform.position = origin;
            go.transform.localScale = new Vector3(0.6f, 0.25f, 1f);
            go.transform.rotation = Quaternion.Euler(0f, 0f, Mathf.Atan2(velocity.y, velocity.x) * Mathf.Rad2Deg);
            SpriteRenderer sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = Fx.WhiteSprite;
            sr.color = new Color(1f, 0.95f, 0.3f);
            sr.sortingOrder = 18;
            _boltVisuals[id] = (go, origin, velocity, Time.time);
        }

        [ObserversRpc]
        private void ObserversEndBolt(ushort id, Vector2 at, bool hit)
        {
            if (_boltVisuals.TryGetValue(id, out var v))
            {
                if (v.go != null)
                    Destroy(v.go);
                _boltVisuals.Remove(id);
            }
            Fx.Ring(at, hit ? 0.9f : 0.3f, new Color(1f, 0.95f, 0.3f, 0.5f));
        }

        private void UpdateBoltVisuals()
        {
            foreach (var v in _boltVisuals.Values)
            {
                if (v.go == null)
                    continue;
                float t = Mathf.Min(Time.time - v.startTime, Abilities.StunRange / Abilities.StunSpeed);
                v.go.transform.position = v.origin + v.velocity * t;
            }
        }
        #endregion

        [ObserversRpc]
        private void ObserversRing(Vector2 at, float radius, Color color, float duration = 0.25f) => Fx.Ring(at, radius, color, duration);

        [ObserversRpc]
        private void ObserversSilence(Vector2 from, Vector2 to) => Fx.Tracer(from, to, new Color(0.75f, 0.4f, 1f), 0.25f, 0.12f);

        #region Shop (server)
        /// <summary>Buy shop offer 0 or 1: it becomes our Q ability (ready at once) and that offer is replaced.</summary>
        [ServerRpc]
        public void ServerBuy(byte slot)
        {
            if (slot > 1 || !CanShop())
                return;
            AbilityId id = Offer(slot);
            AbilityDef def = Abilities.Get(id);
            if (_avatar.Money.Value < def.Cost)
                return;
            _avatar.Money.Value -= def.Cost;
            Current.Value = id;
            _readyAt = 0f;
            RollOffers(rollA: slot == 0, rollB: slot == 1);
            Debug.Log($"[Shop] {_avatar.Nickname.Value} bought {def.Name}; offers now {OfferA.Value}/{OfferB.Value}");
        }

        [ServerRpc]
        public void ServerReroll()
        {
            if (!CanShop() || _avatar.Money.Value < Abilities.RerollCost)
                return;
            _avatar.Money.Value -= Abilities.RerollCost;
            RollOffers(rollA: true, rollB: true);
            Debug.Log($"[Shop] {_avatar.Nickname.Value} rerolled; offers now {OfferA.Value}/{OfferB.Value}");
        }

        private bool CanShop() => !_avatar.IsDead.Value && _avatar.MatchLive && _avatar.InSpawnZone;

        /// <summary>New random offers for the chosen slots: never the ability we hold, never both the same.</summary>
        private void RollOffers(bool rollA, bool rollB)
        {
            if (rollA)
                OfferA.Value = RandomOffer(rollB ? (AbilityId?)null : OfferB.Value);
            if (rollB)
                OfferB.Value = RandomOffer(OfferA.Value);
        }

        private AbilityId RandomOffer(AbilityId? other)
        {
            List<AbilityId> pool = new();
            foreach (AbilityId id in Abilities.Purchasable)
                if (id != Current.Value && id != other)
                    pool.Add(id);
            return pool[Random.Range(0, pool.Count)];
        }
        #endregion
    }
}
