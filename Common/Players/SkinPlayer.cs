using customskin.Common.Skins;
using customskin.Common.Networking;
using customskin.Config;
using customskin.Content.Rendering;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using System;
using System.Collections.Generic;
using Terraria;
using Terraria.DataStructures;
using Terraria.GameContent;
using Terraria.ID;
using Terraria.ModLoader;
using Terraria.ModLoader.IO;

namespace customskin.Common.Players
{
	public sealed class SkinPlayer : ModPlayer
	{
		private const int SelectionDataVersion = 1;
		private readonly HashSet<string> loggedBodyDrawEntries = new(StringComparer.Ordinal);

		internal bool IsManagerPreview { get; set; }
		internal bool IsCreatorPreview { get; set; }
		internal string? ManagerPreviewSkinHash { get; set; }
		internal bool CoreAccessoryVisible { get; set; }
		internal bool HasCharacterSelectionRecord { get; private set; }
		internal string? SelectedSkinHash { get; private set; }

		public override void SaveData(TagCompound tag)
		{
			tag["selectionVersion"] = SelectionDataVersion;
			if (SelectedSkinHash != null)
				tag["selectedSkinHash"] = SelectedSkinHash;
		}

		public override void LoadData(TagCompound tag)
		{
			HasCharacterSelectionRecord = tag.GetInt("selectionVersion") >= SelectionDataVersion;
			string hash = tag.GetString("selectedSkinHash");
			SelectedSkinHash = SkinNetworkResource.IsValidHash(hash) ? hash : null;
		}

		public override void OnEnterWorld()
		{
			if (!Main.dedServ && Player.whoAmI == Main.myPlayer && ReferenceEquals(Player, Main.LocalPlayer))
				ModContent.GetInstance<SkinRepositorySystem>().ApplyCharacterSelection(this);
		}

		internal void SetSelectedSkinHash(string? hash)
		{
			if (hash != null && !SkinNetworkResource.IsValidHash(hash))
				throw new SkinPackageException("selection.hash", "Character skin selection hash is invalid.");
			SelectedSkinHash = hash;
			HasCharacterSelectionRecord = true;
		}

		public override void ResetEffects()
		{
			CoreAccessoryVisible = false;
		}

		public override void FrameEffects()
		{
			if (ShouldApplySkin())
				ApplyTemplateSlots();
		}

		public override void ModifyDrawInfo(ref PlayerDrawSet drawInfo)
		{
			if (ShouldApplySkin())
			{
				ApplyTemplateSlots();
				if (IsManagerPreview)
					ApplyPreviewAnimationFrame(ref drawInfo);
			}
		}

		private static void ApplyPreviewAnimationFrame(ref PlayerDrawSet drawInfo)
		{
			// Terraria runs at 60 updates per second. Six ticks per displayed pose
			// gives the preview the same 10 FPS cadence used by the normal player
			// animation at its representative movement speed, while still leaving
			// every atlas pose visible long enough to inspect.
			const int ticksPerPose = 6;
			IReadOnlyList<SkinEditorPose> poses = SkinEditorDocument.Poses;
			SkinEditorPose pose = poses[(int)((Main.GameUpdateCount / ticksPerPose) % (ulong)poses.Count)];
			Player previewPlayer = drawInfo.drawPlayer;

			previewPlayer.bodyFrame.Y = pose.BodyFrame * previewPlayer.bodyFrame.Height;
			previewPlayer.legFrame.Y = pose.LegFrame * previewPlayer.legFrame.Height;

			// PlayerDrawSet.CreateCompositeData runs before ModifyDrawInfo and has
			// already cached the torso, shoulders, and arm source rectangles. Updating
			// Player.bodyFrame alone therefore cannot animate composite armor in a
			// UICharacter preview. Drive those cached rectangles from the same frozen
			// authoring contract so every visible pose actually reaches the renderer.
			SkinEditorGender gender = previewPlayer.Male ? SkinEditorGender.Male : SkinEditorGender.Female;
			drawInfo.compTorsoFrame = CompositeBodyFrame(SkinEditorDocument.GetAtlasSlot(SkinEditorPart.Torso, pose, gender));
			drawInfo.compFrontShoulderFrame = CompositeBodyFrame(SkinEditorDocument.GetAtlasSlot(SkinEditorPart.FrontShoulder, pose, gender));
			drawInfo.compBackShoulderFrame = CompositeBodyFrame(SkinEditorDocument.GetAtlasSlot(SkinEditorPart.BackShoulder, pose, gender));
			drawInfo.compFrontArmFrame = CompositeBodyFrame(SkinEditorDocument.GetAtlasSlot(SkinEditorPart.FrontArm, pose, gender));
			drawInfo.compBackArmFrame = CompositeBodyFrame(SkinEditorDocument.GetAtlasSlot(SkinEditorPart.BackArm, pose, gender));
			drawInfo.compShoulderOverFrontArm = SkinEditorDocument.IsShoulderOverFrontArm(pose);

			// Clear any composite-arm state left on UICharacter's cloned player,
			// then explicitly exercise all four weapon arm atlas poses as the full
			// 29-pose authoring contract is traversed.
			previewPlayer.SetCompositeArmFront(false, Player.CompositeArmStretchAmount.None, 0f);
			previewPlayer.SetCompositeArmBack(false, Player.CompositeArmStretchAmount.None, 0f);
			if (pose.State == "weapon-arm")
			{
				Player.CompositeArmStretchAmount stretch = pose.ExplicitFrontArm switch
				{
					7 => Player.CompositeArmStretchAmount.Full,
					16 => Player.CompositeArmStretchAmount.ThreeQuarters,
					25 => Player.CompositeArmStretchAmount.Quarter,
					_ => Player.CompositeArmStretchAmount.None
				};
				previewPlayer.SetCompositeArmFront(true, stretch, 0f);
				previewPlayer.SetCompositeArmBack(true, stretch, 0f);
				drawInfo.compositeFrontArmRotation = 0f;
				drawInfo.compositeBackArmRotation = 0f;
			}
		}

