using customskin.Common.Players;
using customskin.Common.Skins;
using System;
using System.Collections.Generic;
using Terraria;
using Terraria.DataStructures;
using Terraria.ID;
using Terraria.ModLoader;

namespace customskin.Content.Rendering
{
	/// <summary>
	/// Registers one transparent-compatible native equipment template. Terraria uses
	/// these slots to generate the exact vanilla head/body/legs draw data; SkinPlayer
	/// then replaces only those template texture references with per-player RGBA textures.
	/// </summary>
	public sealed class SkinTemplateSystem : ModSystem
	{
		private const string HeadTexturePath = "customskin/Assets/TestSkin/Stage0TestSkin_Head";
		private const string BodyTexturePath = "customskin/Assets/TestSkin/Stage0TestSkin_Body";
		private const string LegsTexturePath = "customskin/Assets/TestSkin/Stage0TestSkin_Legs";

		internal static int HeadSlot { get; private set; } = -1;
		internal static int BodySlot { get; private set; } = -1;
		internal static int LegsSlot { get; private set; } = -1;

		internal static bool IsReady => HeadSlot >= 0 && BodySlot >= 0 && LegsSlot >= 0;

		public override void Load()
		{
			if (Main.netMode == NetmodeID.Server)
				return;

			HeadSlot = EquipLoader.AddEquipTexture(Mod, HeadTexturePath, EquipType.Head, name: "SkinTemplateHead");
			BodySlot = EquipLoader.AddEquipTexture(Mod, BodyTexturePath, EquipType.Body,
				name: "SkinTemplateBody", equipTexture: new SkinTemplateBodyEquipTexture());
			LegsSlot = EquipLoader.AddEquipTexture(Mod, LegsTexturePath, EquipType.Legs, name: "SkinTemplateLegs");
		}

		public override void PostSetupContent()
		{
			if (!IsReady)
				return;

			ArmorIDs.Head.Sets.DrawHead[HeadSlot] = false;
			ArmorIDs.Body.Sets.HidesTopSkin[BodySlot] = true;
			ArmorIDs.Body.Sets.HidesArms[BodySlot] = true;
			ArmorIDs.Legs.Sets.HidesBottomSkin[LegsSlot] = true;
		}

		private sealed class SkinTemplateBodyEquipTexture : EquipTexture
		{
			private readonly HashSet<string> loggedContexts = new(StringComparer.Ordinal);

			public override bool ModifyDraw(ref PlayerDrawSet drawInfo, ref DrawData drawData, string layerName)
			{
				SkinPlayer skinPlayer = drawInfo.drawPlayer.GetModPlayer<SkinPlayer>();
				SkinTextureSystem.TextureSet? runtime = skinPlayer.GetRuntimeTexturesForDraw();
				string context = $"{skinPlayer.DrawContextName}:{runtime?.Hash ?? "none"}";
				string source = drawData.sourceRect?.ToString() ?? "none";
				string diagnosticKey = $"{context}:{layerName}:{source}";
				if (loggedContexts.Add(diagnosticKey))
				{
					ModContent.GetInstance<CustomSkinMod>().Logger.Info(
						$"Composite body hook context={context}, layer={layerName}, source={source}, " +
						$"color={drawData.color}, shader={drawData.shader}, playerBody={drawInfo.drawPlayer.body}, " +
						$"template={drawData.texture.Width}x{drawData.texture.Height}, runtime=" +
						$"{(runtime == null ? "none" : $"{runtime.Body.Width}x{runtime.Body.Height}")}");
				}
				if (runtime != null)
					drawData.texture = runtime.Body;
				return true;
			}
		}

		public override void Unload()
		{
			HeadSlot = -1;
			BodySlot = -1;
			LegsSlot = -1;
		}
	}
}
