using System;
using System.Buffers.Binary;
using System.Text;

namespace customskin.Common.Skins
{
	public readonly record struct PngInfo(int Width, int Height);

	public static class PngInspector
	{
		private static readonly byte[] Signature = { 137, 80, 78, 71, 13, 10, 26, 10 };

		public static PngInfo ValidateRgba(byte[] bytes, int expectedWidth, int expectedHeight, string fileName)
		{
			PngInfo info = InspectStatic(bytes, fileName, requireRgba: true);
			if (info.Width != expectedWidth || info.Height != expectedHeight)
				throw new SkinPackageException("png.dimensions", $"{fileName} must be exactly {expectedWidth}x{expectedHeight} pixels.");
			return info;
		}

		public static PngInfo ValidateStatic(byte[] bytes, int maximumWidth, int maximumHeight, int maximumPixels, string fileName)
		{
			PngInfo info = InspectStatic(bytes, fileName, requireRgba: false);
			if (info.Width <= 0 || info.Height <= 0 || info.Width > maximumWidth || info.Height > maximumHeight ||
				(long)info.Width * info.Height > maximumPixels)
			{
				throw new SkinPackageException("png.dimensions",
					$"{fileName} must be at most {maximumWidth}x{maximumHeight} pixels and {maximumPixels} total pixels.");
			}
			return info;
		}

		private static PngInfo InspectStatic(byte[] bytes, string fileName, bool requireRgba)
		{
			if (bytes.Length < 8 || !bytes.AsSpan(0, 8).SequenceEqual(Signature))
				throw new SkinPackageException("png.signature", $"{fileName} is not a PNG file.");

			int offset = 8;
			bool sawHeader = false;
			bool sawImageData = false;
			bool sawEnd = false;
			int width = 0;
			int height = 0;

			while (offset < bytes.Length)
			{
				if (bytes.Length - offset < 12)
					throw new SkinPackageException("png.chunk", $"{fileName} has a truncated PNG chunk.");

				uint unsignedLength = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset, 4));
				if (unsignedLength > int.MaxValue)
					throw new SkinPackageException("png.chunk", $"{fileName} contains an oversized PNG chunk.");

				int length = (int)unsignedLength;
				if (length > bytes.Length - offset - 12)
					throw new SkinPackageException("png.chunk", $"{fileName} has an invalid PNG chunk length.");

				ReadOnlySpan<byte> typeBytes = bytes.AsSpan(offset + 4, 4);
				string type = Encoding.ASCII.GetString(typeBytes);
				ReadOnlySpan<byte> data = bytes.AsSpan(offset + 8, length);
				uint storedCrc = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset + 8 + length, 4));
				uint actualCrc = Crc32.Compute(typeBytes, data);
				if (storedCrc != actualCrc)
					throw new SkinPackageException("png.crc", $"{fileName} has a PNG CRC error.");

				if (!sawHeader && type != "IHDR")
					throw new SkinPackageException("png.header", $"{fileName} does not start with IHDR.");

				switch (type)
				{
					case "IHDR":
						if (sawHeader || length != 13)
							throw new SkinPackageException("png.header", $"{fileName} has an invalid IHDR chunk.");
						uint unsignedWidth = BinaryPrimitives.ReadUInt32BigEndian(data[..4]);
						uint unsignedHeight = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(4, 4));
						if (unsignedWidth > int.MaxValue || unsignedHeight > int.MaxValue)
							throw new SkinPackageException("png.dimensions", $"{fileName} has invalid PNG dimensions.");
						width = (int)unsignedWidth;
						height = (int)unsignedHeight;
						if ((requireRgba && (data[8] != 8 || data[9] != 6)) || data[10] != 0 || data[11] != 0 || data[12] != 0)
							throw new SkinPackageException("png.format", $"{fileName} must be non-interlaced 8-bit RGBA PNG.");
						sawHeader = true;
						break;
					case "IDAT":
						sawImageData = true;
						break;
					case "IEND":
						if (length != 0 || sawEnd)
							throw new SkinPackageException("png.end", $"{fileName} has an invalid IEND chunk.");
						sawEnd = true;
						break;
					case "acTL":
					case "fcTL":
					case "fdAT":
						throw new SkinPackageException("png.animated", $"{fileName} must not be animated PNG.");
					default:
						if (typeBytes[0] is >= (byte)'A' and <= (byte)'Z' && type != "PLTE")
							throw new SkinPackageException("png.critical", $"{fileName} contains unsupported critical chunk {type}.");
						break;
				}

				offset += 12 + length;
				if (sawEnd)
					break;
			}

			if (!sawHeader || !sawImageData || !sawEnd || offset != bytes.Length)
				throw new SkinPackageException("png.structure", $"{fileName} has an invalid PNG structure.");
			return new PngInfo(width, height);
		}
	}

	internal static class Crc32
	{
		private static readonly uint[] Table = BuildTable();

		public static uint Compute(ReadOnlySpan<byte> first, ReadOnlySpan<byte> second)
		{
			uint crc = uint.MaxValue;
			Update(ref crc, first);
			Update(ref crc, second);
			return ~crc;
		}

		private static void Update(ref uint crc, ReadOnlySpan<byte> bytes)
		{
			foreach (byte value in bytes)
				crc = Table[(crc ^ value) & 0xff] ^ (crc >> 8);
		}

		private static uint[] BuildTable()
		{
			uint[] table = new uint[256];
			for (uint index = 0; index < table.Length; index++)
			{
				uint value = index;
				for (int bit = 0; bit < 8; bit++)
					value = (value & 1) != 0 ? 0xedb88320u ^ (value >> 1) : value >> 1;
				table[index] = value;
			}
			return table;
		}
	}
}
