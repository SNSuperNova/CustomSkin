using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using System;
using System.Collections.Generic;
using System.IO;
using customskin.Common.Networking;
using Terraria;
using Terraria.ModLoader;

namespace customskin.Common.Skins
{
	[Autoload(Side = ModSide.Client)]
	public sealed class SkinTextureSystem : ModSystem
	{
		private readonly Dictionary<int, TextureSet> remotePlayers = new();
		private readonly Dictionary<string, TextureSet> characterPreviews = new(StringComparer.Ordinal);
		private readonly HashSet<string> failedCharacterPreviews = new(StringComparer.Ordinal);
		private TextureSet? creatorPreview;
		private ulong lastCharacterPreviewLoadTick = ulong.MaxValue;

		public sealed class TextureSet : IDisposable
		{
			private bool disposed;

			public required string Hash { get; init; }
			public required Texture2D Head { get; init; }
			public required Texture2D Body { get; init; }
			public required Texture2D Legs { get; init; }
			public required Action<Texture2D> TextureDisposer { get; init; }

			public void Dispose()
			{
				if (disposed)
					return;
				disposed = true;
				TextureDisposer(Head);
				TextureDisposer(Body);
				TextureDisposer(Legs);
			}
		}

		public TextureSet? Active { get; private set; }
		public int CreatedTextureCount { get; private set; }
		public int DisposedTextureCount { get; private set; }
		public int LiveTextureCount => CreatedTextureCount - DisposedTextureCount;

		public TextureSet? GetForPlayer(int playerIndex)
		{
			if (playerIndex == Main.myPlayer)
				return Active;
			return remotePlayers.TryGetValue(playerIndex, out TextureSet? textures) ? textures : null;
		}

		public TextureSet? RequestCharacterPreview(string hash)
		{
			if (Active?.Hash == hash)
				return Active;
			if (characterPreviews.TryGetValue(hash, out TextureSet? preview))
				return preview;
			if (failedCharacterPreviews.Contains(hash) || !ModContent.GetInstance<SkinRepositorySystem>().TryGetSkin(hash, out SkinRecord? skin))
				return null;

			// Main-menu character selection does not reliably run world update hooks.
			// Load from the UICharacter request itself, but permit at most one new
			// texture set per game update so a long character list cannot burst-load.
			ulong updateTick = Main.GameUpdateCount;
			if (lastCharacterPreviewLoadTick == updateTick)
				return null;
			lastCharacterPreviewLoadTick = updateTick;
			try
			{
				preview = CreateTextureSet(skin!);
				characterPreviews.Add(hash, preview);
				return preview;
			}
			catch (Exception exception) when (exception is not OutOfMemoryException)
			{
				failedCharacterPreviews.Add(hash);
				Mod.Logger.Warn($"Could not create character-select preview for skin {hash}: {exception.Message}");
				return null;
			}
		}

		public TextureSet? GetCreatorPreview(string hash)
		{
			if (Active?.Hash == hash)
				return Active;
			return creatorPreview?.Hash == hash ? creatorPreview : null;
		}

		public void SetCreatorPreview(NormalizedSkinPackage skin)
		{
			if (Active?.Hash == skin.Hash)
			{
				ClearCreatorPreview();
				return;
			}
			if (creatorPreview?.Hash == skin.Hash)
				return;

			TextureSet next = CreateTextureSet(skin.Hash, skin.HeadRgba, skin.BodyRgba, skin.LegsRgba);
			TextureSet? previous = creatorPreview;
			creatorPreview = next;
			previous?.Dispose();
		}

		public void ClearCreatorPreview()
		{
			TextureSet? previous = creatorPreview;
			creatorPreview = null;
			previous?.Dispose();
		}

		public void SetActive(SkinRecord skin)
		{
			if (Active?.Hash == skin.Hash)
				return;

			Texture2D? head = null;
			Texture2D? body = null;
			Texture2D? legs = null;
			try
			{
				head = LoadTexture(Path.Combine(skin.DirectoryPath, "pixels", "head.rgba"), 40, 1120);
				body = LoadTexture(Path.Combine(skin.DirectoryPath, "pixels", "body.rgba"), 360, 224);
				legs = LoadTexture(Path.Combine(skin.DirectoryPath, "pixels", "legs.rgba"), 40, 1120);
				TextureSet next = new()
				{
					Hash = skin.Hash,
					Head = head,
					Body = body,
					Legs = legs,
					TextureDisposer = DisposeTexture
				};

				head = body = legs = null;
				TextureSet? previous = Active;
				Active = next;
				previous?.Dispose();
				if (creatorPreview?.Hash == next.Hash)
					ClearCreatorPreview();
			}
			finally
			{
				if (head != null)
					DisposeTexture(head);
				if (body != null)
					DisposeTexture(body);
				if (legs != null)
					DisposeTexture(legs);
			}
		}

		public void ClearActive()
		{
			TextureSet? previous = Active;
			Active = null;
			previous?.Dispose();
		}

