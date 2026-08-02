using customskin.Common.Players;
using customskin.Common.Skins;
using customskin.Config;
using Microsoft.Xna.Framework;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Terraria;
using Terraria.ID;
using Terraria.Localization;
using Terraria.ModLoader;

namespace customskin.Common.Networking
{
	public sealed class SkinNetworkSystem : ModSystem
	{
		private enum Message : byte
		{
			ClientReady,
			Announce,
			UploadRequest,
			UploadBegin,
			UploadChunk,
			UploadComplete,
			Assignment,
			ResourceRequest,
			DownloadBegin,
			DownloadChunk,
			DownloadComplete,
			ReloadRequest,
			Error,
			ReadyAck
		}

		private enum NetworkError : byte
		{
			TransferRejected,
			UploadTimeout
		}

		private sealed class PendingTransfer
		{
			public required int Token { get; init; }
			public required string Hash { get; init; }
			public required ChunkTransfer Chunks { get; init; }
			public DateTime LastActivityUtc { get; set; } = DateTime.UtcNow;
		}

		private sealed class OutgoingTransfer
		{
			public required Message BeginMessage { get; init; }
			public required Message ChunkMessage { get; init; }
			public required Message CompleteMessage { get; init; }
			public required int Token { get; init; }
			public required string Hash { get; init; }
			public required byte[] Payload { get; init; }
			public required int ToWho { get; init; }
			public bool Begun { get; set; }
			public int NextChunk { get; set; }
		}

		private const int ReadyDelayTicks = 120;
		private const int ReadyRetryTicks = 300;
		private const int AssignmentWorkIntervalTicks = 15;
		private const int UnconfirmedRetryTicks = 300;
		private const int ConfirmedRefreshTicks = 1800;
		private static readonly TimeSpan TransferTimeout = TimeSpan.FromSeconds(30);
		private static readonly TimeSpan NotificationCooldown = TimeSpan.FromSeconds(3);

		private readonly Dictionary<int, string> playerHashes = new();
		private readonly Dictionary<string, byte[]> serverResources = new(StringComparer.Ordinal);
		private readonly Dictionary<int, (int Token, string Hash, DateTime RequestedUtc)> expectedUploads = new();
		private readonly Dictionary<int, PendingTransfer> incomingUploads = new();
		private readonly Dictionary<int, PendingTransfer> incomingDownloads = new();
		private readonly Dictionary<string, DateTime> requestedDownloads = new(StringComparer.Ordinal);
		private readonly Dictionary<int, DateTime> lastUploadRequests = new();
		private readonly Dictionary<int, Queue<OutgoingTransfer>> outgoingTransfers = new();
		private readonly HashSet<int> readyClients = new();
		private readonly HashSet<int> ignoredDownloadTokens = new();

		private long serverResourceBytes;
		private int nextToken = 1;
		private int clientWorldTicks;
		private int clientAssignmentWorkTimer;
		private int ticksSinceLastAnnouncement;
		private bool clientReady;
		private bool serverReadyAcknowledged;
		private int ticksSinceReadySend;
		private bool hasAnnouncedLocalHash;
		private string? announcedLocalHash;
		private string? confirmedLocalHash;
		private DateTime lastNotificationUtc = DateTime.MinValue;
		private DateTime ignoreStaleDownloadsUntilUtc = DateTime.MinValue;

		public override void PostUpdateEverything()
		{
			if (Main.netMode == NetmodeID.MultiplayerClient)
				UpdateClient();
			else if (Main.netMode == NetmodeID.Server)
				RetryPendingUploads();

			ProcessOutgoingTransfers();
			RemoveExpiredTransfers();
		}

		public override void OnWorldUnload()
		{
			playerHashes.Clear();
			serverResources.Clear();
			expectedUploads.Clear();
			incomingUploads.Clear();
			incomingDownloads.Clear();
			requestedDownloads.Clear();
			lastUploadRequests.Clear();
			outgoingTransfers.Clear();
			readyClients.Clear();
			ignoredDownloadTokens.Clear();
			serverResourceBytes = 0;
			nextToken = 1;
			clientWorldTicks = 0;
			clientAssignmentWorkTimer = 0;
			ticksSinceLastAnnouncement = 0;
			clientReady = false;
			serverReadyAcknowledged = false;
			ticksSinceReadySend = 0;
			hasAnnouncedLocalHash = false;
			announcedLocalHash = null;
			confirmedLocalHash = null;
			lastNotificationUtc = DateTime.MinValue;
			ignoreStaleDownloadsUntilUtc = DateTime.MinValue;
			if (!Main.dedServ)
			{
				// Saving/quitting can invoke OnWorldUnload from a worker thread. Keep
				// Texture2D disposal on the main thread as required by FNA.
				Main.QueueMainThreadAction(
					ModContent.GetInstance<SkinTextureSystem>().ClearRemotePlayers);
			}
		}