		private static Rectangle CompositeBodyFrame(int slot)
			=> new((slot % 9) * SkinEditorDocument.CellWidth, (slot / 9) * SkinEditorDocument.CellHeight,
				SkinEditorDocument.CellWidth, SkinEditorDocument.CellHeight);

		public override void TransformDrawData(ref PlayerDrawSet drawInfo)
		{
			if (!ShouldApplySkin())
				return;

			SkinTextureSystem.TextureSet? runtime = GetRuntimeTextures();
			if (runtime == null)
				return;

			Texture2D templateHead = TextureAssets.ArmorHead[SkinTemplateSystem.HeadSlot].Value;
			Texture2D templateBody = TextureAssets.ArmorBodyComposite[SkinTemplateSystem.BodySlot].Value;
			Texture2D templateLegs = TextureAssets.ArmorLeg[SkinTemplateSystem.LegsSlot].Value;

			for (int index = 0; index < drawInfo.DrawDataCache.Count; index++)
			{
				DrawData data = drawInfo.DrawDataCache[index];
				string? bodyTextureOrigin = null;
				if (ReferenceEquals(data.texture, templateHead))
					data.texture = runtime.Head;
				else if (ReferenceEquals(data.texture, templateBody))
				{
					data.texture = runtime.Body;
					bodyTextureOrigin = "composite-template-replaced";
				}
				else if (ReferenceEquals(data.texture, templateLegs))
					data.texture = runtime.Legs;
				else if (ReferenceEquals(data.texture, runtime.Body))
					bodyTextureOrigin = "runtime-before-transform";
				else
					continue;

				// UICharacter clones the world player and can carry black lighting/dye
				// multipliers into its off-screen preview. The manager preview is an
				// unlit skin inspection surface, so only its replacement layers use
				// neutral color and no armor dye shader.
				if (IsManagerPreview)
				{
					data.color = Color.White;
					data.shader = 0;
				}

				drawInfo.DrawDataCache[index] = data;

				if (bodyTextureOrigin != null)
				{
					string source = data.sourceRect?.ToString() ?? "none";
					string diagnosticKey = $"{DrawContextName}:{runtime.Hash}:{bodyTextureOrigin}:{source}:{data.color}:{data.shader}";
					if (loggedBodyDrawEntries.Add(diagnosticKey))
					{
						ModContent.GetInstance<CustomSkinMod>().Logger.Info(
							$"Final body draw context={DrawContextName}:{runtime.Hash}, origin={bodyTextureOrigin}, " +
							$"source={source}, color={data.color}, shader={data.shader}, playerBody={Player.body}");
					}
				}
			}

			// UICharacter can rebuild part of its cloned player's vanilla draw cache
			// when the window loses focus. Equipment hide flags are not consistently
			// honored in that inactive preview pass, leaving skin, hair, clothing, or
			// accessories visible through transparent CustomSkin pixels. Manager and
			// creator previews are skin inspection surfaces, so retain only the three
			// runtime atlas textures after all template references have been replaced.
			if (IsManagerPreview)
			{
				for (int index = drawInfo.DrawDataCache.Count - 1; index >= 0; index--)
				{
					Texture2D texture = drawInfo.DrawDataCache[index].texture;
					if (!ReferenceEquals(texture, runtime.Head) &&
						!ReferenceEquals(texture, runtime.Body) &&
						!ReferenceEquals(texture, runtime.Legs))
					{
						drawInfo.DrawDataCache.RemoveAt(index);
					}
				}
			}
			else
			{
				// Do not hide PlayerDrawLayers.Skin as a whole: composite armor arms
				// also pass through that layer and have already been replaced with
				// runtime.Body above. Remove only draw data that still references the
				// active vanilla skin variant. This suppresses inactive-window body/leg
				// leakage without deleting CustomSkin hands, held items, or accessories.
				for (int index = drawInfo.DrawDataCache.Count - 1; index >= 0; index--)
				{
					if (IsVanillaPlayerTexture(drawInfo.DrawDataCache[index].texture, Player.skinVariant))
						drawInfo.DrawDataCache.RemoveAt(index);
				}
			}
		}

