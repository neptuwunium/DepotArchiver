// SPDX-FileCopyrightText: 2025 Legiayayana
//
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers;
using System.Globalization;
using System.Net;
using System.Runtime.InteropServices;
using DepotArchiver.Steam;
using Serilog;
using SteamKit2;
using SteamKit2.CDN;
using DepotPlan =
	System.Collections.Generic.Dictionary<
		uint,
		System.Collections.Generic.Dictionary<
			uint,
			System.Collections.Generic.Dictionary<
				ulong,
				string
			>
		>
	>;

namespace DepotArchiver;

internal static class Program {
	private static async Task Main() {
		Log.Logger = new LoggerConfiguration().MinimumLevel.Debug().WriteTo.Console().CreateLogger();

		var flags = ProgramFlags.Instance;

		if (flags is { NoAppInfo: true, NoManifests: true, NoDepotKeys: true, NoChunks: true }) {
			Log.Error("Would do nothing, exiting...");
			return;
		}

		var plan = await ParsePlan(flags);
		if (plan.Count == 0) {
			Log.Error("Empty plan.");
			return;
		}

		using var client = new SteamSession(new SteamUser.LogOnDetails {
			Username = flags.Username,
			Password = flags.Password,
			ShouldRememberPassword = flags.RememberPassword,
			AccessToken = flags.Token,
			LoginID = flags.LoginId ?? 0xDEE2DEE2, // DEERDEER
		});

		var loop = new Thread((clientObj) => {
			((SteamSession) clientObj!).TickCallbacks();
		});
		loop.Start(client);

		client.Connect();

		await client.FullyLoggedInTask;

		if (!flags.NoAppInfo) {
			await FetchAppInfo(client, plan);
		}

		if (!flags.NoDepotKeys) {
			await FetchDepotKeys(client, plan);
		}

		if (!flags.NoManifests) {
			await FetchManifests(client, plan);
		}

		if (!flags.NoChunks) {
			await FetchChunks(client, plan);
		}

		client.Disconnect();
		loop.Join();
	}

	private static async Task FetchAppInfo(SteamSession client, DepotPlan plan) {
		var pics = await client.Apps.PICSGetProductInfo(plan.Keys.Select(x => new SteamApps.PICSRequest(x, client.PackageTokens.GetValueOrDefault(x))), []);
		if (pics.Failed || pics.Results == null) {
			Log.Error("Could not get PICS data");
			return;
		}

		var output = Path.GetFullPath(ProgramFlags.Instance.TargetDirectory);
		Directory.CreateDirectory(output);

		foreach (var appInfo in pics.Results) {
			foreach (var (appId, app) in appInfo.Apps) {
				var target = Path.Combine(output, appId.ToString("D", CultureInfo.InvariantCulture) + ".vdf");
				Log.Information("Saved {Id}.vdf", appId);
				app.KeyValues.SaveToFile(target, false);
			}
		}
	}

	private static async Task FetchManifests(SteamSession client, DepotPlan plan) {
		var done = new HashSet<(uint, ulong)>();
		var output = Path.GetFullPath(ProgramFlags.Instance.TargetDirectory);
		foreach (var (appId, depot) in plan) {
			foreach (var (depotId, manifests) in depot) {
				var manifestPath = Path.Combine(output, depotId.ToString("D", CultureInfo.InvariantCulture), "manifest");
				Directory.CreateDirectory(manifestPath);

				SteamContent.CDNAuthToken? cdnToken = null;
				var server = client.Connections.GetConnection();
				foreach (var (manifestId, branch) in manifests) {
					if (!done.Add((depotId, manifestId))) {
						continue;
					}

					var token = await client.GetDepotManifestRequestCodeAsync(appId, depotId, manifestId, branch);

					while (true) {
						try {
							if (cdnToken != null && cdnToken.Expiration >= DateTime.Now) {
								cdnToken = await client.RequestAuthToken(appId, depotId, server);
							}

							// no intro wants it in a zip file with one file named "z"
							var manifest = await client.Connections.Client.DownloadManifestAsync(depotId, manifestId, token, server, null, client.Connections.ProxyServer, cdnToken?.Token);
							manifest.SaveToFile(Path.Combine(manifestPath, manifestId.ToString("D", CultureInfo.InvariantCulture)));
							Log.Information("Saved depots/{DepotId}/manifests/{ManifestId}", depotId, manifestId);
							break;
						} catch (SteamKitWebRequestException ex) {
							if (ex.StatusCode == HttpStatusCode.Forbidden && cdnToken == null) {
								cdnToken = await client.RequestAuthToken(appId, depotId, server);
								continue;
							}

							if (ex.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized or HttpStatusCode.NotFound) {
								Log.Error("Cannot download manifest {Id} for {DepotId}, got {Code}", manifestId, depotId, ex.StatusCode);
								break;
							}
						} catch (OperationCanceledException) {
							break;
						} catch (Exception ex) {
							Log.Error(ex, "Manifest download {Id} for {DepotId} failed, rotating servers...", manifestId, depotId);
						}

						cdnToken = null;
						server = client.Connections.ExchangeBrokenConnection(server);
					}
				}
			}
		}
	}

