// SPDX-FileCopyrightText: 2025 Legiayayana
//
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using DepotArchiver.Steam;
using DragonLib;
using Serilog;
using Serilog.Events;
using SteamKit2;
using SteamKit2.CDN;
using DepotPlan = System.Collections.Generic.Dictionary<
	uint, System.Collections.Generic.Dictionary<
		uint, System.Collections.Generic.Dictionary<
			ulong,
			string?
		>
	>
>;
using BranchPasswords = System.Collections.Generic.Dictionary<
	uint, System.Collections.Generic.HashSet<
		string
	>
>;

namespace DepotArchiver;

internal static class Program {
	private static async Task Main() {
		Log.Logger = new LoggerConfiguration().MinimumLevel.Is(Debugger.IsAttached ? LogEventLevel.Debug : LogEventLevel.Information).WriteTo.Console().CreateLogger();

		var flags = ProgramFlags.Instance;

		if (flags is { NoAppInfo: true, NoManifests: true, NoDepotKeys: true, NoChunks: true }) {
			Log.Error("Would do nothing, exiting...");
			return;
		}

		var (plan, passwords) = await ParsePlan(flags);
		if (plan.Count == 0) {
			Log.Error("Empty plan.");
			return;
		}

		if (flags.OnlyValidate) {
			flags.Validate = true;
		}

		if (flags.NoAppInfo && plan.Any(x => x.Value.Count == 0)) {
			Log.Error("Enabling App Info retrieval since we are missing depot ids...");
			flags.NoAppInfo = false;
		}

		using var client = new SteamSession(new SteamUser.LogOnDetails {
			MachineName = $"{Environment.MachineName} (Archival)",
			Username = flags.Username,
			Password = flags.Password,
			ShouldRememberPassword = flags.RememberPassword,
			AccessToken = flags.Token,
			LoginID = flags.LoginId ?? 0xDEE2DEE2, // DEERDEER
		});

		var loop = new Thread(clientObj => {
			((SteamSession) clientObj!).TickCallbacks();
		});
		loop.Start(client);

		client.Connect();

		try {
			await client.FullyLoggedInTask;
		} catch (TaskCanceledException) {
			Log.Error("Could not fully log in, exiting");
			loop.Join();
			return;
		}

		if (!flags.NoAppInfo) {
			await FetchAppInfo(client, plan, passwords);
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

		try {
			client.Disconnect();
		} catch(Exception e) {
			Log.Warning(e, "Crashed while exiting?");
			Environment.Exit(0);
			// what
		}

		loop.Join();
	}

	private static async Task FetchAppInfo(SteamSession client, DepotPlan plan, BranchPasswords passwords) {
		var accessTokens = await client.Apps.PICSGetAccessTokens(plan.Keys, []);

		var pics = await client.Apps.PICSGetProductInfo(plan.Keys.Select(x => new SteamApps.PICSRequest(x, accessTokens.AppTokens.GetValueOrDefault(x))), []);
		if (pics.Failed || pics.Results == null) {
			Log.Error("Could not get PICS data");
			return;
		}

		var output = Path.GetFullPath(ProgramFlags.Instance.TargetDirectory);
		Directory.CreateDirectory(output);

		Log.Information("Saving app info to {Path}", output);

		foreach (var appInfo in pics.Results) {
			foreach (var (appId, app) in appInfo.Apps) {
				if (passwords.TryGetValue(appId, out var appPasswords)) {
					var depots = app.KeyValues["depots"];
					if (depots == KeyValue.Invalid) {
						depots = app.KeyValues["depots"] = new KeyValue();
					}

					var branches = depots["branches"];
					if (branches == KeyValue.Invalid) {
						branches = app.KeyValues["branches"] = new KeyValue();
					}

					foreach (var appPassword in appPasswords) {
						var appPasswordResponse = await client.Apps.CheckAppBetaPassword(appId, appPassword);
						if (appPasswordResponse.Result != EResult.OK) {
							Log.Error("Password {Password} (SHA:8) for {AppId} is invalid", Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes(appPassword)))[..8], appId);
							continue;
						}

						var done = new HashSet<string>();
						foreach (var (branchName, appKey) in appPasswordResponse.BetaPasswords) {
							if (!done.Add(branchName)) {
								continue;
							}

							// is this key immutable?
							var keyName = appId.ToString("D", CultureInfo.InvariantCulture) + $"_{branchName}.branchkey";
							var keyTarget = Path.Combine(output, keyName);

							await File.WriteAllBytesAsync(keyTarget, appKey);
							Log.Information("Saved {KeyName}.branchkey", keyName);

							var privateBeta = await client.Apps.PICSGetPrivateBeta(appId, accessTokens.AppTokens.GetValueOrDefault(appId), branchName, appKey);
							if (privateBeta.Result != EResult.OK) {
								Log.Error("Private Branch {Branch} for {AppId} is invalid", branchName, appId);
								continue;
							}

							// merge branches
							foreach (var kv in privateBeta.DepotSection["branches"].Children) {
								if (kv.Name == null) {
									continue;
								}

								if (branches[kv.Name] == KeyValue.Invalid) {
									branches[kv.Name] = kv;
								}
							}

							// merge depots
							foreach (var kv in privateBeta.DepotSection.Children) {
								if (kv.Name is null or "branches") {
									continue;
								}

								if (depots[kv.Name] == KeyValue.Invalid) {
									depots[kv.Name] = kv;
									continue;
								}

								var depot = depots[kv.Name];
								var manifests = depot["manifests"];
								if (manifests == KeyValue.Invalid) {
									depot["manifests"] = kv["manifests"];
									continue;
								}

								foreach (var manifestKv in kv["manifests"].Children) {
									if (manifestKv.Name == null || manifests[manifestKv.Name] != KeyValue.Invalid) {
										continue;
									}

									manifests[manifestKv.Name] = manifestKv;
								}
							}
						}
					}
				}

				var target = Path.Combine(output, appId.ToString("D", CultureInfo.InvariantCulture) + ".vdf");
				Log.Information("Saved {Id}.vdf", appId);
				app.KeyValues.SaveToFile(target, false);

				await ListDepots(plan, appId, app);
			}
		}
	}

	private static async Task ListDepots(DepotPlan plan, uint appId, SteamApps.PICSProductInfoCallback.PICSProductInfo app) {
		if (!plan.TryGetValue(appId, out var appPlan)) {
			appPlan = []; // realistically should never happen
		}

		var isBlank = appPlan.Count == 0;
		var wildcardDepots = new HashSet<uint>();

		Log.Information("Available depots for app {AppId}", appId);
		foreach (var depot in app.KeyValues.Children.Where(x => x.Name == "depots").FirstOrDefault(KeyValue.Invalid).Children) {
			var manifests = depot["manifests"];

			if (manifests.Children.Count == 0) {
				continue;
			}

			foreach (var branch in manifests.Children) {
				var gid = branch["gid"];

				if (string.IsNullOrEmpty(gid.Value) || string.IsNullOrEmpty(depot.Name)) {
					continue;
				}

				var depotId = uint.Parse(depot.Name);
				var manifestId = ulong.Parse(gid.Value);

				if (Console.IsErrorRedirected) {
					await Console.Error.WriteLineAsync($"{appId},{depotId},{manifestId},{branch.Name}");
				} else {
					Log.Information("\t{AppId},{DepotId},{ManifestId},{Branch}", appId, depotId, manifestId, branch.Name);
				}

				if (!appPlan.TryGetValue(depotId, out var depotPlan)) {
					if (!isBlank) {
						continue;
					}

					depotPlan = appPlan[depotId] = [];
				}

				if (depotPlan.Count > 0 && !wildcardDepots.Contains(depotId)) {
					continue;
				}

				wildcardDepots.Add(depotId);
				depotPlan[manifestId] = ProgramFlags.Instance.Branches.Count > 0 ? null : branch.Name;
			}
		}
	}

	private static async Task FetchManifests(SteamSession client, DepotPlan plan) {
		var done = new HashSet<(uint, ulong)>();
		var output = Path.GetFullPath(ProgramFlags.Instance.TargetDirectory);

		Log.Information("Saving manifests to {Path}", output);

		foreach (var (appId, depot) in plan) {
			foreach (var (depotId, manifests) in depot) {
				var manifestRootPath = Path.Combine(output, depotId.ToString("D", CultureInfo.InvariantCulture), "manifest");
				Directory.CreateDirectory(manifestRootPath);

				var cdn = new ContentContext(null, client.Connections.GetConnection());
				foreach (var pair in manifests) {
					var (manifestId, branch) = pair;
					if (!done.Add((depotId, manifestId))) {
						continue;
					}

					var manifestPath = Path.Combine(manifestRootPath, manifestId.ToString("D", CultureInfo.InvariantCulture));
					if (File.Exists(manifestPath)) {
						continue;
					}

					if (string.IsNullOrEmpty(branch)) {
						if (ProgramFlags.Instance.Branches.Count > 0) {
							foreach (var selectedBranch in ProgramFlags.Instance.Branches) {
								await FetchBranchManifest(client, appId, depotId, manifestId, selectedBranch, manifestPath, cdn);
							}

							continue;
						}

						// realistically should never happen.
						branch = "public";
						Log.Warning("No branch for {Manifest} in {Depot}, falling back to {Branch}", depotId, manifestId, branch);
					}

					await FetchBranchManifest(client, appId, depotId, manifestId, branch, manifestPath, cdn);
				}
			}
		}
	}

	private static async Task FetchBranchManifest(SteamSession client, uint appId, uint depotId, ulong manifestId, string branch, string manifestPath, ContentContext cdn) {
		var token = await client.GetDepotManifestRequestCodeAsync(appId, depotId, manifestId, branch);

		while (true) {
			try {
				if (cdn.Token != null && cdn.Token.Expiration >= DateTime.Now) {
					cdn.Token = await client.RequestAuthToken(appId, depotId, cdn.Server);
				}

				// no intro wants it in a zip file with one file named "z"
				var manifest = await client.Connections.Client.DownloadManifestAsync(depotId, manifestId, token, cdn.Server, null, client.Connections.ProxyServer, cdn.Token?.Token);
				manifest.SaveToFile(manifestPath);
				Log.Information("Saved depots/{DepotId}/manifests/{ManifestId}", depotId, manifestId);
				break;
			} catch (SteamKitWebRequestException ex) {
				if (ex.StatusCode == HttpStatusCode.Forbidden && cdn.Token == null) {
					cdn.Token = await client.RequestAuthToken(appId, depotId, cdn.Server);
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

			cdn.Token = null;
			cdn.Server = client.Connections.ExchangeBrokenConnection(cdn.Server);
		}
	}

	private static async Task FetchDepotKeys(SteamSession client, DepotPlan plan) {
		var done = new HashSet<uint>();
		var output = Path.GetFullPath(ProgramFlags.Instance.TargetDirectory);
		Directory.CreateDirectory(output);

		Log.Information("Saving depot keys to {Path}", output);

		foreach (var (appId, depot) in plan) {
			foreach (var depotId in depot.Keys.Where(depotId => done.Add(depotId))) {
				var keyPath = Path.Combine(output, $"{depotId.ToString("D", CultureInfo.InvariantCulture)}.depotkey");
				if (File.Exists(keyPath)) {
					continue;
				}

				try {
					var key = await client.RequestDepotKey(depotId, appId);
					if (key == null) {
						continue;
					}

					await File.WriteAllBytesAsync(keyPath, key);
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
		Log.Information("Saving chunks to {Path}", output);

		var cts = new CancellationTokenSource();
		var parallelOptions = new ParallelOptions {
			MaxDegreeOfParallelism = ProgramFlags.Instance.Threads,
			CancellationToken = cts.Token,
		};

		Console.CancelKeyPress += ConsoleOnCancelKeyPress;

		try {
			foreach (var (appId, depot) in plan) {
				foreach (var (depotId, manifests) in depot) {
					var depotPath = Path.Combine(output, depotId.ToString("D", CultureInfo.InvariantCulture));
					var depotKeyPath = Path.Combine(output, $"{depotId.ToString("D", CultureInfo.InvariantCulture)}.depotkey");
					var depotKey = ProgramFlags.Instance.Validate && File.Exists(depotKeyPath) ? await File.ReadAllBytesAsync(depotKeyPath, cts.Token) : null;
					if (ProgramFlags.Instance.Validate && depotKey is not { Length: 32 }) {
						Log.Warning("Depot key for {Depot} is missing or invalid, cannot validate", depotId);
						depotKey = null;
					}

					var manifestRootPath = Path.Combine(depotPath, "manifest");
					Directory.CreateDirectory(depotPath);
					Directory.CreateDirectory(manifestRootPath);

					var handled = new HashSet<SHA1Hash>();
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

						var chunks = manifest.Files
											 .SelectMany(x => x.Chunks)
											 .Where(x => ProgramFlags.Instance.Validate || ShouldDownloadChunk(depotPath, x))
											 .Where(x => handled.Add(MemoryMarshal.Read<SHA1Hash>(x.ChunkID)))
											 .ToArray();

						if (chunks.Length > 0) {
							Log.Information("Beginning {Type} of {Manifest} for {Depot} ({Size})", ProgramFlags.Instance.OnlyValidate ? "validation" : "download", manifestId, depotId, chunks.Sum(x => x.UncompressedLength).GetHumanReadableBytes());

							var done = 0;
							await Parallel.ForEachAsync(chunks, parallelOptions, async (chunk, ct) => {
								if (cts.IsCancellationRequested) {
									return;
								}

								var attempt = 3;
								while (attempt-- > 0) {
									var chunkId = Convert.ToHexStringLower(chunk.ChunkID!);
									var chunkPath = Path.Combine(depotPath, chunkId);
									var exit = await FetchChunk(client, chunkPath, appId, depotId, depotKey, chunk);
									if (exit && !cts.IsCancellationRequested) {
										try {
											await cts.CancelAsync();
										} catch {
											// ignored
										}

										return;
									}

									if (!File.Exists(chunkPath)) {
										Log.Information("{Current} did not actually download? Retrying...", chunkId);
										try {
											await Task.Delay(TimeSpan.FromSeconds(1), ct);
										} catch (TaskCanceledException) {
											// ignored
										}
									} else {
										Log.Information("[{Done}/{Total}] {Current}", Interlocked.Increment(ref done), chunks.Length, chunkId);
										break;
									}
								}
							});

							if (!cts.IsCancellationRequested) {
								Log.Information("Processed {Total} new chunks", chunks.Length);
								continue;
							}

							Log.Fatal("Encountered an unrecoverable error, exiting so we don't potentially flood the CDN");
							return;
						}

						Log.Debug("Manifest has no new chunks");
					}
				}
			}
		} finally {
			Console.CancelKeyPress -= ConsoleOnCancelKeyPress;
			cts.Dispose();
		}

		return;

		[SuppressMessage("ReSharper", "AccessToDisposedClosure")]
		void ConsoleOnCancelKeyPress(object? sender, ConsoleCancelEventArgs e) {
			if (cts.IsCancellationRequested) {
				return;
			}

			try {
				cts.Cancel();
			} catch {
				// ignored
			}

			e.Cancel = true;
		}
	}

	private static bool ShouldDownloadChunk(string depotPath, DepotManifest.ChunkData chunk) {
		var fileInfo = new FileInfo(Path.Combine(depotPath, Convert.ToHexStringLower(chunk.ChunkID!)));
		if (!fileInfo.Exists) {
			return true;
		}

		if (fileInfo.Length != chunk.CompressedLength) {
			return true;
		}

		return false;
	}

	private static async Task<bool> FetchChunk(SteamSession client, string chunkPath, uint appId, uint depotId, byte[]? depotKey, DepotManifest.ChunkData chunk) {
		var chunkId = Convert.ToHexStringLower(chunk.ChunkID!);
		var buffer = ArrayPool<byte>.Shared.Rent((int) chunk.CompressedLength);

		try {
			if (File.Exists(chunkPath)) {
				var fileInfo = new FileInfo(chunkPath);
				if (!ProgramFlags.Instance.Validate) {
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
					if (ValidateChunk(depotKey, chunk, existing, ProgramFlags.Instance.Validate)) {
						return false;
					}

					if (Console.IsErrorRedirected) {
						await Console.Error.WriteLineAsync(chunkId);
					}

					Log.Warning("Chunk {Id} failed validation", chunkId);
				}

				if (ProgramFlags.Instance.OnlyValidate) {
					return false;
				}
			}

			if (ProgramFlags.Instance.OnlyValidate) {
				if (Console.IsErrorRedirected) {
					await Console.Error.WriteLineAsync(chunkId);
				}

				Log.Warning("Chunk {Id} does not exist", chunkId);
				return false;
			}

			var server = client.Connections.GetConnection();
			SteamContent.CDNAuthToken? cdnToken = null;

			var attempts = client.Connections.Attempts * 2;
			var currentAttempt = 0;
			var isRetry = false;
			while (currentAttempt++ < attempts) {
				try {
					if (cdnToken != null && cdnToken.Expiration >= DateTime.Now) {
						cdnToken = await client.RequestAuthToken(appId, depotId, server);
					}

					var n = await client.Connections.Client.DownloadDepotChunkAsync(depotId, chunk, server, buffer, null, client.Connections.ProxyServer, cdnToken?.Token);
					if (!ValidateChunk(depotKey, chunk, buffer.AsSpan(0, n), ProgramFlags.Instance.Validate || ProgramFlags.Instance.ValidateNew)) {
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
							Log.Error("Chunk {Id} for {DepotId} is not found, waiting one second...", chunkId, depotId);
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
							return false;
					}

					Log.Error("Chunk {Id} for {DepotId} got {Code}, rotating servers", chunkId, depotId, ex.StatusCode);
				} catch (OperationCanceledException) {
					if (!isRetry) {
						Log.Error("Chunk {Id} for {DepotId} timed out, waiting one second...", chunkId, depotId);
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
				continue;

			delay_retry:
				await Task.Delay(TimeSpan.FromSeconds(1));
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

		return false;
	}

	private static bool ValidateChunk(byte[]? depotKey, DepotManifest.ChunkData chunk, Span<byte> buffer, bool should) {
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

	private static async Task<(DepotPlan, BranchPasswords)> ParsePlan(ProgramFlags flags) {
		var plan = new DepotPlan();
		var passwords = new BranchPasswords();
		if (!File.Exists(flags.ArchivePlanFile)) {
			if (uint.TryParse(flags.ArchivePlanFile, NumberStyles.Integer, CultureInfo.InvariantCulture, out var appId)) {
				plan[appId] = [];
			} else {
				Log.Error("Cannot open {Path}", flags.ArchivePlanFile);
			}

			return (plan, passwords);
		}

		await using var stream = new FileStream(flags.ArchivePlanFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
		using var reader = new StreamReader(stream);

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

			if (!uint.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var appId)) {
				Log.Error("Cannot parse line {Parts} (invalid app id {id})", line, parts[0]);
				continue;
			}

			if (parts is [_, "password", _]) {
				if (!passwords.TryGetValue(appId, out var appPasswords)) {
					appPasswords = passwords[appId] = [];
				}

				appPasswords.Add(parts[2]);
				continue;
			}

			var depotId = 0u;
			var manifestId = 0ul;
			var branch = parts.ElementAtOrDefault(3) ?? "public";

			switch (parts.Length) {
				case > 1 when !uint.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out depotId):
					Log.Error("Cannot parse line {Parts} (invalid depot id {id})", line, parts[1]);
					continue;
				case > 2 when !ulong.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out manifestId):
					Log.Error("Cannot parse line {Parts} (invalid manifest id {Id})", line, parts[2]);
					continue;
			}

			if (!plan.TryGetValue(appId, out var app)) {
				app = plan[appId] = [];
			}

			if (depotId == 0) {
				continue;
			}

			if (!app.TryGetValue(depotId, out var depot)) {
				depot = app[depotId] = [];
			}

			if (manifestId == 0) {
				continue;
			}

			depot[manifestId] = branch;
		}

		return (plan, passwords);
	}

	private class ContentContext(SteamContent.CDNAuthToken? token, Server server) {
		public SteamContent.CDNAuthToken? Token { get; set; } = token;
		public Server Server { get; set; } = server;
	}
}
