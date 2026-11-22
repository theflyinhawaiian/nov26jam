namespace Jam.Gameplay
{
    public enum ShopItemId : byte { Vitality = 0, Firepower = 1 }

    public sealed class ShopItem
    {
        public ShopItemId Id;
        public string Name;
        public string Description;
        public int Cost;
        /// <summary>How many times one player can buy it.</summary>
        public int MaxLevel;
    }

    /// <summary>
    /// The economy: bounties and what the shop sells. Players earn money for minion kills and
    /// enemy towers falling, and spend it in the shop (B, inside their own spawn zone). Purchases
    /// are permanent upgrades for the rest of the match.
    /// </summary>
    public static class Shop
    {
        /// <summary>Paid to the player who lands the killing blow on an enemy minion.</summary>
        public const int MinionBounty = 5;
        /// <summary>Paid to every player on the other team when a tower falls.</summary>
        public const int TowerBounty = 100;

        public const int VitalityHealth = 25;
        public const float FirepowerDamage = 0.15f;

        public static readonly ShopItem[] Items =
        {
            new() { Id = ShopItemId.Vitality, Name = "Vitality", Description = $"+{VitalityHealth} max health", Cost = 50, MaxLevel = 4 },
            new() { Id = ShopItemId.Firepower, Name = "Firepower", Description = $"+{FirepowerDamage * 100f:0}% weapon damage", Cost = 75, MaxLevel = 4 },
        };

        public static ShopItem Get(ShopItemId id) => Items[(int)id];
    }
}