		public void HandlePacket(BinaryReader reader, int whoAmI)
		{
			try
			{
				Message message = (Message)reader.ReadByte();
				switch (message)
				{
					case Message.ClientReady when Main.netMode == NetmodeID.Server: ReceiveClientReady(whoAmI); break;
					case Message.Announce when Main.netMode == NetmodeID.Server: ReceiveAnnouncement(reader, whoAmI); break;
					case Message.UploadRequest when Main.netMode == NetmodeID.MultiplayerClient: ReceiveUploadRequest(reader); break;
					case Message.UploadBegin when Main.netMode == NetmodeID.Server: ReceiveUploadBegin(reader, whoAmI); break;
					case Message.UploadChunk when Main.netMode == NetmodeID.Server: ReceiveUploadChunk(reader, whoAmI); break;
					case Message.UploadComplete when Main.netMode == NetmodeID.Server: ReceiveUploadComplete(reader, whoAmI); break;
					case Message.Assignment when Main.netMode == NetmodeID.MultiplayerClient: ReceiveAssignment(reader); break;
					case Message.ResourceRequest when Main.netMode == NetmodeID.Server: ReceiveResourceRequest(reader, whoAmI); break;
					case Message.DownloadBegin when Main.netMode == NetmodeID.MultiplayerClient: ReceiveDownloadBegin(reader); break;
					case Message.DownloadChunk when Main.netMode == NetmodeID.MultiplayerClient: ReceiveDownloadChunk(reader); break;
					case Message.DownloadComplete when Main.netMode == NetmodeID.MultiplayerClient: ReceiveDownloadComplete(reader); break;
					case Message.ReloadRequest when Main.netMode == NetmodeID.Server: ReceiveReloadRequest(whoAmI); break;
					case Message.Error when Main.netMode == NetmodeID.MultiplayerClient: ReceiveError(reader); break;
					case Message.ReadyAck when Main.netMode == NetmodeID.MultiplayerClient: ReceiveReadyAck(); break;
					default: throw new InvalidDataException("Unexpected CustomSkin network message.");
				}
			}
			catch (Exception exception) when (exception is not OutOfMemoryException)
			{
				Mod.Logger.Warn($"Rejected CustomSkin network packet from {whoAmI}: {exception.Message}");
				if (Main.netMode == NetmodeID.Server && whoAmI >= 0 && whoAmI < Main.maxPlayers)
					SendError(whoAmI, NetworkError.TransferRejected);
				else if (Main.netMode == NetmodeID.MultiplayerClient)
					NotifyNetworkProblem("Mods.customskin.Network.LocalError", exception.Message);
			}
		}

		public void SendPlayerAssignment(int playerIndex, int toWho)
		{
			SkinNetworkPolicy policy = GetServerPolicy();
			if (Main.netMode != NetmodeID.Server || !playerHashes.TryGetValue(playerIndex, out string? hash) ||
				policy.IsHashBanned(hash) || !serverResources.ContainsKey(hash))
				return;
			SendAssignment(playerIndex, hash, toWho);
		}

		public void HandlePlayerDisconnect(int playerIndex)
		{
			if (Main.netMode == NetmodeID.Server)
			{
				playerHashes.Remove(playerIndex);
				expectedUploads.Remove(playerIndex);
				incomingUploads.Remove(playerIndex);
				lastUploadRequests.Remove(playerIndex);
				readyClients.Remove(playerIndex);
				outgoingTransfers.Remove(playerIndex);
				SendAssignment(playerIndex, null);
			}
			else if (Main.netMode == NetmodeID.MultiplayerClient)
			{
				playerHashes.Remove(playerIndex);
				ModContent.GetInstance<SkinTextureSystem>().ClearRemotePlayer(playerIndex);
			}
		}

