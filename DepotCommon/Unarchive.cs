// SPDX-FileCopyrightText: 2025 Legiayayana
//
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers;
using System.Globalization;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using DragonLib.Extensions;
using Serilog;
using SteamKit2;
using SteamKit2.CDN;

namespace DepotCommon;

public static class Unarchive {
	public static void ProcessManifest(IUnarchiveOptions flags, string targetDirectory, string manifestsPath, ulong manifestId, string depotKeyPath, string depotPath) {
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
		var maxChunk = 0UL;

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
				Log.Information("Skipping {Path} (already exists)", dest);
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

			using (var stream = new FileStream(dest, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite)) {
				if (stream.Length != (long) file.TotalSize) {
					Log.Information("[{Depot}/{Manifest}] Allocating {Path}", manifest.DepotID, manifest.ManifestGID, dest);
					stream.SetLength((long) file.TotalSize);
					stream.Flush();
				}
			}

			sum += file.TotalSize;

			if (file.TotalSize == 0) {
				continue;
			}

			var memoryMappedFile = MemoryMappedFile.CreateFromFile(dest, FileMode.Open, null, (long) file.TotalSize, MemoryMappedFileAccess.ReadWrite);
			fileMaps.Add(memoryMappedFile);

			foreach (var chunk in file.Chunks) {
				if (chunk.ChunkID == null) {
					continue;
				}

				var chunkPath = Path.Combine(depotPath, Convert.ToHexString(chunk.ChunkID).ToLowerInvariant());
				ops.Add(new ChunkLoadOp(memoryMappedFile, chunk, chunkPath, depotKey, manifest));

				if (chunk.CompressedLength > maxChunk) {
					maxChunk = chunk.CompressedLength;
				}

				if (chunk.UncompressedLength > maxChunk) {
					maxChunk = chunk.UncompressedLength;
				}
			}
		}

		if (ops.Count > 0) {
			Parallel.ForEach(ops, new ParallelOptions { MaxDegreeOfParallelism = flags.Threads },
				() => (
					Compressed: ArrayPool<byte>.Shared.Rent(int.CreateChecked(maxChunk)),
					Uncompressed: ArrayPool<byte>.Shared.Rent(int.CreateChecked(maxChunk))
				),
				ProcessChunk,
				pair => {
					ArrayPool<byte>.Shared.Return(pair.Compressed);
					ArrayPool<byte>.Shared.Return(pair.Uncompressed);
				});
		}

		foreach (var fileMap in fileMaps) {
			fileMap.Dispose();
		}

		if (sum > 0) {
			Log.Information("Unpacked {Size} bytes for manifest {ManifestId} (Depot {DepotId})", sum.HumanReadableBytes, manifestId, manifest.DepotID);
		}

		if (Directory.Exists(targetDirectory)) {
			if (flags.Time) {
				Directory.SetCreationTimeUtc(targetDirectory, manifest.CreationTime);
			}

			if (flags.Validate || flags.Time) {
				foreach (var file in manifest.Files.Where(file => flags.Filter.Count == 0 || flags.Filter.Any(x => x.IsMatch(file.FileName)))) {
					var dest = Path.Combine(targetDirectory, file.FileName);
					if (string.IsNullOrEmpty(dest)) {
						continue;
					}

					if (flags.Time) {
						if ((file.Flags & EDepotFileFlag.Directory) != 0) {
							Directory.SetCreationTimeUtc(dest, manifest.CreationTime);
						} else {
							File.SetCreationTimeUtc(dest, manifest.CreationTime);
						}
					}

					if (flags.Validate) {
						ValidateFile(file, dest);
					}
				}
			}
		}

		return;

		void ValidateFile(DepotManifest.FileData file, string dest) {
			if (!flags.Validate || file.FileHash.Length <= 0 || !File.Exists(dest) || (new FileInfo(dest).Attributes & FileAttributes.ReparsePoint) != 0) {
				return;
			}

			var expectedHash = MemoryMarshal.Read<SHA1Hash>(file.FileHash);
			using var stream = new FileStream(dest, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
			var ourHash = MemoryMarshal.Read<SHA1Hash>(SHA1.HashData(stream));
			if (ourHash != expectedHash) {
				Log.Warning("{Path} did not extract correctly!", dest);
			}
		}
	}

	private static (byte[], byte[]) ProcessChunk(ChunkLoadOp op, ParallelLoopState state, (byte[], byte[]) pool) {
		var (map, chunk, path, depotKey, manifest) = op;

		var (compressed, uncompressed) = pool;
		try {
			var compressedSpan = compressed.AsSpan(0, (int) chunk.CompressedLength);

			using (var stream = new FileStream(op.ChunkPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) {
				stream.ReadExactly(compressedSpan);
			}

			var n = DepotChunk.Process(chunk, compressedSpan, uncompressed, depotKey);
			using var accessor = map.CreateViewAccessor((long) chunk.Offset, n);
			accessor.WriteArray(0, uncompressed, 0, n);

			Log.Information("[{Depot}/{Manifest}] Processed Chunk {Chunk}", manifest.DepotID, manifest.ManifestGID, Path.GetFileName(path));
		} catch (Exception ex) {
			Log.Error(ex, "Cannot process chunk {Chunk}", Path.GetFileName(path));
		}

		return pool;
	}

	private record ChunkLoadOp(MemoryMappedFile File, DepotManifest.ChunkData Chunk, string ChunkPath, byte[] DepotKey, DepotManifest Manifest);
}
