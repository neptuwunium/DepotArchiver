// SPDX-FileCopyrightText: 2025 Legiayayana
//
// SPDX-License-Identifier: GPL-2.0-or-later

using System.IO.Compression;
using System.IO.IsolatedStorage;
using ProtoBuf;
using Serilog;

namespace DepotCommon.Steam;

[ProtoContract]
public class ConfigStore {
	[ProtoMember(100, IsRequired = false)]
	public Dictionary<string, string> LoginTokens { get; private set; } = new(StringComparer.OrdinalIgnoreCase);

	[ProtoMember(101, IsRequired = false)]
	public Dictionary<string, string> GuardData { get; private set; } = new(StringComparer.OrdinalIgnoreCase);

	private static Lazy<IsolatedStorageFile> IsolatedStorage { get; } = new(IsolatedStorageFile.GetUserStoreForAssembly);

	private static bool WriteLocal { get; } = Environment.GetEnvironmentVariable("DEPOTARCHIVER_USE_ISOLATED_STORAGE") == null;
	private static string ConfigName { get; } = "archiver.config";
	private static string ConfigDir { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DepotArchiver");
	private static string LocalPath { get; } = Path.Combine(ConfigDir, ConfigName);

	public static ConfigStore Instance {
		get {
			if (field != null) {
				return field;
			}

			if (WriteLocal && File.Exists(LocalPath)) {
				try {
					using var fs = new FileStream(LocalPath, FileMode.Open, FileAccess.Read);
					field = LoadInner(fs);
				} catch (Exception ex) {
					Log.Error(ex, "Failed to load config store");
				}
			} else if (IsolatedStorage.Value.FileExists(ConfigName)) {
				try {
					using var fs = IsolatedStorage.Value.OpenFile(ConfigName, FileMode.Open, FileAccess.Read);
					field = LoadInner(fs);

					if (WriteLocal) {
						field.Save(); // resave to local storage.
					}
				} catch (Exception ex) {
					Log.Error(ex, "Failed to load config store");
				}
			}

			field ??= new ConfigStore();

			return field;
		}
	}

	public void Save() {
		try {
			if (WriteLocal) {
				if (!Directory.Exists(ConfigDir)) {
					Directory.CreateDirectory(ConfigDir);
				}

				using var fs = new FileStream(LocalPath, FileMode.Create, FileAccess.ReadWrite);
				SaveInner(fs);
			} else {
				using var fs = IsolatedStorage.Value.OpenFile(ConfigName, FileMode.Create, FileAccess.ReadWrite);
				SaveInner(fs);
			}
		} catch (Exception ex) {
			Log.Error(ex, "Failed to save config store");
		}
	}

	public static ConfigStore LoadInner(Stream fs) {
		using var ds = new DeflateStream(fs, CompressionMode.Decompress);
		return Serializer.Deserialize<ConfigStore>(ds);
	}

	public void SaveInner(Stream fs) {
		fs.SetLength(0);
		using var ds = new DeflateStream(fs, CompressionMode.Compress);
		Serializer.Serialize(ds, this);
	}
}