		public bool RequestManualReload()
		{
			if (Main.netMode != NetmodeID.MultiplayerClient || !clientReady || Main.gameMenu)
			{
				Main.NewText(Language.GetTextValue("Mods.customskin.Network.ReloadUnavailable"), Color.Orange);
				return false;
			}

			playerHashes.Clear();
			ignoredDownloadTokens.Clear();
			ignoredDownloadTokens.UnionWith(incomingDownloads.Keys);
			incomingDownloads.Clear();
			requestedDownloads.Clear();
			ignoreStaleDownloadsUntilUtc = DateTime.UtcNow.AddSeconds(5);
			outgoingTransfers.Remove(-1);
			ModContent.GetInstance<SkinTextureSystem>().ClearRemotePlayers();
			hasAnnouncedLocalHash = false;
			announcedLocalHash = null;
			confirmedLocalHash = null;
			ticksSinceLastAnnouncement = 0;

			Begin(Message.ReloadRequest).Send();
			UpdateLocalAnnouncement(force: true);
			Main.NewText(Language.GetTextValue("Mods.customskin.Network.ReloadStarted"), Color.LightGreen);
			return true;
		}

		public void ApplyClientPrivacySettings()
		{
			if (Main.netMode != NetmodeID.MultiplayerClient)
				return;

			// Cancel downloads whose request was made under the previous privacy
			// settings. Begin/chunk/complete packets already in flight are ignored for
			// a short bounded window, while local skin announcements remain untouched.
			ignoredDownloadTokens.UnionWith(incomingDownloads.Keys);
			incomingDownloads.Clear();
			requestedDownloads.Clear();
			ignoreStaleDownloadsUntilUtc = DateTime.UtcNow.AddSeconds(5);
			clientAssignmentWorkTimer = AssignmentWorkIntervalTicks;
			ModContent.GetInstance<SkinTextureSystem>().ClearRemotePlayers();
		}

		private void UpdateClient()
		{
			if (Main.gameMenu || Main.myPlayer < 0 || Main.myPlayer >= Main.maxPlayers || !Main.player[Main.myPlayer].active)
				return;

			if (!clientReady)
			{
				clientWorldTicks++;
				if (clientWorldTicks < ReadyDelayTicks)
					return;
				clientReady = true;
				SendClientReady();
				UpdateLocalAnnouncement(force: true);
			}
			else
			{
				if (!serverReadyAcknowledged && ++ticksSinceReadySend >= ReadyRetryTicks)
					SendClientReady();
				UpdateLocalAnnouncement();
			}

			if (++clientAssignmentWorkTimer >= AssignmentWorkIntervalTicks)
			{
				clientAssignmentWorkTimer = 0;
				try
				{
					EnsureOneRemoteAssignment();
				}
				catch (Exception exception) when (exception is not OutOfMemoryException)
				{
					Mod.Logger.Warn($"Could not prepare a remote skin: {exception.Message}");
					NotifyNetworkProblem("Mods.customskin.Network.LocalError", exception.Message);
				}
			}
		}

		private void UpdateLocalAnnouncement(bool force = false)
		{
			if (!clientReady)
				return;

			string? desired = GetEffectiveLocalHash();
			ticksSinceLastAnnouncement++;
			bool changed = !hasAnnouncedLocalHash || !string.Equals(desired, announcedLocalHash, StringComparison.Ordinal);
			bool unconfirmed = !string.Equals(desired, confirmedLocalHash, StringComparison.Ordinal);
			bool retryDue = unconfirmed && ticksSinceLastAnnouncement >= UnconfirmedRetryTicks;
			bool refreshDue = !unconfirmed && ticksSinceLastAnnouncement >= ConfirmedRefreshTicks;
			if (!force && !changed && !retryDue && !refreshDue)
				return;
			if (changed)
				outgoingTransfers.Remove(-1);

			ModPacket packet = Begin(Message.Announce);
			WriteOptionalHash(packet, desired);
			packet.Send();
			hasAnnouncedLocalHash = true;
			announcedLocalHash = desired;
			ticksSinceLastAnnouncement = 0;
		}

