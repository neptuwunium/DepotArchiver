// SPDX-FileCopyrightText: 2025 Legiayayana
//
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.IO.MemoryMappedFiles;
using DragonLib;
using Serilog;
using Serilog.Events;
using SteamKit2;
using SteamKit2.CDN;

namespace DepotUnarchiver;

internal static class Program {
	private static void Main() {
		Log.Logger = new LoggerConfiguration().MinimumLevel.Is(Debugger.IsAttached ? LogEventLevel.Debug : LogEventLevel.Information).WriteTo.Console().CreateLogger();

		var flags = ProgramFlags.Instance;

		var appDepotPlan = ParsePlan(flags);
		var depotPlan = new Dictionary<uint, HashSet<ulong>>();

		foreach (var (id, manifestIds) in appDepotPlan) {
			if (manifestIds.Count > 0) {
				depotPlan[id] = manifestIds;
				continue;
			}

			var idStr = id.ToString("D", CultureInfo.InvariantCulture);
			var appPath = Path.Combine(Path.GetFullPath(flags.DepotDirectory), idStr + ".vdf");

			if (!File.Exists(appPath)) {
				depotPlan[id] = manifestIds;
				continue;
			}

			var app = KeyValue.LoadAsText(appPath);
			if (app == null) {
				continue;
			}

			foreach (var depot in app.Children.Where(x => x.Name == "depots").FirstOrDefault(KeyValue.Invalid).Children) {
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
					if (!depotPlan.TryGetValue(depotId, out var depotManifests)) {
						depotPlan[depotId] = depotManifests = [];
					}

					depotManifests.Add(manifestId);
				}
			}
		}

