using System;
using System.Collections.Generic;

namespace customskin.Common.Networking
{
	public static class RemoteSkinPrivacyPolicy
	{
		public static bool IsPlayerAllowed(bool showRemoteSkins, string? playerName, IEnumerable<string>? blockedPlayerNames)
		{
			if (!showRemoteSkins || string.IsNullOrWhiteSpace(playerName))
				return false;

			string normalized = playerName.Trim();
			if (blockedPlayerNames == null)
				return true;

			foreach (string? blocked in blockedPlayerNames)
			{
				if (!string.IsNullOrWhiteSpace(blocked) &&
					string.Equals(blocked.Trim(), normalized, StringComparison.OrdinalIgnoreCase))
				{
					return false;
				}
			}

			return true;
		}
	}
}