		private static string? GetEffectiveLocalHash()
		{
			SkinRecord? selected = ModContent.GetInstance<SkinRepositorySystem>().SelectedSkin;
			if (selected == null)
				return null;
			CustomSkinClientConfig config = ModContent.GetInstance<CustomSkinClientConfig>();
			return config.EnableMode switch
			{
				SkinEnableMode.Always => selected.Hash,
				SkinEnableMode.AccessoryOnly when Main.LocalPlayer.GetModPlayer<SkinPlayer>().CoreAccessoryVisible => selected.Hash,
				_ => null
			};
		}

		private void ReceiveClientReady(int sender)
		{
			readyClients.Add(sender);
			Begin(Message.ReadyAck).Send(sender);
			SendAllAssignments(sender);
			if (playerHashes.TryGetValue(sender, out string? hash) && !serverResources.ContainsKey(hash))
				TryRequestUpload(sender, hash);
		}

		private void SendClientReady()
		{
			Begin(Message.ClientReady).Send();
			ticksSinceReadySend = 0;
		}

		private void ReceiveReadyAck()
		{
			serverReadyAcknowledged = true;
		}

		private void ReceiveAnnouncement(BinaryReader reader, int sender)
		{
			string? hash = ReadOptionalHash(reader);
			if (hash == null)
			{
				playerHashes.Remove(sender);
				expectedUploads.Remove(sender);
				incomingUploads.Remove(sender);
				SendAssignment(sender, null);
				return;
			}
			if (GetServerPolicy().IsHashBanned(hash))
			{
				playerHashes.Remove(sender);
				expectedUploads.Remove(sender);
				incomingUploads.Remove(sender);
				SendAssignment(sender, null);
				return;
			}

			bool changed = !playerHashes.TryGetValue(sender, out string? previousHash) || previousHash != hash;
			if (changed)
			{
				expectedUploads.Remove(sender);
				incomingUploads.Remove(sender);
			}
			playerHashes[sender] = hash;
			if (serverResources.ContainsKey(hash))
				SendAssignment(sender, hash);
			else if (readyClients.Contains(sender))
			{
				if (changed)
					SendAssignment(sender, null);
				TryRequestUpload(sender, hash);
			}
		}

		private void TryRequestUpload(int playerIndex, string hash)
		{
			if (!readyClients.Contains(playerIndex) || expectedUploads.ContainsKey(playerIndex))
				return;
			SkinNetworkPolicy policy = GetServerPolicy();
			if (!policy.AllowUploads || policy.IsHashBanned(hash))
				return;
			DateTime now = DateTime.UtcNow;
			DateTime? last = lastUploadRequests.TryGetValue(playerIndex, out DateTime value) ? value : null;
			if (!policy.IsUploadCooldownElapsed(now, last))
				return;

			int token = NextToken();
			expectedUploads[playerIndex] = (token, hash, now);
			lastUploadRequests[playerIndex] = now;
			ModPacket packet = Begin(Message.UploadRequest);
			packet.Write(token);
			WriteHash(packet, hash);
			packet.Send(playerIndex);
		}

		private void ReceiveUploadRequest(BinaryReader reader)
		{
			int token = reader.ReadInt32();
			string hash = ReadHash(reader);
			if (!clientReady || !string.Equals(hash, GetEffectiveLocalHash(), StringComparison.Ordinal))
				return;
			SkinRecord? skin = ModContent.GetInstance<SkinRepositorySystem>().SelectedSkin;
			if (skin?.Hash != hash)
				return;

			SkinNetworkResource resource = SkinNetworkResource.FromStoredDirectory(skin.DirectoryPath, hash);
			QueueOutgoing(new OutgoingTransfer
			{
				BeginMessage = Message.UploadBegin,
				ChunkMessage = Message.UploadChunk,
				CompleteMessage = Message.UploadComplete,
				Token = token,
				Hash = hash,
				Payload = resource.Serialize(),
				ToWho = -1
			});
		}

		private void ReceiveUploadBegin(BinaryReader reader, int sender)
		{
			int token = reader.ReadInt32();
			string hash = ReadHash(reader);
			int total = reader.ReadInt32();
			int chunks = reader.ReadInt32();
			if (!readyClients.Contains(sender) || !expectedUploads.TryGetValue(sender, out var expected) || expected.Token != token || expected.Hash != hash)
				throw new InvalidDataException("Upload was not requested by the server.");
			SkinNetworkPolicy policy = GetServerPolicy();
			if (!policy.CanBeginUpload(hash, total))
				throw new InvalidDataException("Upload is disabled, banned, or exceeds the server limit.");
			incomingUploads[sender] = new PendingTransfer { Token = token, Hash = hash, Chunks = new ChunkTransfer(total, chunks, policy.MaxUploadBytes) };
		}

