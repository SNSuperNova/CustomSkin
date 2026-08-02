using System;
using System.Text;

namespace customskin.Common.Skins
{
	public static class SkinTextNormalizer
	{
		private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

		public static byte[]? NormalizeLicense(byte[]? source)
		{
			if (source == null)
				return null;

			string text;
			try
			{
				text = StrictUtf8.GetString(source);
			}
			catch (DecoderFallbackException exception)
			{
				throw new SkinPackageException("license.utf8", "LICENSE.txt must be valid UTF-8 text.", exception);
			}

			text = text.Replace("\r\n", "\n", StringComparison.Ordinal)
				.Replace('\r', '\n')
				.Normalize(NormalizationForm.FormC);

			foreach (char character in text)
			{
				if (character == '\0' || (char.IsControl(character) && character is not '\n' and not '\t'))
					throw new SkinPackageException("license.control", "LICENSE.txt contains an unsupported control character.");
			}

			byte[] normalized = new UTF8Encoding(false).GetBytes(text);
			if (normalized.Length > SkinPackageImporter.MaxLicenseBytes)
				throw new SkinPackageException("license.size", "LICENSE.txt exceeds its normalized size limit.");
			return normalized;
		}
	}
}