	private static async Task FetchDepotKeys(SteamSession client, DepotPlan plan) {
		var done = new HashSet<uint>();
		var output = Path.GetFullPath(ProgramFlags.Instance.TargetDirectory);
		Directory.CreateDirectory(output);

		foreach (var (appId, depot) in plan) {
			foreach (var depotId in depot.Keys.Where(depotId => done.Add(depotId))) {
				try {
					var key = await client.RequestDepotKey(depotId, appId);
					if (key == null) {
						continue;
					}

					await File.WriteAllBytesAsync(Path.Combine(output, $"{depotId.ToString("D", CultureInfo.InvariantCulture)}.depotkey"), key);
					Log.Information("Saved depots/{DepotId}.depotkey", depotId);
				} catch (Exception ex) {
					Log.Error(ex, "Could not get depot key for {Id}", depotId);
				}
			}
		}
	}

	private static async Task FetchChunks(SteamSession client, DepotPlan plan) {
		var output = Path.GetFullPath(ProgramFlags.Instance.TargetDirectory);
		Directory.CreateDirectory(output);
		var parallelOptions = new ParallelOptions {
			MaxDegreeOfParallelism = ProgramFlags.Instance.Threads,
		};

		foreach (var (appId, depot) in plan) {
			foreach (var (depotId, manifests) in depot) {
				var depotPath = Path.Combine(output, depotId.ToString("D", CultureInfo.InvariantCulture));
				var depotKeyPath = Path.Combine(output, $"{depotId.ToString("D", CultureInfo.InvariantCulture)}.depotkey");
				var depotKey = ProgramFlags.Instance.Validate && File.Exists(depotKeyPath) ? await File.ReadAllBytesAsync(depotKeyPath) : null;
				if (ProgramFlags.Instance.Validate && depotKey is not { Length: 32 }) {
					Log.Warning("Depot key for {Depot} is missing or invalid, cannot validate", depotId);
					depotKey = null;
				}

				var manifestRootPath = Path.Combine(depotPath, "manifest");
				Directory.CreateDirectory(depotPath);
				Directory.CreateDirectory(manifestRootPath);

				foreach (var manifestId in manifests.Keys) {
					var manifestPath = Path.Combine(manifestRootPath, manifestId.ToString("D", CultureInfo.InvariantCulture));
					if (!File.Exists(manifestPath)) {
						Log.Error("Manifest {ManifestId} for {Depot} was not saved!", manifestId, depotId);
						continue;
					}

					var manifest = DepotManifest.LoadFromFile(manifestPath);
					if (manifest == null) {
						Log.Error("Manifest {ManifestId} for {Depot} failed to load!", manifestId, depotId);
						continue;
					}

					if (manifest.Files == null || manifest.Files.Count == 0) {
						continue;
					}

					await client.Connections.UpdateServerList(client.CellId);

					var chunks = manifest.Files.SelectMany(x => x.Chunks).DistinctBy(x => MemoryMarshal.Read<SHA1Hash>(x.ChunkID)).ToArray();
					var done = 0;
					await Parallel.ForEachAsync(chunks, parallelOptions, async (chunk, _) => {
						await FetchChunk(client, depotPath, appId, depotId, depotKey, chunk);
						Log.Information("[{Done}/{Total}] {Current}", Interlocked.Increment(ref done), chunks.Length, Convert.ToHexStringLower(chunk.ChunkID!));
					});
				}
			}
		}
	}

