// SPDX-FileCopyrightText: 2025 Legiayayana
//
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers;
using System.Net;
using Serilog;
using SteamKit2;
using SteamKit2.CDN;

namespace DepotCommon.Steam;

public static class ChunkDownload {
	public static bool Validate { get; set; }
	public static bool ValidateNew { get; set; }
	public static bool OnlyValidate { get; set; }

	public static async Task<bool> FetchChunk(SteamSession client, string chunkPath, uint appId, uint depotId, byte[]? depotKey, DepotManifest.ChunkData chunk, int attemptCount, int delay) {
		var chunkId = Convert.ToHexString(chunk.ChunkID!).ToLowerInvariant();
		var buffer = ArrayPool<byte>.Shared.Rent((int) chunk.CompressedLength);

		try {
			if (File.Exists(chunkPath)) {
				var fileInfo = new FileInfo(chunkPath);
				if (!Validate) {
					if (fileInfo.Length == chunk.CompressedLength) {
						return false;
					}

					if (Console.IsErrorRedirected) {
						await Console.Error.WriteLineAsync(chunkId);
					}

					Log.Warning("Chunk {Id} is an invalid size", chunkId);
				} else {
					await using var stream = fileInfo.Open(FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
					var existing = buffer.AsSpan(0, (int) chunk.CompressedLength);
					stream.ReadExactly(existing);
					if (ValidateChunk(depotKey, chunk, existing, Validate)) {
						return false;
					}

					if (Console.IsErrorRedirected) {
						await Console.Error.WriteLineAsync(chunkId);
					}

					Log.Warning("Chunk {Id} failed validation", chunkId);
				}

				if (OnlyValidate) {
					return false;
				}
			}

			if (OnlyValidate) {
				if (Console.IsErrorRedirected) {
					await Console.Error.WriteLineAsync(chunkId);
				}

				Log.Warning("Chunk {Id} does not exist", chunkId);
				return false;
			}

			var server = client.Connections.Connection;
			SteamContent.CDNAuthToken? cdnToken = null;

			var attempts = client.Connections.Attempts * attemptCount;
			var currentAttempt = 0;
			var isRetry = false;
			while (currentAttempt++ < attempts) {
				try {
					if (cdnToken != null && cdnToken.Expiration >= DateTime.Now) {
						cdnToken = await client.RequestAuthToken(appId, depotId, server);
					}

					var n = await client.Connections.Client.DownloadDepotChunkAsync(depotId, chunk, server, buffer, null, client.Connections.ProxyServer, cdnToken?.Token);
					if (!ValidateChunk(depotKey, chunk, buffer.AsSpan(0, n), Validate || ValidateNew)) {
						if (isRetry) {
							Log.Warning("Chunk {Id} failed validation twice, re-downloading from a different cdn", chunkId);
							isRetry = false;
							goto rotate;
						}

						Log.Warning("Chunk {Id} failed validation, re-downloading", chunkId);
						goto retry;
					}

					{
						await using var stream = new FileStream(chunkPath, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite);
						await stream.WriteAsync(buffer.AsMemory(0, n));
						await stream.FlushAsync();
						return false;
					}
				} catch (SteamKitWebRequestException ex) {
					switch (ex.StatusCode) {
						case HttpStatusCode.Forbidden when cdnToken == null: {
							cdnToken = await client.RequestAuthToken(appId, depotId, server);
							goto retry;
						}
						case HttpStatusCode.NotFound when isRetry == false: {
							// this will emit when the cdn isn't warm for this file.
							Log.Error("Chunk {Id} for {DepotId} is not found, waiting for {Delay}s...", chunkId, depotId, delay);
							goto delay_retry;
						}
						case HttpStatusCode.NotFound: {
							Log.Error("Chunk {Id} for {DepotId} is not found, rotating servers", chunkId, depotId);
							goto rotate;
						}
						case HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized:
							if (Console.IsErrorRedirected) {
								await Console.Error.WriteLineAsync(chunkId);
							}

							Log.Error("Cannot download chunk {Id} for {DepotId}, got {Code}", chunkId, depotId, ex.StatusCode);
							return !Console.IsErrorRedirected;
					}

					Log.Error("Chunk {Id} for {DepotId} got {Code}, rotating servers and waiting for {Delay}s...", chunkId, depotId, ex.StatusCode, delay);
				} catch (OperationCanceledException) {
					if (!isRetry) {
						Log.Error("Chunk {Id} for {DepotId} timed out, waiting for {Delay}s...", chunkId, depotId, delay);
						goto delay_retry;
					}

					Log.Error("Chunk {Id} for {DepotId} timed out, rotating servers...", chunkId, depotId);
				} catch (IOException ex) {
					Log.Fatal(ex, "File System error while handling {Id} for {DepotId}", chunkId, depotId);
					return true;
				} catch (Exception ex) {
					Log.Error(ex, "Chunk {Id} for {DepotId} failed, rotating servers...", chunkId, depotId);
				}

			rotate:
				isRetry = false;
				cdnToken = null;
				server = client.Connections.ExchangeBrokenConnection(server);
				await Task.Delay(TimeSpan.FromSeconds(delay));
				continue;

			delay_retry:
				await Task.Delay(TimeSpan.FromSeconds(delay));
			retry:
				isRetry = true;
			}

			if (Console.IsErrorRedirected) {
				await Console.Error.WriteLineAsync(chunkId);
			}

			Log.Error("Chunk {Id} for {DepotId} failed, no valid cdns have this file", chunkId, depotId);
		} finally {
			ArrayPool<byte>.Shared.Return(buffer);
		}

		return !Console.IsErrorRedirected;
	}

	public static bool ValidateChunk(byte[]? depotKey, DepotManifest.ChunkData chunk, Span<byte> buffer, bool should) {
		if (!should || depotKey == null) {
			return true;
		}

		var targetBuffer = ArrayPool<byte>.Shared.Rent((int) chunk.UncompressedLength);
		try {
			DepotChunk.Process(chunk, buffer, targetBuffer, depotKey);
		} catch {
			return false;
		} finally {
			ArrayPool<byte>.Shared.Return(targetBuffer);
		}

		return true;
	}
}
