// SPDX-FileCopyrightText: 2025 Legiayayana
//
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using DepotCommon;
using DepotCommon.Steam;
using DragonLib;
using Serilog;
using Serilog.Events;
using SteamKit2;
using SteamKit2.CDN;

namespace DepotHealth;

internal record ManifestFileContext(
	string Path,
	SHA1Hash Hash,
	ulong CompressedSize,
	ulong Size,
	[property: JsonConverter(typeof(JsonStringEnumConverter<EDepotFileFlag>))]
	EDepotFileFlag Flags);

internal record ManifestContext(ulong TotalCompressedSize, ulong TotalSize, List<ManifestFileContext> Files);

[JsonConverter(typeof(JsonContextConverter))]
internal record JsonContext(Dictionary<uint, HashSet<string>> BadChunks, Dictionary<uint, Dictionary<ulong, ManifestContext>> Manifests);

internal class JsonContextConverter : JsonConverter<JsonContext> {
	public override JsonContext Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => throw new NotSupportedException();

	public override void Write(Utf8JsonWriter writer, JsonContext value, JsonSerializerOptions options) {
		writer.WriteStartObject();

		if (value.BadChunks.Count > 0) {
			writer.WritePropertyName(options.PropertyNamingPolicy?.ConvertName(nameof(JsonContext.BadChunks)) ?? nameof(JsonContext.BadChunks));
			JsonSerializer.Serialize(writer, value.BadChunks, options);
		}

		if (value.Manifests.Count > 0) {
			writer.WritePropertyName(options.PropertyNamingPolicy?.ConvertName(nameof(JsonContext.Manifests)) ?? nameof(JsonContext.Manifests));
			JsonSerializer.Serialize(writer, value.Manifests, options);
		}

		writer.WriteEndArray();
	}
}

internal static class Program {
	private static long CorruptChunks;
	private static SteamSession? Session { get; set; }

	internal static JsonContext Context { get; } = new([], []);

	internal static JsonSerializerOptions JsonOptions { get; } = new() {
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
	};