		private void ReceiveUploadChunk(BinaryReader reader, int sender)
		{
			int token = reader.ReadInt32();
			int index = reader.ReadInt32();
			byte[] data = ReadChunk(reader);
			if (!incomingUploads.TryGetValue(sender, out PendingTransfer? transfer) || transfer.Token != token)
				throw new InvalidDataException("Upload chunk has no matching transfer.");
			transfer.Chunks.AddChunk(index, data);
			transfer.LastActivityUtc = DateTime.UtcNow;
		}

		private void ReceiveUploadComplete(BinaryReader reader, int sender)
		{
			int token = reader.ReadInt32();
			if (!incomingUploads.Remove(sender, out PendingTransfer? transfer) || transfer.Token != token)
				throw new InvalidDataException("Upload completion has no matching transfer.");
			expectedUploads.Remove(sender);
			byte[] payload = transfer.Chunks.GetPayload();
			SkinNetworkPolicy policy = GetServerPolicy();
			bool alreadyCached = serverResources.ContainsKey(transfer.Hash);
			if (!policy.CanCache(transfer.Hash, payload.Length, serverResources.Count, serverResourceBytes, alreadyCached))
				throw new InvalidDataException("The upload is disallowed or the server skin cache is full.");
			SkinNetworkResource.DeserializeAndValidate(payload, transfer.Hash);
			if (!alreadyCached)
			{
				serverResources.Add(transfer.Hash, payload);
				serverResourceBytes += payload.Length;
			}
			if (playerHashes.TryGetValue(sender, out string? current) && current == transfer.Hash)
				SendAssignment(sender, transfer.Hash);
		}

		private void ReceiveAssignment(BinaryReader reader)
		{
			int playerIndex = reader.ReadByte();
			if (playerIndex < 0 || playerIndex >= Main.maxPlayers)
				throw new InvalidDataException("Player assignment index is invalid.");
			string? hash = ReadOptionalHash(reader);

			if (playerIndex == Main.myPlayer)
			{
				confirmedLocalHash = hash;
				return;
			}

			if (hash == null)
			{
				playerHashes.Remove(playerIndex);
				ModContent.GetInstance<SkinTextureSystem>().ClearRemotePlayer(playerIndex);
				return;
			}
			if (!playerHashes.TryGetValue(playerIndex, out string? previous) || previous != hash)
				ModContent.GetInstance<SkinTextureSystem>().ClearRemotePlayer(playerIndex);
			playerHashes[playerIndex] = hash;
		}

		private void EnsureOneRemoteAssignment()
		{
			CustomSkinClientConfig config = ModContent.GetInstance<CustomSkinClientConfig>();
			if (!clientReady || !config.ShowRemoteSkins || requestedDownloads.Count > 0 || incomingDownloads.Count > 0)
				return;

			SkinTextureSystem textures = ModContent.GetInstance<SkinTextureSystem>();
			foreach ((int player, string hash) in playerHashes.ToArray())
			{
				if (player == Main.myPlayer || player < 0 || player >= Main.maxPlayers || !Main.player[player].active ||
					!config.IsRemotePlayerAllowed(Main.player[player].name))
					continue;
				if (textures.GetForPlayer(player)?.Hash == hash)
					continue;
				if (ModContent.GetInstance<RemoteSkinCacheSystem>().TryLoad(hash, out SkinNetworkResource? cached))
				{
					textures.SetRemotePlayer(player, cached!);
					return;
				}

				requestedDownloads[hash] = DateTime.UtcNow;
				ModPacket request = Begin(Message.ResourceRequest);
				WriteHash(request, hash);
				request.Send();
				return;
			}
		}

