// SPDX-FileCopyrightText: 2025 Legiayayana
//
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using System.Globalization;
using DepotCommon;
using DragonLib;
using Serilog;
using Serilog.Events;
using SteamKit2;

namespace DepotUnarchiver;

internal static class Program {
	private static void Main() {
		Helpers.ResetCulture();
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
				Unarchive.ProcessManifest(flags, targetDirectory, manifestsPath, manifestId, depotKeyPath, depotPath);
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
}
