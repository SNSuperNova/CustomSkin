using System;

namespace customskin.Common.Skins
{
	public sealed class SkinPackageException : Exception
	{
		public string ErrorCode { get; }

		public SkinPackageException(string errorCode, string message)
			: base(message)
		{
			ErrorCode = errorCode;
		}

		public SkinPackageException(string errorCode, string message, Exception innerException)
			: base(message, innerException)
		{
			ErrorCode = errorCode;
		}
	}
}
