namespace Jam.Gameplay
{
    /// <summary>None: an empty ability slot (E, until something is bought into it).</summary>
    public enum AbilityId : byte { Grenade = 0, Heal = 1, Teleport = 2, Slow = 3, Silence = 4, Stun = 5, None = 255 }

    /// <summary>Timed effects on players and minions. Slow: half speed. Silence: no abilities. Stun: no moving, shooting or abilities.</summary>
    public enum StatusEffect : byte { Slow, Silence, Stun }

    public sealed class AbilityDef
    {
        public AbilityId Id;
        public string Name;
        public string Description;
        /// <summary>Shop price; 0 for the starting Grenade (not sold).</summary>
        public int Cost;
        public float Cooldown;
        /// <summary>Shop/HUD icon: Resources/Icons/{Icon}.png.</summary>
        public string Icon;
    }

    /// <summary>
    /// Abilities, used with Q and E. Each player has two slots: everyone starts with Grenade on
    /// Q and nothing on E. Buying fills E if it's empty, otherwise the player picks which slot it
    /// replaces. The shop offers two purchasable abilities at random (never ones you hold);
    /// buying one replaces that offer, and rerolling (for money) replaces both.
    /// Grenade's cooldown and damage live on the GrenadeLauncher weapon asset.
    /// </summary>
    public static class Abilities
    {
        public const int RerollCost = 25;

        public const int HealAmount = 50;
        public const float TeleportRange = 10f;
        public const float SlowRange = 14f;
        public const float SlowRadius = 4f;
        public const float SlowFactor = 0.5f;
        public const float SlowSeconds = 2.5f;
        public const float SilenceRange = 16f;
        /// <summary>Silence picks the enemy player within this of the cursor.</summary>
        public const float SilencePickRadius = 3f;
        public const float SilenceSeconds = 2.5f;
        public const float StunSpeed = 28f;
        public const float StunRange = 20f;
        public const float StunRadius = 0.3f;
        public const float StunSeconds = 1.25f;

        public static readonly AbilityDef[] Defs =
        {
            new() { Id = AbilityId.Grenade, Name = "Grenade", Description = "Lobbed explosive", Cost = 0, Cooldown = 3f, Icon = "grenade" },
            new() { Id = AbilityId.Heal, Name = "Heal", Description = $"Restore {HealAmount} health", Cost = 100, Cooldown = 12f, Icon = "heal" },
            new() { Id = AbilityId.Teleport, Name = "Teleport", Description = $"Blink up to {TeleportRange:0} units toward the cursor", Cost = 125, Cooldown = 10f, Icon = "teleport" },
            new() { Id = AbilityId.Slow, Name = "Slow Field", Description = $"Enemies in an area move {SlowFactor * 100f:0}% slower for {SlowSeconds}s", Cost = 100, Cooldown = 10f, Icon = "slow" },
            new() { Id = AbilityId.Silence, Name = "Silence", Description = $"Target enemy can't use abilities for {SilenceSeconds}s. Can't be dodged", Cost = 125, Cooldown = 12f, Icon = "silence" },
            new() { Id = AbilityId.Stun, Name = "Stun Bolt", Description = $"Bolt that stuns the first enemy hit for {StunSeconds}s", Cost = 150, Cooldown = 10f, Icon = "stun" },
        };

        /// <summary>Every ability (the dev shop can set any of them).</summary>
        public static readonly AbilityId[] All = { AbilityId.Grenade, AbilityId.Heal, AbilityId.Teleport, AbilityId.Slow, AbilityId.Silence, AbilityId.Stun };

        /// <summary>What the shop can offer (not Grenade).</summary>
        public static readonly AbilityId[] Purchasable = { AbilityId.Heal, AbilityId.Teleport, AbilityId.Slow, AbilityId.Silence, AbilityId.Stun };

        public static AbilityDef Get(AbilityId id) => Defs[(int)id];
    }
}