		private static bool IsVanillaPlayerTexture(Texture2D texture, int skinVariant)
		{
			if (skinVariant < 0 || skinVariant >= TextureAssets.Players.GetLength(0))
				return false;
			for (int index = 0; index < TextureAssets.Players.GetLength(1); index++)
			{
				var asset = TextureAssets.Players[skinVariant, index];
				if (asset.IsLoaded && ReferenceEquals(texture, asset.Value))
					return true;
			}
			return false;
		}

		private bool ShouldApplySkin()
		{
			if (Main.netMode == NetmodeID.Server ||
				!SkinTemplateSystem.IsReady ||
				(!Player.active && !IsCharacterSelectionPreview()))
			{
				return false;
			}

			if (IsManagerPreview)
			{
				if (ManagerPreviewSkinHash != null)
				{
					if (IsCreatorPreview)
						return ModContent.GetInstance<SkinTextureSystem>().GetCreatorPreview(ManagerPreviewSkinHash) != null;
					return ModContent.GetInstance<SkinRepositorySystem>().TryGetSkin(ManagerPreviewSkinHash, out _);
				}
				return ModContent.GetInstance<SkinTextureSystem>().Active != null &&
					ModContent.GetInstance<SkinRepositorySystem>().SelectedSkin != null;
			}
			if (IsCharacterSelectionPreview())
				return GetRuntimeTextures() != null;

			int playerIndex = Player.whoAmI;
			bool isActualPlayer = playerIndex >= 0 &&
				playerIndex < Main.maxPlayers &&
				ReferenceEquals(Player, Main.player[playerIndex]);
			if (!isActualPlayer || ModContent.GetInstance<SkinTextureSystem>().GetForPlayer(playerIndex) == null)
				return false;
			if (playerIndex != Main.myPlayer)
				return ModContent.GetInstance<CustomSkinClientConfig>().IsRemotePlayerAllowed(Player.name);

			return ModContent.GetInstance<CustomSkinClientConfig>().EnableMode switch
			{
				SkinEnableMode.Always => true,
				SkinEnableMode.AccessoryOnly => CoreAccessoryVisible,
				_ => false
			};
		}

		private SkinTextureSystem.TextureSet? GetRuntimeTextures()
		{
			SkinTextureSystem textures = ModContent.GetInstance<SkinTextureSystem>();
			if (IsManagerPreview)
			{
				if (ManagerPreviewSkinHash == null)
					return textures.Active;
				return IsCreatorPreview
					? textures.GetCreatorPreview(ManagerPreviewSkinHash)
					: textures.RequestCharacterPreview(ManagerPreviewSkinHash);
			}
			if (IsCharacterSelectionPreview())
				return textures.RequestCharacterPreview(SelectedSkinHash!);
			return textures.GetForPlayer(Player.whoAmI);
		}

		internal SkinTextureSystem.TextureSet? GetRuntimeTexturesForDraw()
			=> GetRuntimeTextures();

		internal string DrawContextName => IsCreatorPreview
			? "creator-preview"
			: IsManagerPreview
				? "manager-preview"
				: IsCharacterSelectionPreview()
					? "character-preview"
					: Player.whoAmI == Main.myPlayer ? "local-player" : $"remote-player-{Player.whoAmI}";

		private bool IsCharacterSelectionPreview()
			=> Main.gameMenu && SelectedSkinHash != null;

		public override void SyncPlayer(int toWho, int fromWho, bool newPlayer)
			=> ModContent.GetInstance<SkinNetworkSystem>().SendPlayerAssignment(Player.whoAmI, toWho);

		public override void PlayerDisconnect()
			=> ModContent.GetInstance<SkinNetworkSystem>().HandlePlayerDisconnect(Player.whoAmI);

		private void ApplyTemplateSlots()
		{
			Player.head = SkinTemplateSystem.HeadSlot;
			Player.body = SkinTemplateSystem.BodySlot;
			Player.legs = SkinTemplateSystem.LegsSlot;
		}
	}
}
