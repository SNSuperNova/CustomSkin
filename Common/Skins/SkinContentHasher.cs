using System;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace customskin.Common.Skins
{
	public static class SkinContentHasher
	{
		public static string ComputeHash(byte[] manifest, byte[] icon, byte[] head, byte[] body, byte[] legs, byte[]? license)
		{
			using IncrementalHash hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
			AppendField(hasher, "manifest.json", manifest);
			AppendField(hasher, "icon.rgba", icon);
			AppendField(hasher, "head.rgba", head);
			AppendField(hasher, "body.rgba", body);
			AppendField(hasher, "legs.rgba", legs);
			if (license != null)
				AppendField(hasher, "LICENSE.txt", license);
			return Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
		}

		private static void AppendField(IncrementalHash hasher, string name, byte[] data)
		{
			byte[] nameBytes = Encoding.UTF8.GetBytes(name);
			Span<byte> length = stackalloc byte[4];
			BinaryPrimitives.WriteInt32BigEndian(length, nameBytes.Length);
			hasher.AppendData(length);
			hasher.AppendData(nameBytes);
			BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
			hasher.AppendData(length);
			hasher.AppendData(data);
		}
	}
}