		private void ReceiveResourceRequest(BinaryReader reader, int requester)
		{
			string hash = ReadHash(reader);
			if (!readyClients.Contains(requester) || GetServerPolicy().IsHashBanned(hash))
				return;
			bool assigned = playerHashes.Any(pair => pair.Value == hash && pair.Key >= 0 && pair.Key < Main.maxPlayers && Main.player[pair.Key].active);
			if (!assigned || !serverResources.TryGetValue(hash, out byte[]? payload))
				return;

			QueueOutgoing(new OutgoingTransfer
			{
				BeginMessage = Message.DownloadBegin,
				ChunkMessage = Message.DownloadChunk,
				CompleteMessage = Message.DownloadComplete,
				Token = NextToken(),
				Hash = hash,
				Payload = payload,
				ToWho = requester
			});
		}

		private void ReceiveDownloadBegin(BinaryReader reader)
		{
			int token = reader.ReadInt32();
			string hash = ReadHash(reader);
			int total = reader.ReadInt32();
			int chunks = reader.ReadInt32();
			if (!clientReady || !requestedDownloads.ContainsKey(hash))
			{
				if (DateTime.UtcNow < ignoreStaleDownloadsUntilUtc)
				{
					ignoredDownloadTokens.Add(token);
					return;
				}
				throw new InvalidDataException("Received an unrequested remote skin.");
			}
			if (incomingDownloads.Count >= 1 && !incomingDownloads.ContainsKey(token))
				throw new InvalidDataException("A remote skin download is already active.");
			incomingDownloads[token] = new PendingTransfer { Token = token, Hash = hash, Chunks = new ChunkTransfer(total, chunks, SkinNetworkResource.MaxSerializedBytes) };
		}

		private void ReceiveDownloadChunk(BinaryReader reader)
		{
			int token = reader.ReadInt32();
			int index = reader.ReadInt32();
			byte[] data = ReadChunk(reader);
			if (ignoredDownloadTokens.Contains(token))
				return;
			if (!incomingDownloads.TryGetValue(token, out PendingTransfer? transfer))
				throw new InvalidDataException("Download chunk has no matching transfer.");
			transfer.Chunks.AddChunk(index, data);
			transfer.LastActivityUtc = DateTime.UtcNow;
		}

		private void ReceiveDownloadComplete(BinaryReader reader)
		{
			int token = reader.ReadInt32();
			if (ignoredDownloadTokens.Remove(token))
				return;
			if (!incomingDownloads.Remove(token, out PendingTransfer? transfer))
				throw new InvalidDataException("Download completion has no matching transfer.");
			requestedDownloads.Remove(transfer.Hash);
			SkinNetworkResource resource = ModContent.GetInstance<RemoteSkinCacheSystem>().Store(transfer.Chunks.GetPayload(), transfer.Hash);
			CustomSkinClientConfig config = ModContent.GetInstance<CustomSkinClientConfig>();
			int player = playerHashes.Where(pair =>
				pair.Value == resource.Hash &&
				pair.Key != Main.myPlayer &&
				pair.Key >= 0 && pair.Key < Main.maxPlayers &&
				Main.player[pair.Key].active &&
				config.IsRemotePlayerAllowed(Main.player[pair.Key].name))
				.Select(pair => pair.Key)
				.DefaultIfEmpty(-1)
				.First();
			if (player >= 0 && player < Main.maxPlayers && playerHashes.TryGetValue(player, out string? hash) && hash == resource.Hash)
				ModContent.GetInstance<SkinTextureSystem>().SetRemotePlayer(player, resource);
		}

		private void ReceiveReloadRequest(int requester)
		{
			if (!readyClients.Contains(requester))
				return;
			outgoingTransfers.Remove(requester);
			expectedUploads.Remove(requester);
			incomingUploads.Remove(requester);
			lastUploadRequests.Remove(requester);
			SendAllAssignments(requester);
			if (playerHashes.TryGetValue(requester, out string? hash) && !serverResources.ContainsKey(hash))
				TryRequestUpload(requester, hash);
		}

		private void ReceiveError(BinaryReader reader)
		{
			NetworkError error = (NetworkError)reader.ReadByte();
			string key = error switch
			{
				NetworkError.UploadTimeout => "Mods.customskin.Network.UploadTimeout",
				_ => "Mods.customskin.Network.ServerRejected"
			};
			NotifyNetworkProblem(key);
		}

		private void SendAllAssignments(int toWho)
		{
			SkinNetworkPolicy policy = GetServerPolicy();
			foreach ((int player, string hash) in playerHashes.ToArray())
			{
				if (player >= 0 && player < Main.maxPlayers && Main.player[player].active &&
					!policy.IsHashBanned(hash) && serverResources.ContainsKey(hash))
					SendAssignment(player, hash, toWho);
			}
		}

