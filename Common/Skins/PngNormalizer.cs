using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using System;
using System.IO;
using Terraria;

namespace customskin.Common.Skins
{
	public sealed class NormalizedSkinPackage
	{
		public required string Hash { get; init; }
		public required SkinManifest Manifest { get; init; }
		public required byte[] ManifestJson { get; init; }
		public required byte[] HeadRgba { get; init; }
		public required byte[] BodyRgba { get; init; }
		public required byte[] LegsRgba { get; init; }
		public required byte[] IconRgba { get; init; }
		public byte[]? LicenseText { get; init; }
	}

	public static class SkinNormalizer
	{
		public static NormalizedSkinPackage Normalize(ValidatedSkinPackage package)
		{
			if (Main.dedServ)
				throw new InvalidOperationException("Skin image normalization is client-only.");

			byte[] manifest = SkinManifestValidator.SerializeCanonical(package.Manifest);
			byte[] icon = DecodeRgba(package.IconPng, 80, 80);
			byte[] head = DecodeRgba(package.HeadPng, 40, 1120);
			byte[] body = DecodeRgba(package.BodyPng, 360, 224);
			byte[] legs = DecodeRgba(package.LegsPng, 40, 1120);
			byte[]? license = SkinTextNormalizer.NormalizeLicense(package.LicenseText);

			return new NormalizedSkinPackage
			{
				Hash = SkinContentHasher.ComputeHash(manifest, icon, head, body, legs, license),
				Manifest = package.Manifest,
				ManifestJson = manifest,
				IconRgba = icon,
				HeadRgba = head,
				BodyRgba = body,
				LegsRgba = legs,
				LicenseText = license
			};
		}

		private static byte[] DecodeRgba(byte[] source, int width, int height)
		{
			try
			{
				using MemoryStream stream = new(source, writable: false);
				using Texture2D texture = Texture2D.FromStream(Main.instance.GraphicsDevice, stream);
				if (texture.Width != width || texture.Height != height)
					throw new SkinPackageException("png.decodedDimensions", "Decoded PNG dimensions do not match the validated header.");

				Color[] pixels = new Color[checked(width * height)];
				texture.GetData(pixels);
				byte[] rgba = new byte[checked(pixels.Length * 4)];
				int output = 0;
				foreach (Color color in pixels)
				{
					rgba[output++] = color.R;
					rgba[output++] = color.G;
					rgba[output++] = color.B;
					rgba[output++] = color.A;
				}
				return rgba;
			}
			catch (SkinPackageException)
			{
				throw;
			}
			catch (Exception exception) when (exception is not OutOfMemoryException)
			{
				throw new SkinPackageException("png.decode", "PNG pixel data could not be decoded.", exception);
			}
		}

	}
}