	private static async Task FetchChunk(SteamSession client, string path, uint appId, uint depotId, byte[]? depotKey, DepotManifest.ChunkData chunk) {
		var chunkId = Convert.ToHexStringLower(chunk.ChunkID!);
		var chunkPath = Path.Combine(path, chunkId);
		var buffer = ArrayPool<byte>.Shared.Rent((int) chunk.CompressedLength);

		try {
			if (File.Exists(chunkPath)) {
				await using var stream = new FileStream(chunkPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
				var existing = buffer.AsSpan(0, (int) chunk.CompressedLength);
				stream.ReadExactly(existing);
				if (ValidateChunk(depotKey, chunk, existing)) {
					Log.Warning("Chunk {Id} failed validation, re-downloading", chunkId);
					return;
				}
			}

			var server = client.Connections.GetConnection();
			SteamContent.CDNAuthToken? cdnToken = null;

			while (true) {
				try {
					if (cdnToken != null && cdnToken.Expiration >= DateTime.Now) {
						cdnToken = await client.RequestAuthToken(appId, depotId, server);
					}

					var n = await client.Connections.Client.DownloadDepotChunkAsync(depotId, chunk, server, buffer, null, client.Connections.ProxyServer, cdnToken?.Token);
					if (!ValidateChunk(depotKey, chunk, buffer.AsSpan(0, n))) {
						Log.Warning("Chunk {Id} failed validation, re-downloading", chunkId);
						continue;
					}

					await using var stream = new FileStream(chunkPath, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite);
					stream.SetLength((int) chunk.CompressedLength);
					stream.Position = 0;
					stream.Write(buffer.AsSpan(0, n));
					return;
				} catch (SteamKitWebRequestException ex) {
					switch (ex.StatusCode) {
						case HttpStatusCode.Forbidden when cdnToken == null:
							cdnToken = await client.RequestAuthToken(appId, depotId, server);
							continue;
						case HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized or HttpStatusCode.NotFound:
							Log.Error("Cannot download chunk {Id} for {DepotId}, got {Code}", chunkId, depotId, ex.StatusCode);
							return;
					}
				} catch (OperationCanceledException) {
					return;
				} catch (Exception ex) {
					Log.Error(ex, "Chunk {Id} for {DepotId} failed, rotating servers...", chunkId, depotId);
				}

				cdnToken = null;
				server = client.Connections.ExchangeBrokenConnection(server);
			}
		} finally {
			ArrayPool<byte>.Shared.Return(buffer);
		}
	}

	private static bool ValidateChunk(byte[]? depotKey, DepotManifest.ChunkData chunk, Span<byte> buffer) {
		if (!ProgramFlags.Instance.Validate || depotKey == null) {
			return true;
		}

		var targetBuffer = ArrayPool<byte>.Shared.Rent((int) chunk.UncompressedLength);
		try {
			DepotChunk.Process(chunk, buffer, targetBuffer, depotKey);
		} catch {
			return true;
		} finally {
			ArrayPool<byte>.Shared.Return(targetBuffer);
		}

		return false;
	}

	private static async Task<DepotPlan> ParsePlan(ProgramFlags flags) {
		await using var stream = new FileStream(flags.ArchivePlanFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
		using var reader = new StreamReader(stream);

		var plan = new DepotPlan();
		while (await reader.ReadLineAsync() is { } line) {
			var comment = line.IndexOf('#', StringComparison.Ordinal);
			if (comment > -1) {
				line = line[..comment];
			}

			line = line.Trim();

			if (line.Length == 0) {
				continue;
			}

			var parts = line.Split(',', 4, StringSplitOptions.TrimEntries);

			var appId = uint.Parse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture);
			var depotId = uint.Parse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture);
			var manifestId = ulong.Parse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture);
			var branch = parts.ElementAtOrDefault(3) ?? "public";

			if (!plan.TryGetValue(appId, out var app)) {
				app = plan[appId] = [];
			}

			if (!app.TryGetValue(depotId, out var depot)) {
				depot = app[depotId] = [];
			}

			depot[manifestId] = branch;
		}

		return plan;
	}
}
