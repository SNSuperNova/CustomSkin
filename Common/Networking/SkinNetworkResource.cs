using customskin.Common.Skins;
using System;
using System.IO;
using System.Linq;
using System.Text;

namespace customskin.Common.Networking
{
	public sealed class SkinNetworkResource
	{
		private static readonly byte[] Magic = "CSN1"u8.ToArray();
		private const byte FormatVersion = 1;

		public const int IconBytes = 80 * 80 * 4;
		public const int HeadBytes = 40 * 1120 * 4;
		public const int BodyBytes = 360 * 224 * 4;
		public const int LegsBytes = 40 * 1120 * 4;
		public const int FixedPixelBytes = IconBytes + HeadBytes + BodyBytes + LegsBytes;
		public const int MaxSerializedBytes = 800 * 1024;

		public required string Hash { get; init; }
		public required SkinManifest Manifest { get; init; }
		public required byte[] ManifestJson { get; init; }
		public required byte[] IconRgba { get; init; }
		public required byte[] HeadRgba { get; init; }
		public required byte[] BodyRgba { get; init; }
		public required byte[] LegsRgba { get; init; }
		public byte[]? LicenseText { get; init; }

		public static SkinNetworkResource FromStoredDirectory(string directory, string expectedHash)
		{
			byte[] manifest = ReadBounded(Path.Combine(directory, "manifest.json"), SkinPackageImporter.MaxManifestBytes);
			byte[] icon = ReadExact(Path.Combine(directory, "pixels", "icon.rgba"), IconBytes);
			byte[] head = ReadExact(Path.Combine(directory, "pixels", "head.rgba"), HeadBytes);
			byte[] body = ReadExact(Path.Combine(directory, "pixels", "body.rgba"), BodyBytes);
			byte[] legs = ReadExact(Path.Combine(directory, "pixels", "legs.rgba"), LegsBytes);
			string licensePath = Path.Combine(directory, "LICENSE.txt");
			byte[]? license = File.Exists(licensePath) ? ReadBounded(licensePath, SkinPackageImporter.MaxLicenseBytes, allowEmpty: true) : null;
			return CreateAndValidate(manifest, icon, head, body, legs, license, expectedHash);
		}

		public byte[] Serialize()
		{
			using MemoryStream stream = new();
			using (BinaryWriter writer = new(stream, Encoding.UTF8, leaveOpen: true))
			{
				writer.Write(Magic);
				writer.Write(FormatVersion);
				writer.Write(ManifestJson.Length);
				writer.Write(LicenseText?.Length ?? -1);
				writer.Write(ManifestJson);
				writer.Write(IconRgba);
				writer.Write(HeadRgba);
				writer.Write(BodyRgba);
				writer.Write(LegsRgba);
				if (LicenseText != null)
					writer.Write(LicenseText);
			}

			if (stream.Length > MaxSerializedBytes)
				throw new SkinPackageException("network.size", "Normalized skin resource exceeds the network size limit.");
			return stream.ToArray();
		}

