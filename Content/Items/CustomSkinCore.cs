using customskin.Common.Players;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;

namespace customskin.Content.Items
{
	public sealed class CustomSkinCore : ModItem
	{
		public override string Texture => $"Terraria/Images/Item_{ItemID.FamiliarWig}";

		public override void SetStaticDefaults()
		{
			Item.ResearchUnlockCount = 1;
		}

		public override void SetDefaults()
		{
			Item.width = 20;
			Item.height = 20;
			Item.accessory = true;
			Item.value = Item.buyPrice(silver: 50);
			Item.rare = ItemRarityID.Blue;
		}

		public override void UpdateAccessory(Player player, bool hideVisual)
		{
			if (!hideVisual)
				player.GetModPlayer<SkinPlayer>().CoreAccessoryVisible = true;
		}

		public override void UpdateVanity(Player player)
		{
			player.GetModPlayer<SkinPlayer>().CoreAccessoryVisible = true;
		}

		public override void AddRecipes()
		{
			CreateRecipe()
				.AddIngredient(ItemID.Silk, 5)
				.AddTile(TileID.Loom)
				.Register();
		}
	}
}
