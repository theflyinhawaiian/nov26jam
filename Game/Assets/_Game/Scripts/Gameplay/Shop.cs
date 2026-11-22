namespace Jam.Gameplay
{
    public enum ShopItemId : byte { Vitality = 0, Firepower = 1, Armor = 2 }

    public sealed class ShopItem
    {
        public ShopItemId Id;
        public string Name;
        public string Description;
        public int Cost;
        /// <summary>How many times one player can buy it (Armor: its max points; it refills rather than levels up).</summary>
        public int MaxLevel;
        /// <summary>Used up in play and bought again (Armor), rather than a permanent upgrade.</summary>
        public bool Consumable;
    }

    /// <summary>
    /// The economy: bounties and what the shop sells. Players earn money for minion kills and
    /// enemy towers falling, and spend it in the shop (B, inside their own spawn zone). Upgrades
    /// last the rest of the match; Armor is used up and bought again.
    /// </summary>
    public static class Shop
    {
        /// <summary>Paid to the player who lands the killing blow on an enemy minion.</summary>
        public const int MinionBounty = 5;
        /// <summary>Paid to every player on the other team when a tower falls.</summary>
        public const int TowerBounty = 100;

        /// <summary>Money each player starts a match with (the server's -startmoney overrides it).</summary>
        public const int StartingMoney = 40;

        public const int VitalityHealth = 25;
        /// <summary>Temporary health from Armor: soaks damage before health; gone when used up or on death.</summary>
        public const int ArmorPoints = 20;
        public const float FirepowerDamage = 0.15f;

        public static readonly ShopItem[] Items =
        {
            new() { Id = ShopItemId.Vitality, Name = "Vitality", Description = $"+{VitalityHealth} max health", Cost = 50, MaxLevel = 4 },
            new() { Id = ShopItemId.Firepower, Name = "Firepower", Description = $"+{FirepowerDamage * 100f:0}% weapon damage", Cost = 75, MaxLevel = 4 },
            new() { Id = ShopItemId.Armor, Name = "Armor", Description = $"{ArmorPoints} temporary health, used up before your health", Cost = 25, MaxLevel = ArmorPoints, Consumable = true },
        };

        public static ShopItem Get(ShopItemId id) => Items[(int)id];
    }
}