		foreach (var (depotId, manifestIds) in depotPlan) {
			var depotIdStr = depotId.ToString("D", CultureInfo.InvariantCulture);
			var depotPath = Path.Combine(Path.GetFullPath(flags.DepotDirectory), depotIdStr);
			var depotKeyPath = Path.Combine(Path.GetFullPath(flags.DepotDirectory), depotIdStr + ".depotkey");
			var manifestsPath = Path.Combine(depotPath, "manifest");

			if (manifestIds.Count == 0) {
				foreach (var manifestId in Directory.EnumerateFiles(manifestsPath, "*", SearchOption.TopDirectoryOnly)) {
					manifestIds.Add(ulong.Parse(Path.GetFileName(manifestId), NumberStyles.Integer));
				}
			}

			var targetDirectory = flags.TargetDirectory;
			if (flags.AppendDepot) {
				targetDirectory = Path.Combine(targetDirectory, depotIdStr);
			}

			foreach (var manifestId in manifestIds) {
				ProcessManifest(flags, targetDirectory, manifestsPath, manifestId, depotKeyPath, depotPath);
			}
		}
	}

	private static Dictionary<uint, HashSet<ulong>> ParsePlan(ProgramFlags flags) {
		var result = new Dictionary<uint, HashSet<ulong>>();
		var lastDepotId = flags.DepotId.Contains(':', StringComparison.CurrentCulture)
			? ParseDepotManifest(result, flags.DepotId, 0)
			: uint.Parse(flags.DepotId, NumberStyles.Integer, CultureInfo.InvariantCulture);

		result[lastDepotId] = [];

		_ = flags.ManifestIds.Aggregate(lastDepotId, (current, manifestId) => ParseDepotManifest(result, manifestId, current));

		return result;
	}

	private static uint ParseDepotManifest(Dictionary<uint, HashSet<ulong>> result, string value, uint lastDepotId) {
		Span<Range> ranges = stackalloc Range[2];
		Span<string> separators = [":"];
		var chars = value.AsSpan();
		var n = chars.SplitAny(ranges, separators);
		ulong manifestId;
		if (n == 2) {
			lastDepotId = uint.Parse(chars[ranges[0]], NumberStyles.Integer, CultureInfo.InvariantCulture);
			manifestId = ranges[1].GetOffsetAndLength(chars.Length).Length == 0
				? 0
				: ulong.Parse(chars[ranges[1]], NumberStyles.Integer, CultureInfo.InvariantCulture);
		} else {
			if (lastDepotId == 0) {
				return lastDepotId;
			}

			manifestId = ulong.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture);
		}

		if (!result.TryGetValue(lastDepotId, out var manifests)) {
			result[lastDepotId] = manifests = [];
		}

		if (manifestId > 0) {
			manifests.Add(manifestId);
		}

		return lastDepotId;
	}

	private static void ProcessManifest(ProgramFlags flags, string targetDirectory, string manifestsPath, ulong manifestId, string depotKeyPath, string depotPath) {
		var manifestIdStr = manifestId.ToString("D", CultureInfo.InvariantCulture);
		var manifestPath = Path.Combine(manifestsPath, manifestIdStr);

		var manifest = DepotManifest.LoadFromFile(manifestPath);
		if (manifest == null) {
			Log.Error("Cannot load manifest {Path}", manifestPath);
			return;
		}

		if (manifest.Files == null || manifest.Files.Count == 0) {
			return;
		}

		if (!File.Exists(depotKeyPath)) {
			Log.Error("Depot key does not exist, need {Path}", depotKeyPath);
			return;
		}

		var depotKey = File.ReadAllBytes(depotKeyPath);
		if (depotKey.Length != 32) {
			Log.Error("Invalid depot key, expected a 32-byte key");
			return;
		}

		if (!manifest.DecryptFilenames(depotKey)) {
			Log.Error("Could not decrypt filenames");
			return;
		}

		var fileMaps = new List<MemoryMappedFile>();
		var ops = new List<ChunkLoadOp>();

		if (flags.AppendManifest) {
			targetDirectory = Path.Combine(targetDirectory, manifestIdStr);
		}

		var sum = 0UL;

		foreach (var file in manifest.Files.Where(file => flags.Filter.Count == 0 || flags.Filter.Any(x => x.IsMatch(file.FileName)))) {
			if (flags.List) {
				if ((file.Flags & EDepotFileFlag.Directory) == 0) {
					if (Console.IsErrorRedirected) {
						Console.Error.WriteLine(file.FileName);
					} else {
						Log.Information("{0}", file.FileName);
					}
				}

				continue;
			}

			var dest = Path.Combine(targetDirectory, file.FileName);
			if (string.IsNullOrEmpty(dest)) {
				continue;
			}

			if (flags.NoClobber && Path.Exists(dest)) {
				continue;
			}

			if ((file.Flags & EDepotFileFlag.Symlink) != 0) {
				if (string.IsNullOrEmpty(file.LinkTarget)) {
					continue;
				}

				var src = Path.Combine(targetDirectory, file.LinkTarget);

				if (string.IsNullOrEmpty(src)) {
					continue;
				}

				Log.Information("Symlinking {From} to {To}", dest, src);
				if ((file.Flags & EDepotFileFlag.Directory) != 0) {
					Directory.CreateSymbolicLink(dest, src);
				} else {
					File.CreateSymbolicLink(dest, src);
				}

				continue;
			}

			if ((file.Flags & EDepotFileFlag.Directory) != 0) {
				Directory.CreateDirectory(dest);
				continue;
			}

			var directory = Path.GetDirectoryName(dest)!;
			Directory.CreateDirectory(directory);

			Log.Information("Allocating {Path}", dest);
			var stream = new FileStream(dest, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite);
			stream.SetLength((long) file.TotalSize);
			sum += file.TotalSize;

			var memoryMappedFile = MemoryMappedFile.CreateFromFile(stream, null, stream.Length, MemoryMappedFileAccess.ReadWrite, HandleInheritability.None, false);
			fileMaps.Add(memoryMappedFile);

			foreach (var chunk in file.Chunks) {
				if (chunk.ChunkID == null) {
					continue;
				}

				var chunkPath = Path.Combine(depotPath, Convert.ToHexString(chunk.ChunkID).ToLowerInvariant());
				ops.Add(new ChunkLoadOp(memoryMappedFile, chunk, chunkPath, depotKey));
			}
		}

		if (ops.Count > 0) {
			Parallel.ForEach(ops, ProcessChunk);
		}

		foreach (var fileMap in fileMaps) {
			fileMap.Dispose();
		}

		if (sum > 0) {
			Log.Information("Unpacked {Size} bytes for manifest {ManifestId} (Depot {DepotId})", sum.GetHumanReadableBytes(), manifestId, manifest.DepotID);
		}
	}

	private static void ProcessChunk(ChunkLoadOp op) {
		var (map, chunk, path, depotKey) = op;

		var compressed = ArrayPool<byte>.Shared.Rent((int) chunk.CompressedLength);
		var uncompressed = ArrayPool<byte>.Shared.Rent((int) chunk.UncompressedLength);
		try {
			var compressedSpan = compressed.AsSpan(0, (int) chunk.CompressedLength);

			using (var stream = new FileStream(op.ChunkPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) {
				stream.ReadExactly(compressedSpan);
			}

			var n = DepotChunk.Process(chunk, compressedSpan, uncompressed, depotKey);
			using var accessor = map.CreateViewAccessor((long) chunk.Offset, n);
			accessor.WriteArray(0, uncompressed, 0, n);

			Log.Information("Processed Chunk {Chunk}", Path.GetFileName(path));
		} catch (Exception ex) {
			Log.Error(ex, "Cannot process chunk {Chunk}", Path.GetFileName(path));
		} finally {
			ArrayPool<byte>.Shared.Return(compressed);
			ArrayPool<byte>.Shared.Return(uncompressed);
		}
	}

	private record ChunkLoadOp(MemoryMappedFile File, DepotManifest.ChunkData Chunk, string ChunkPath, byte[] DepotKey);
}