		public void SetRemotePlayer(int playerIndex, SkinNetworkResource resource)
		{
			if (playerIndex < 0 || playerIndex >= Main.maxPlayers || playerIndex == Main.myPlayer)
				return;
			if (remotePlayers.TryGetValue(playerIndex, out TextureSet? current) && current.Hash == resource.Hash)
				return;

			TextureSet next = CreateTextureSet(resource.Hash, resource.HeadRgba, resource.BodyRgba, resource.LegsRgba);
			if (remotePlayers.Remove(playerIndex, out TextureSet? previous))
				previous.Dispose();
			remotePlayers[playerIndex] = next;
		}

		public void ClearRemotePlayer(int playerIndex)
		{
			if (remotePlayers.Remove(playerIndex, out TextureSet? previous))
				previous.Dispose();
		}

		public void ClearRemotePlayers()
		{
			foreach (TextureSet textures in remotePlayers.Values)
				textures.Dispose();
			remotePlayers.Clear();
		}

		public void RemoveCharacterPreview(string hash)
		{
			failedCharacterPreviews.Remove(hash);
			if (characterPreviews.Remove(hash, out TextureSet? preview))
				preview.Dispose();
		}

		public void ClearCharacterPreviews()
		{
			foreach (TextureSet textures in characterPreviews.Values)
				textures.Dispose();
			characterPreviews.Clear();
			failedCharacterPreviews.Clear();
			lastCharacterPreviewLoadTick = ulong.MaxValue;
		}

		public override void OnWorldLoad()
		{
			// Multiplayer invokes OnWorldLoad from the TCP client thread. FNA texture
			// disposal is main-thread-only, so defer the complete preview cleanup.
			Main.QueueMainThreadAction(() =>
			{
				ClearCharacterPreviews();
				ClearCreatorPreview();
			});
		}

		public override void Unload()
		{
			// Mod synchronization can invoke Unload from a worker thread. FNA requires
			// every Texture2D disposal to run on the graphics/main thread, and unlike
			// QueueMainThreadAction this waits until cleanup has actually completed.
			Main.RunOnMainThread(() =>
			{
				ClearActive();
				ClearRemotePlayers();
				ClearCharacterPreviews();
				ClearCreatorPreview();
			}).GetAwaiter().GetResult();
		}

		private Texture2D LoadTexture(string path, int width, int height)
		{
			int expectedBytes = checked(width * height * 4);
			FileInfo file = new(path);
			if (!file.Exists || file.Length != expectedBytes)
				throw new SkinPackageException("repository.texture", $"Stored RGBA data has invalid length: {path}.");
			return LoadTexture(File.ReadAllBytes(path), width, height);
		}

		private TextureSet CreateTextureSet(string hash, byte[] headBytes, byte[] bodyBytes, byte[] legsBytes)
		{
			Texture2D? head = null;
			Texture2D? body = null;
			Texture2D? legs = null;
			try
			{
				head = LoadTexture(headBytes, 40, 1120);
				body = LoadTexture(bodyBytes, 360, 224);
				legs = LoadTexture(legsBytes, 40, 1120);
				TextureSet result = new() { Hash = hash, Head = head, Body = body, Legs = legs, TextureDisposer = DisposeTexture };
				head = body = legs = null;
				return result;
			}
			finally
			{
				if (head != null) DisposeTexture(head);
				if (body != null) DisposeTexture(body);
				if (legs != null) DisposeTexture(legs);
			}
		}

		private TextureSet CreateTextureSet(SkinRecord skin)
		{
			byte[] head = ReadTextureBytes(Path.Combine(skin.DirectoryPath, "pixels", "head.rgba"), 40, 1120);
			byte[] body = ReadTextureBytes(Path.Combine(skin.DirectoryPath, "pixels", "body.rgba"), 360, 224);
			byte[] legs = ReadTextureBytes(Path.Combine(skin.DirectoryPath, "pixels", "legs.rgba"), 40, 1120);
			return CreateTextureSet(skin.Hash, head, body, legs);
		}

		private static byte[] ReadTextureBytes(string path, int width, int height)
		{
			int expectedBytes = checked(width * height * 4);
			FileInfo file = new(path);
			if (!file.Exists || file.Length != expectedBytes)
				throw new SkinPackageException("repository.texture", $"Stored RGBA data has invalid length: {path}.");
			return File.ReadAllBytes(path);
		}

		private Texture2D LoadTexture(byte[] bytes, int width, int height)
		{
			int expectedBytes = checked(width * height * 4);
			if (bytes.Length != expectedBytes)
				throw new SkinPackageException("repository.texture", "RGBA texture data has an invalid length.");

			Color[] pixels = new Color[checked(width * height)];
			for (int pixel = 0, input = 0; pixel < pixels.Length; pixel++, input += 4)
				pixels[pixel] = new Color(bytes[input], bytes[input + 1], bytes[input + 2], bytes[input + 3]);

			Texture2D texture = new(Main.instance.GraphicsDevice, width, height);
			CreatedTextureCount++;
			try
			{
				texture.SetData(pixels);
				return texture;
			}
			catch
			{
				DisposeTexture(texture);
				throw;
			}
		}

		private void DisposeTexture(Texture2D texture)
		{
			texture.Dispose();
			DisposedTextureCount++;
		}
	}
}