		private void SendAssignment(int playerIndex, string? hash, int toWho = -1)
		{
			ModPacket packet = Begin(Message.Assignment);
			packet.Write((byte)playerIndex);
			WriteOptionalHash(packet, hash);
			packet.Send(toWho);
		}

		private void SendError(int toWho, NetworkError error)
		{
			ModPacket packet = Begin(Message.Error);
			packet.Write((byte)error);
			packet.Send(toWho);
		}

		private void RetryPendingUploads()
		{
			SkinNetworkPolicy policy = GetServerPolicy();
			ReconcileServerPolicy(policy);
			foreach ((int player, string hash) in playerHashes.ToArray())
			{
				if (readyClients.Contains(player) && !policy.IsHashBanned(hash) && !serverResources.ContainsKey(hash) &&
					player >= 0 && player < Main.maxPlayers && Main.player[player].active)
					TryRequestUpload(player, hash);
			}
		}

		private void ReconcileServerPolicy(SkinNetworkPolicy policy)
		{
			foreach ((int player, string hash) in playerHashes.ToArray())
			{
				if (!policy.IsHashBanned(hash))
					continue;
				playerHashes.Remove(player);
				expectedUploads.Remove(player);
				incomingUploads.Remove(player);
				SendAssignment(player, null);
			}

			foreach ((int player, (int Token, string Hash, DateTime RequestedUtc) expected) in expectedUploads.ToArray())
			{
				if (policy.AllowUploads && !policy.IsHashBanned(expected.Hash))
					continue;
				expectedUploads.Remove(player);
				incomingUploads.Remove(player);
			}

			foreach (int recipient in outgoingTransfers.Keys.ToArray())
			{
				Queue<OutgoingTransfer> filtered = new(outgoingTransfers[recipient].Where(transfer => !policy.IsHashBanned(transfer.Hash)));
				if (filtered.Count == 0)
					outgoingTransfers.Remove(recipient);
				else
					outgoingTransfers[recipient] = filtered;
			}
		}

		private static SkinNetworkPolicy GetServerPolicy()
			=> ModContent.GetInstance<CustomSkinServerConfig>().CreatePolicy();

		private void QueueOutgoing(OutgoingTransfer transfer)
		{
			if (!outgoingTransfers.TryGetValue(transfer.ToWho, out Queue<OutgoingTransfer>? queue))
			{
				queue = new Queue<OutgoingTransfer>();
				outgoingTransfers.Add(transfer.ToWho, queue);
			}
			if (queue.Any(existing => existing.Hash == transfer.Hash && existing.BeginMessage == transfer.BeginMessage))
				return;
			queue.Enqueue(transfer);
		}

		private void ProcessOutgoingTransfers()
		{
			foreach (int recipient in outgoingTransfers.Keys.ToArray())
			{
				try
				{
					ProcessOutgoingTransfer(recipient);
				}
				catch (Exception exception) when (exception is not OutOfMemoryException)
				{
					outgoingTransfers.Remove(recipient);
					Mod.Logger.Warn($"Could not send a paced skin transfer to {recipient}: {exception.Message}");
					if (Main.netMode == NetmodeID.Server && recipient >= 0 && recipient < Main.maxPlayers)
						SendError(recipient, NetworkError.TransferRejected);
					else if (Main.netMode == NetmodeID.MultiplayerClient)
						NotifyNetworkProblem("Mods.customskin.Network.LocalError", exception.Message);
				}
			}
		}

