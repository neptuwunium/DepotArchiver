using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using SteamKit2;

namespace DepotCommon.Steam;

public static class DepotManifestExtensions {
	private static readonly char AltDirChar = Path.DirectorySeparatorChar == '\\' ? '/' : '\\';

	public static bool DecryptFilenamesFixed(this DepotManifest manifest, byte[] encryptionKey) {
		if (!manifest.FilenamesEncrypted) {
			return true;
		}

		DebugLog.Assert(manifest.Files != null, nameof(DepotManifest), "Files was null when attempting to decrypt filenames.");
		DebugLog.Assert(encryptionKey.Length == 32, nameof(DepotManifest), "Decrypt filnames used with non 32 byte key!");

		// This was copypasted from <see cref="CryptoHelper.SymmetricDecrypt"/> to avoid allocating Aes instance for every filename
		using var aes = Aes.Create();
		aes.BlockSize = 128;
		aes.KeySize = 256;
		aes.Key = encryptionKey;

		var bufferDecoded = ArrayPool<byte>.Shared.Rent(256);
		var bufferDecrypted = ArrayPool<byte>.Shared.Rent(256);

		try {
			foreach (var file in manifest.Files) {
				if (!TryDecryptName(file.FileName, out var name)) {
					return false;
				}

				file.FileName = name;

				if (!string.IsNullOrEmpty(file.LinkTarget)) {
					if (!TryDecryptName(file.LinkTarget, out var linkName)) {
						return false;
					}

					file.LinkTarget = linkName;
				}
			}
		} finally {
			ArrayPool<byte>.Shared.Return(bufferDecoded);
			ArrayPool<byte>.Shared.Return(bufferDecrypted);
		}

		// Sort file entries alphabetically because that's what Steam does
		// TODO: Doesn't match Steam sorting if there are non-ASCII names present
		manifest.Files.Sort((f1, f2) => StringComparer.OrdinalIgnoreCase.Compare(f1.FileName, f2.FileName));

		manifest.FilenamesEncrypted = false;
		return true;

		bool TryDecryptName(string name, [MaybeNullWhen(false)] out string decoded) {
			decoded = null;

			var decodedLength = name.Length / 4 * 3; // This may be higher due to padding

			// Majority of filenames are short, even when they are encrypted and base64 encoded,
			// so this resize will be hit *very* rarely
			if (decodedLength > bufferDecoded.Length) {
				ArrayPool<byte>.Shared.Return(bufferDecoded);
				bufferDecoded = ArrayPool<byte>.Shared.Rent(decodedLength);

				ArrayPool<byte>.Shared.Return(bufferDecrypted);
				bufferDecrypted = ArrayPool<byte>.Shared.Rent(decodedLength);
			}

			if (!Convert.TryFromBase64Chars(name, bufferDecoded, out decodedLength)) {
				DebugLog.Assert(false, nameof(DepotManifest), "Failed to base64 decode the filename.");
				return false;
			}

			Span<byte> iv = stackalloc byte[16];
			int filenameLength;
			try {
				var encryptedFilename = bufferDecoded.AsSpan()[..decodedLength];
				aes.DecryptEcb(encryptedFilename[..iv.Length], iv, PaddingMode.None);
				filenameLength = aes.DecryptCbc(encryptedFilename[iv.Length..], iv, bufferDecrypted, PaddingMode.PKCS7);
			} catch (Exception) {
				DebugLog.Assert(false, nameof(DepotManifest), "Failed to decrypt the filename.");
				return false;
			}

			// Trim the ending null byte, safe for UTF-8
			if (filenameLength > 0 && bufferDecrypted[filenameLength] == 0) {
				filenameLength--;
			}

			// ASCII is subset of UTF-8, so it safe to replace the raw bytes here
			bufferDecrypted.AsSpan().Replace((byte) AltDirChar, (byte) Path.DirectorySeparatorChar);

			decoded = Encoding.UTF8.GetString(bufferDecrypted, 0, filenameLength);
			return true;
		}
	}
}