		public static SkinNetworkResource DeserializeAndValidate(byte[] payload, string expectedHash)
		{
			if (payload.Length <= Magic.Length + 9 + FixedPixelBytes || payload.Length > MaxSerializedBytes)
				throw new SkinPackageException("network.size", "Normalized skin resource has an invalid length.");

			try
			{
				using MemoryStream stream = new(payload, writable: false);
				using BinaryReader reader = new(stream, Encoding.UTF8, leaveOpen: false);
				if (!ReadExact(reader, Magic.Length).SequenceEqual(Magic))
					throw new SkinPackageException("network.magic", "Normalized skin resource has an invalid header.");
				if (reader.ReadByte() != FormatVersion)
					throw new SkinPackageException("network.version", "Normalized skin resource uses an unsupported version.");

				int manifestLength = reader.ReadInt32();
				int licenseLength = reader.ReadInt32();
				if (manifestLength <= 0 || manifestLength > SkinPackageImporter.MaxManifestBytes)
					throw new SkinPackageException("network.manifest", "Normalized manifest length is invalid.");
				if (licenseLength < -1 || licenseLength > SkinPackageImporter.MaxLicenseBytes)
					throw new SkinPackageException("network.license", "Normalized license length is invalid.");

				long expectedLength = Magic.Length + 1L + 4 + 4 + manifestLength + FixedPixelBytes + Math.Max(licenseLength, 0);
				if (expectedLength != payload.Length)
					throw new SkinPackageException("network.length", "Normalized skin resource fields do not match its total length.");

				byte[] manifest = ReadExact(reader, manifestLength);
				byte[] icon = ReadExact(reader, IconBytes);
				byte[] head = ReadExact(reader, HeadBytes);
				byte[] body = ReadExact(reader, BodyBytes);
				byte[] legs = ReadExact(reader, LegsBytes);
				byte[]? license = licenseLength >= 0 ? ReadExact(reader, licenseLength) : null;
				return CreateAndValidate(manifest, icon, head, body, legs, license, expectedHash);
			}
			catch (SkinPackageException)
			{
				throw;
			}
			catch (Exception exception) when (exception is EndOfStreamException or IOException or OverflowException)
			{
				throw new SkinPackageException("network.resource", "Normalized skin resource is malformed.", exception);
			}
		}

		public static bool IsValidHash(string hash)
			=> hash.Length == 64 && hash.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

		private static SkinNetworkResource CreateAndValidate(byte[] manifestJson, byte[] icon, byte[] head, byte[] body, byte[] legs, byte[]? license, string expectedHash)
		{
			if (!IsValidHash(expectedHash))
				throw new SkinPackageException("network.hash", "Skin hash is invalid.");
			if (icon.Length != IconBytes || head.Length != HeadBytes || body.Length != BodyBytes || legs.Length != LegsBytes)
				throw new SkinPackageException("network.pixels", "Normalized skin resource has an invalid pixel length.");

			SkinManifest manifest = SkinManifestValidator.ParseAndNormalize(manifestJson);
			byte[] canonicalManifest = SkinManifestValidator.SerializeCanonical(manifest);
			if (!canonicalManifest.SequenceEqual(manifestJson))
				throw new SkinPackageException("network.canonicalManifest", "Network manifest is not in canonical form.");

			byte[]? normalizedLicense = SkinTextNormalizer.NormalizeLicense(license);
			if (license != null && !license.SequenceEqual(normalizedLicense!))
				throw new SkinPackageException("network.canonicalLicense", "Network license is not in canonical form.");

			string actualHash = SkinContentHasher.ComputeHash(canonicalManifest, icon, head, body, legs, normalizedLicense);
			if (!string.Equals(actualHash, expectedHash, StringComparison.Ordinal))
				throw new SkinPackageException("network.hash", "Normalized skin resource does not match its announced hash.");

			return new SkinNetworkResource
			{
				Hash = actualHash,
				Manifest = manifest,
				ManifestJson = canonicalManifest,
				IconRgba = icon,
				HeadRgba = head,
				BodyRgba = body,
				LegsRgba = legs,
				LicenseText = normalizedLicense
			};
		}

		private static byte[] ReadExact(string path, int expectedLength)
		{
			FileInfo file = new(path);
			if (!file.Exists || file.Length != expectedLength)
				throw new SkinPackageException("network.file", $"Stored normalized file has an invalid length: {path}.");
			return File.ReadAllBytes(path);
		}

		private static byte[] ReadBounded(string path, int maximumLength, bool allowEmpty = false)
		{
			FileInfo file = new(path);
			if (!file.Exists || (!allowEmpty && file.Length == 0) || file.Length > maximumLength)
				throw new SkinPackageException("network.file", $"Stored normalized file has an invalid length: {path}.");
			return File.ReadAllBytes(path);
		}

		private static byte[] ReadExact(BinaryReader reader, int length)
		{
			byte[] bytes = reader.ReadBytes(length);
			if (bytes.Length != length)
				throw new EndOfStreamException();
			return bytes;
		}
	}
}
