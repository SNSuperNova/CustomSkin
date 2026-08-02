using System;
using System.IO;

namespace customskin.Common.Networking
{
	public sealed class ChunkTransfer
	{
		public const int ChunkSize = 16 * 1024;

		private readonly byte[] payload;
		private readonly bool[] received;
		private int receivedCount;

		public int TotalLength => payload.Length;
		public int ChunkCount => received.Length;
		public bool IsComplete => receivedCount == received.Length;

		public ChunkTransfer(int totalLength, int chunkCount, int maximumLength)
		{
			if (totalLength <= 0 || totalLength > maximumLength)
				throw new InvalidDataException("Transfer length is outside the allowed range.");
			int expectedChunks = checked((totalLength + ChunkSize - 1) / ChunkSize);
			if (chunkCount != expectedChunks)
				throw new InvalidDataException("Transfer chunk count does not match its total length.");

			payload = new byte[totalLength];
			received = new bool[chunkCount];
		}

		public void AddChunk(int index, byte[] data)
		{
			if ((uint)index >= (uint)received.Length)
				throw new InvalidDataException("Transfer chunk index is outside the allowed range.");

			int offset = checked(index * ChunkSize);
			int expectedLength = Math.Min(ChunkSize, payload.Length - offset);
			if (data.Length != expectedLength)
				throw new InvalidDataException("Transfer chunk has an invalid length.");

			if (received[index])
			{
				if (!payload.AsSpan(offset, expectedLength).SequenceEqual(data))
					throw new InvalidDataException("A duplicate transfer chunk contained different data.");
				return;
			}

			data.CopyTo(payload, offset);
			received[index] = true;
			receivedCount++;
		}

		public byte[] GetPayload()
		{
			if (!IsComplete)
				throw new InvalidOperationException("Transfer is incomplete.");
			return payload;
		}
	}
}