	private static async Task<int> Main() {
		Log.Logger = new LoggerConfiguration()
					 .MinimumLevel.Is(Debugger.IsAttached ? LogEventLevel.Debug : LogEventLevel.Information)
					 .WriteTo.Console().CreateLogger();

		var flags = ProgramFlags.Instance;

		Thread? loop = null;

		if (flags.Repair) {
			ChunkDownload.Validate = false;
			ChunkDownload.ValidateNew = true;
			ChunkDownload.OnlyValidate = false;

			Session = new SteamSession(new SteamUser.LogOnDetails {
				MachineName = $"{Environment.MachineName} (Archival)",
				LoginID = 0xDEE2DEE2, // DEERDEER
			});

			loop = new Thread(clientObj => {
				((SteamSession) clientObj!).TickCallbacks();
			});
			loop.Start(Session);
			Session.Connect();

			try {
				await Session.FullyLoggedInTask;
			} catch (TaskCanceledException) {
				Log.Error("Could not fully log in, exiting");
				loop.Join();
				return 2;
			}
		}

		var depotKey = new byte[0x20];

		foreach (var manifestFolder in Directory.EnumerateDirectories(flags.DepotDirectory, "*manifest*", SearchOption.AllDirectories)) {
			var depotPath = Path.GetDirectoryName(manifestFolder.TrimEnd('/', '\\'))?.TrimEnd('/', '\\');
			if (string.IsNullOrEmpty(depotPath)) {
				continue;
			}

			var depotKeyPath = depotPath + ".depotkey";
			if (!File.Exists(depotKeyPath)) {
				Log.Error("Cannot find Depot Key for {Depot}", Path.GetFileName(depotPath));
			}

			await using (var stream = new FileStream(depotKeyPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) {
				stream.ReadExactly(depotKey);
			}

			var processedChunks = new HashSet<SHA1Hash>();
			foreach (var manifest in GetManifests(depotKey, manifestFolder)) {
				try {
					ProcessDepotManifest(manifest, depotPath, depotKey, processedChunks);
				} catch (Exception e) {
					Log.Error(e, "Cannot process manifest {Id} (depot {DepotId})", manifest.ManifestGID, manifest.DepotID);
				}
			}
		}

		if (flags.OutputJson && (Context.BadChunks.Count > 0 || Context.Manifests.Count > 0)) {
			await Console.Error.WriteLineAsync(JsonSerializer.Serialize(Context, JsonOptions));
		}

		try {
			Session?.Disconnect();
		} catch (Exception e) {
			Log.Warning(e, "Crashed while exiting?");
			Environment.Exit(0);
			// what
		}

		loop?.Join();

		return CorruptChunks > 0 ? 1 : 0;
	}

	private static IEnumerable<DepotManifest> GetManifests(byte[] depotKey, string manifestFolder) {
		var manifests = GetManifestsInner(depotKey, manifestFolder);
		return !ProgramFlags.Instance.Meta ? manifests : manifests.OrderBy(x => x.CreationTime);
	}

	private static IEnumerable<DepotManifest> GetManifestsInner(byte[] depotKey, string manifestFolder) {
		foreach (var manifestPath in Directory.EnumerateFiles(manifestFolder)) {
			var manifest = DepotManifest.LoadFromFile(manifestPath);

			if (manifest?.Files == null || manifest.Files.Count == 0) {
				continue;
			}

			try {
				manifest.DecryptFilenames(depotKey);
			} catch {
				Log.Error("Cannot decrypt manifest {Path}", manifestPath);
			}

			yield return manifest;
		}
	}

	private static void ProcessDepotManifest(DepotManifest manifest, string depotPath, byte[] depotKey, HashSet<SHA1Hash> processedChunks) {
		var flags = ProgramFlags.Instance;
		if (flags.Meta) {
			Log.Information("Depot: {DepotId}; Manifest: {Id}; Size: {Compressed} ({Uncompressed})", manifest.DepotID, manifest.ManifestGID, manifest.TotalCompressedSize.GetHumanReadableBytes(), manifest.TotalUncompressedSize.GetHumanReadableBytes());
			ManifestContext? manifestContext = null;

			if (flags.OutputJson) {
				manifestContext = new ManifestContext(manifest.TotalCompressedSize, manifest.TotalUncompressedSize, []);
			}

			if (flags.List) {
				foreach (var file in manifest.Files!.OrderBy(x => x.FileName).Where(f => (f.Flags & EDepotFileFlag.Directory) == 0)) {
					var hash = MemoryMarshal.Read<SHA1Hash>(file.FileHash);
					Log.Information("-> {FileName} ({Hash}, Size: {Compressed})", file.FileName, hash, file.TotalSize.GetHumanReadableBytes());
					manifestContext?.Files.Add(new ManifestFileContext(file.FileName, hash, (ulong) file.Chunks.Sum(x => x.CompressedLength), file.TotalSize, file.Flags));
				}
			}
		} else {
			Log.Information("Processing manifest {Id}", manifest.ManifestGID);
		}

		if (flags.OnlyInfo) {
			return;
		}

		var depotId = manifest.DepotID;
		var ops = new List<ChunkLoadOp>();
		var maxChunk = 0UL;
		foreach (var chunk in manifest.Files!.Where(file =>
										  (file.Flags & EDepotFileFlag.Directory) == 0 &&
										  (file.Flags & EDepotFileFlag.Symlink) == 0)
									  .SelectMany(file => file.Chunks)) {
			if (chunk.ChunkID == null) {
				throw new UnreachableException();
			}

			var chunkId = MemoryMarshal.Read<SHA1Hash>(chunk.ChunkID);

			if (!processedChunks.Add(chunkId)) {
				continue;
			}

			ops.Add(new ChunkLoadOp(depotId, chunk, Path.Combine(depotPath, chunkId.ToString()), depotKey));

			if (chunk.CompressedLength > maxChunk) {
				maxChunk = chunk.CompressedLength;
			}

			if (chunk.UncompressedLength > maxChunk) {
				maxChunk = chunk.UncompressedLength;
			}
		}

		if (flags.OutputJson) {
			Context.BadChunks[depotId] = [];
		}

		Parallel.ForEach(ops, new ParallelOptions { MaxDegreeOfParallelism = flags.Threads },
			() => (
				Compressed: ArrayPool<byte>.Shared.Rent(int.CreateChecked(maxChunk)),
				Uncompressed: ArrayPool<byte>.Shared.Rent(int.CreateChecked(maxChunk))
			),
			CheckChunk,
			pair => {
				ArrayPool<byte>.Shared.Return(pair.Compressed);
				ArrayPool<byte>.Shared.Return(pair.Uncompressed);
			});

		if (!flags.OutputJson) {
			return;
		}

		if (Context.BadChunks[depotId].Count == 0) {
			Context.BadChunks.Remove(depotId);
		}
	}

	private static (byte[], byte[]) CheckChunk(ChunkLoadOp op, ParallelLoopState state, (byte[], byte[]) pair) {
		var (depotId, chunk, chunkPath, depotKey) = op;
		var (compressed, uncompressed) = pair;
		try {
			var compressedSpan = compressed.AsSpan(0, (int) chunk.CompressedLength);
			using (var stream = new FileStream(chunkPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) {
				stream.ReadExactly(compressedSpan);
			}

			DepotChunk.Process(chunk, compressedSpan, uncompressed, depotKey);
			Log.Debug("Chunk {Chunk}, Depot {Depot}: OK", Path.GetFileName(chunkPath), depotId);
		} catch {
			Interlocked.Increment(ref CorruptChunks);
			Log.Error("Chunk {Chunk}, Depot {Depot}: ERR", Path.GetFileName(chunkPath), depotId);
			if (ProgramFlags.Instance.OutputJson) {
				Context.BadChunks[depotId].Add(chunkPath);
			} else {
				Console.Error.WriteLine(chunkPath);
			}

			if (ProgramFlags.Instance.Repair) {
				Repair(chunkPath, depotId, depotKey, chunk).Wait();
			}
		}

		return (compressed, uncompressed);
	}

	private static async Task Repair(string chunkPath, uint depotId, byte[] depotKey, DepotManifest.ChunkData chunk) {
		File.Delete(chunkPath);

		var attempt = 3;
		while (attempt-- > 0) {
			// todo: gotta preserve the AppId somehow...
			// rn just truncating the last digit.
			var exit = await ChunkDownload.FetchChunk(Session!, chunkPath, depotId / 10 * 10, depotId, depotKey, chunk);
			if (exit) {
				return;
			}

			var chunkId = MemoryMarshal.Read<SHA1Hash>(chunk.ChunkID);
			if (!File.Exists(chunkPath)) {
				Log.Information("{Current} did not actually download? Retrying...", chunkId);
				try {
					await Task.Delay(TimeSpan.FromSeconds(1));
				} catch (TaskCanceledException) {
					// ignored
				}
			} else {
				Log.Information("{Current} repaired", chunkId);
				break;
			}
		}
	}

	private record ChunkLoadOp(uint DepotId, DepotManifest.ChunkData Chunk, string ChunkPath, byte[] DepotKey);
}
