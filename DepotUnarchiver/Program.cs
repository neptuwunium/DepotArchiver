// SPDX-FileCopyrightText: 2025 Legiayayana
//
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.IO.MemoryMappedFiles;
using Serilog;
using Serilog.Events;
using SteamKit2;
using SteamKit2.CDN;

namespace DepotUnarchiver;

internal static class Program {
	private record ChunkLoadOp(MemoryMappedFile File, DepotManifest.ChunkData Chunk, string ChunkPath, byte[] DepotKey);

	private static void Main() {
		Log.Logger = new LoggerConfiguration().MinimumLevel.Is(Debugger.IsAttached ? LogEventLevel.Debug : LogEventLevel.Information).WriteTo.Console().CreateLogger();

		var flags = ProgramFlags.Instance;

		var depotPath = Path.Combine(Path.GetFullPath(flags.DepotDirectory), flags.DepotId.ToString("D", CultureInfo.InvariantCulture));
		var depotKeyPath = Path.Combine(Path.GetFullPath(flags.DepotDirectory), flags.DepotId.ToString("D", CultureInfo.InvariantCulture) + ".depotkey");
		var manifestPath = Path.Combine(depotPath, "manifest", flags.ManifestId.ToString("D", CultureInfo.InvariantCulture));

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

		var depotKey = File.ReadAllBytes(depotPath);
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

		foreach (var file in manifest.Files) {
			var dest = Path.GetDirectoryName(Path.Combine(flags.TargetDirectory, file.FileName));
			if (string.IsNullOrEmpty(dest)) {
				continue;
			}

			if ((file.Flags & EDepotFileFlag.Symlink) != 0) {
				if (string.IsNullOrEmpty(file.LinkTarget)) {
					continue;
				}

				var src = Path.GetDirectoryName(Path.Combine(flags.TargetDirectory, file.LinkTarget));

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

			var memoryMappedFile = MemoryMappedFile.CreateNew(dest, (long) file.TotalSize);
			fileMaps.Add(memoryMappedFile);

			foreach (var chunk in file.Chunks) {
				if (chunk.ChunkID == null) {
					continue;
				}

				var chunkPath = Path.Combine(depotPath, Convert.ToHexStringLower(chunk.ChunkID));
				ops.Add(new ChunkLoadOp(memoryMappedFile, chunk, chunkPath, depotKey));
			}
		}

		Parallel.ForEach(ops, ProcessChunk);

		foreach (var fileMap in fileMaps) {
			fileMap.Dispose();
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
			var accessor = map.CreateViewAccessor((long) chunk.Offset, n);
			accessor.WriteArray(0, uncompressed, 0, n);
		} catch (Exception ex) {
			Log.Error(ex, "Cannot process chunk {Chunk}", Path.GetFileName(path));
		} finally {
			ArrayPool<byte>.Shared.Return(compressed);
			ArrayPool<byte>.Shared.Return(uncompressed);
		}
	}
}
