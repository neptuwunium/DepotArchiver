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
	public ConfigStore() {
		LoginTokens = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		GuardData = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
	}

	[ProtoMember(100, IsRequired = false)]
	public Dictionary<string, string> LoginTokens { get; private set; }

	[ProtoMember(101, IsRequired = false)]
	public Dictionary<string, string> GuardData { get; private set; }

	private static IsolatedStorageFile IsolatedStorage { get; } = IsolatedStorageFile.GetUserStoreForAssembly();

	public static ConfigStore Instance {
		get {
			if ((ConfigStore?) field != null) {
				return field;
			}

			if (IsolatedStorage.FileExists("archiver.config")) {
				try {
					using var fs = IsolatedStorage.OpenFile("archiver.config", FileMode.Open, FileAccess.Read);
					using var ds = new DeflateStream(fs, CompressionMode.Decompress);
					field = Serializer.Deserialize<ConfigStore>(ds);
				} catch (Exception ex) {
					Log.Error(ex, "Failed to load config store");
					field = new ConfigStore();
				}
			} else {
				field = new ConfigStore();
			}

			return field;
		}
	} = null!;

	public void Save() {
		try {
			using var fs = IsolatedStorage.OpenFile("archiver.config", FileMode.Create, FileAccess.Write);
			using var ds = new DeflateStream(fs, CompressionMode.Compress);
			Serializer.Serialize(ds, this);
		} catch (Exception ex) {
			Log.Error(ex, "Failed to save config store");
		}
	}
}
