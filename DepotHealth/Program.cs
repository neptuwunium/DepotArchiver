// SPDX-FileCopyrightText: 2025 Legiayayana
//
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers;
using System.Diagnostics;
using System.Runtime.InteropServices;
using DepotCommon;
using Serilog;
using Serilog.Events;
using SteamKit2;
using SteamKit2.CDN;

namespace DepotHealth;

internal static class Program {
	private static long CorruptChunks;

	private static int Main() {
		Log.Logger = new LoggerConfiguration().MinimumLevel.Is(Debugger.IsAttached ? LogEventLevel.Debug : LogEventLevel.Information).WriteTo.Console().CreateLogger();

		var flags = ProgramFlags.Instance;

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

			using (var stream = new FileStream(depotKeyPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) {
				stream.ReadExactly(depotKey);
			}

			var processedChunks = new HashSet<SHA1Hash>();
			foreach (var manifest in Directory.EnumerateFiles(manifestFolder)) {
				try {
					ProcessDepotManifest(manifest, depotPath, depotKey, processedChunks);
				} catch {
					Log.Error("Cannot process manifest {Path}", manifest);
				}
			}
		}

		return CorruptChunks > 0 ? 1 : 0;
	}

	private static void ProcessDepotManifest(string manifestPath, string depotPath, byte[] depotKey, HashSet<SHA1Hash> processedChunks) {
		var manifest = DepotManifest.LoadFromFile(manifestPath);
		if (manifest == null) {
			Log.Error("Cannot load manifest {Path}", manifestPath);
			return;
		}

		if (manifest.Files == null || manifest.Files.Count == 0) {
			return;
		}

		try {
			manifest.DecryptFilenames(depotKey);
		} catch {
			Log.Error("Cannot decrypt manifest {Path}", manifestPath);
		}

		Log.Information("Processing manifest {Path}", manifestPath);

		var depotId = Path.GetFileName(depotPath);

		var ops = new List<ChunkLoadOp>();
		foreach (var chunk in manifest.Files.Where(file =>
										  (file.Flags & EDepotFileFlag.Directory) == 0 &&
										  (file.Flags & EDepotFileFlag.Symlink) == 0)
									  .SelectMany(file => file.Chunks)) {
			if (chunk.ChunkID == null) {
				throw new UnreachableException();
			}

			var chunkId = MemoryMarshal.Read<SHA1Hash>(chunk.ChunkID);

			if (processedChunks.Add(chunkId)) {
				ops.Add(new ChunkLoadOp(depotId, chunk, Path.Combine(depotPath, chunkId.ToString()), depotKey));
			}
		}

		Parallel.ForEach(ops, CheckChunk);
	}

	private static void CheckChunk(ChunkLoadOp op) {
		var (depotId, chunk, chunkPath, depotKey) = op;
		var compressed = ArrayPool<byte>.Shared.Rent((int) chunk.CompressedLength);
		var uncompressed = ArrayPool<byte>.Shared.Rent((int) chunk.UncompressedLength);
		try {
			using (var stream = new FileStream(chunkPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) {
				stream.ReadExactly(compressed.AsSpan(0, (int) chunk.CompressedLength));
			}

			DepotChunk.Process(chunk, compressed.AsSpan(0, (int) chunk.CompressedLength), uncompressed, depotKey);
			Log.Debug("Chunk {Chunk}, Depot {Depot}: OK", Path.GetFileName(chunkPath), depotId);
		} catch (Exception ex) {
			Interlocked.Increment(ref CorruptChunks);
			Console.Error.WriteLine(chunkPath);
			Log.Error(ex, "Chunk {Chunk}, Depot {Depot}: ERR", Path.GetFileName(chunkPath), depotId);
		} finally {
			ArrayPool<byte>.Shared.Return(compressed);
			ArrayPool<byte>.Shared.Return(uncompressed);
		}
	}

	private record ChunkLoadOp(string DepotId, DepotManifest.ChunkData Chunk, string ChunkPath, byte[] DepotKey);
}