		private void ProcessOutgoingTransfer(int recipient)
		{
			Queue<OutgoingTransfer> queue = outgoingTransfers[recipient];
				if (queue.Count == 0)
				{
					outgoingTransfers.Remove(recipient);
					return;
				}

				OutgoingTransfer transfer = queue.Peek();
				if (!transfer.Begun)
				{
					ModPacket begin = Begin(transfer.BeginMessage);
					begin.Write(transfer.Token);
					WriteHash(begin, transfer.Hash);
					begin.Write(transfer.Payload.Length);
					begin.Write(GetChunkCount(transfer.Payload.Length));
					begin.Send(transfer.ToWho);
					transfer.Begun = true;
					return;
				}

				int chunkCount = GetChunkCount(transfer.Payload.Length);
				if (transfer.NextChunk < chunkCount)
				{
					int index = transfer.NextChunk++;
					int offset = index * ChunkTransfer.ChunkSize;
					int length = Math.Min(ChunkTransfer.ChunkSize, transfer.Payload.Length - offset);
					ModPacket chunk = Begin(transfer.ChunkMessage);
					chunk.Write(transfer.Token);
					chunk.Write(index);
					chunk.Write((ushort)length);
					chunk.Write(transfer.Payload, offset, length);
					chunk.Send(transfer.ToWho);
					return;
				}

				ModPacket complete = Begin(transfer.CompleteMessage);
				complete.Write(transfer.Token);
				complete.Send(transfer.ToWho);
				queue.Dequeue();
				if (queue.Count == 0)
					outgoingTransfers.Remove(recipient);
		}

		private void RemoveExpiredTransfers()
		{
			DateTime cutoff = DateTime.UtcNow - TransferTimeout;
			foreach (int player in incomingUploads.Where(pair => pair.Value.LastActivityUtc < cutoff).Select(pair => pair.Key).ToArray())
			{
				incomingUploads.Remove(player);
				expectedUploads.Remove(player);
				SendError(player, NetworkError.UploadTimeout);
			}
			foreach (int token in incomingDownloads.Where(pair => pair.Value.LastActivityUtc < cutoff).Select(pair => pair.Key).ToArray())
			{
				string hash = incomingDownloads[token].Hash;
				incomingDownloads.Remove(token);
				requestedDownloads.Remove(hash);
				NotifyNetworkProblem("Mods.customskin.Network.DownloadTimeout");
			}
			foreach (string hash in requestedDownloads.Where(pair => pair.Value < cutoff).Select(pair => pair.Key).ToArray())
			{
				requestedDownloads.Remove(hash);
				NotifyNetworkProblem("Mods.customskin.Network.DownloadTimeout");
			}
			foreach (int player in expectedUploads.Where(pair => pair.Value.RequestedUtc < cutoff).Select(pair => pair.Key).ToArray())
			{
				expectedUploads.Remove(player);
				SendError(player, NetworkError.UploadTimeout);
			}
		}

		private void NotifyNetworkProblem(string key, params object[] args)
		{
			if (Main.netMode != NetmodeID.MultiplayerClient || DateTime.UtcNow - lastNotificationUtc < NotificationCooldown)
				return;
			lastNotificationUtc = DateTime.UtcNow;
			string problem = Language.GetTextValue(key, args);
			string hint = Language.GetTextValue("Mods.customskin.Network.ReloadHint");
			Main.NewText($"{problem} {hint}", Color.OrangeRed);
		}

		private ModPacket Begin(Message message)
		{
			ModPacket packet = Mod.GetPacket();
			packet.Write((byte)message);
			return packet;
		}

		private int NextToken()
		{
			if (nextToken == int.MaxValue)
				nextToken = 1;
			return nextToken++;
		}

		private static int GetChunkCount(int length)
			=> checked((length + ChunkTransfer.ChunkSize - 1) / ChunkTransfer.ChunkSize);

		private static byte[] ReadChunk(BinaryReader reader)
		{
			int length = reader.ReadUInt16();
			if (length <= 0 || length > ChunkTransfer.ChunkSize)
				throw new InvalidDataException("Network chunk length is invalid.");
			byte[] data = reader.ReadBytes(length);
			if (data.Length != length)
				throw new EndOfStreamException();
			return data;
		}

		private static void WriteOptionalHash(BinaryWriter writer, string? hash)
		{
			writer.Write(hash != null);
			if (hash != null)
				WriteHash(writer, hash);
		}

		private static string? ReadOptionalHash(BinaryReader reader)
			=> reader.ReadBoolean() ? ReadHash(reader) : null;

		private static void WriteHash(BinaryWriter writer, string hash)
		{
			if (!SkinNetworkResource.IsValidHash(hash))
				throw new InvalidDataException("Skin hash is invalid.");
			writer.Write(Convert.FromHexString(hash));
		}

		private static string ReadHash(BinaryReader reader)
		{
			byte[] bytes = reader.ReadBytes(32);
			if (bytes.Length != 32)
				throw new EndOfStreamException();
			return Convert.ToHexString(bytes).ToLowerInvariant();
		}
	}
}
