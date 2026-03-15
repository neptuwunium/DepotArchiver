// SPDX-FileCopyrightText: 2025 Legiayayana
//
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using DepotCommon;
using DragonLib;
using Spectre.Console;
using SteamKit2;

namespace DepotACF;

internal static class Program {
	private static bool TrueColor { get; } = Environment.GetEnvironmentVariable("COLORTERM")?.ToLower() is "truecolor" or "24bit";

	internal static IAnsiConsole Term { get; } = AnsiConsole.Create(new AnsiConsoleSettings {
		Ansi = TrueColor ? AnsiSupport.Yes : AnsiSupport.Detect,
		ColorSystem = TrueColor ? ColorSystemSupport.TrueColor : ColorSystemSupport.Detect,
		Out = new AnsiConsoleOutput(Console.Out),
	});

	private static void Main() {
		Helpers.ResetCulture();
		var flags = ProgramFlags.Instance;

		var depotsPath = flags.DepotDirectory ?? PathCompletionPrompt.Display("Select Depot Directory", "Where DepotArchiver saved chunks to", true);
		if (string.IsNullOrEmpty(depotsPath)) {
			return;
		}

		var appsPath = flags.TargetDirectory ?? PathCompletionPrompt.Display("Select Install Directory", "Where Steam Apps are installed to", false) ?? string.Empty;
		Directory.CreateDirectory(appsPath);

		var apps = LoadApps(depotsPath);
		if (apps.Count == 0) {
			ShowError("No apps found.");
		}

		var selectedApp = SelectApp(apps);
		var selectedManifests = SelectDepots(depotsPath, selectedApp);
		var (installedDepots, installScriptsKv) = UnarchiveApp(appsPath, selectedApp, selectedManifests, depotsPath);
		WriteACF(installedDepots, selectedApp, selectedManifests, installScriptsKv, appsPath);
	}

	private static void WriteACF(List<KeyValue> installedDepots, AppInfo selectedApp, Dictionary<uint, ManifestInfo> selectedManifests, KeyValue installScriptsKv, string appsPath) {
		var acf = new KeyValue("AppState");
		var installedDepotsKv = new KeyValue("InstalledDepots");
		installedDepotsKv.Children.AddRange(installedDepots);
		var sharedDepotsKv = new KeyValue("SharedDepots");
		foreach (var depot in selectedApp.KeyValue["depots"].Children) {
			if (depot["depotfromapp"].Value is { } sharedDepot &&
				depot["sharedinstall"].AsBoolean() &&
				uint.TryParse(depot.Name ?? "0", NumberStyles.Integer, null, out var depotId) &&
				depotId > 0 &&
				selectedManifests.ContainsKey(depotId)) {
				sharedDepotsKv.Children.Add(new KeyValue(depot.Name!, sharedDepot));
			}
		}

		acf.Children.AddRange([
			new KeyValue("appid", selectedApp.Id.ToString()),
			new KeyValue("universe", "1"),
			new KeyValue("StateFlags", "4"),
			new KeyValue("name", selectedApp.Name),
			new KeyValue("installdir", selectedApp.InstallDir),
			new KeyValue("SizeOnDisk", selectedManifests.Select(x => (long) x.Value.Manifest.TotalUncompressedSize).Sum(x => x).ToString(CultureInfo.InvariantCulture)),
			new KeyValue("DownloadType", "1"),
			installedDepotsKv,
		]);

		if (installScriptsKv.Children.Count > 0) {
			acf.Children.Add(installScriptsKv);
		}

		if (sharedDepotsKv.Children.Count > 0) {
			acf.Children.Add(sharedDepotsKv);
		}

		acf.SaveToFile(Path.Combine(appsPath, $"appmanifest_{selectedApp.Id}.acf"), false);
	}

	private static (List<KeyValue> installedDepots, KeyValue installScriptsKv) UnarchiveApp(string appsPath, AppInfo selectedApp, Dictionary<uint, ManifestInfo> selectedManifests, string depotsPath) =>
		Term.Progress()
			.Columns(
				new TaskDescriptionColumn(),
				new ProgressBarColumn(),
				new PercentageColumn(),
				new ElapsedTimeColumn(),
				new SpinnerColumn())
			.AutoRefresh(true)
			.AutoClear(true)
			.Start(ctx => {
				var appPath = Path.Combine(appsPath, "common", selectedApp.InstallDir);
				var installedDepots = new List<KeyValue>();
				var installScriptsKv = new KeyValue("InstallScripts");

				foreach (var (depotId, manifestInfo) in selectedManifests) {
					var task = ctx.AddTask($"[cyan]Writing {depotId}[/]");

					var manifestPath = Path.Combine(depotsPath, depotId.ToString(CultureInfo.InvariantCulture), "manifest");
					var keyPath = Path.Combine(depotsPath, depotId.ToString(CultureInfo.InvariantCulture) + ".depotkey");
					var depotPath = Path.Combine(depotsPath, depotId.ToString(CultureInfo.InvariantCulture));
					Unarchive.ProcessManifest(ProgramFlags.Instance, appPath, manifestPath, manifestInfo.Id, keyPath, depotPath);

					var depotInfo = new KeyValue(depotId.ToString(CultureInfo.InvariantCulture));
					depotInfo.Children.AddRange([
						new KeyValue("manifest", manifestInfo.Id.ToString(CultureInfo.InvariantCulture)),
						new KeyValue("size", manifestInfo.Manifest.TotalUncompressedSize.ToString(CultureInfo.InvariantCulture)),
					]);
					installedDepots.Add(depotInfo);

					var installScripts = new List<string>();
					foreach (var file in manifestInfo.Manifest.Files ?? []) {
						if ((file.Flags & EDepotFileFlag.InstallScript) == 0) {
							continue;
						}

						installScripts.Add(file.FileName);
					}

					if (installScripts.Count > 0) {
						if (installScripts.Count == 1) {
							installScriptsKv.Children.Add(new KeyValue(depotId.ToString(CultureInfo.InvariantCulture), installScripts[0]));
						} else {
							var depotInstallScripts = new KeyValue(depotId.ToString(CultureInfo.InvariantCulture));
							installScriptsKv.Children.Add(depotInstallScripts);
							for (var index = 0; index < installScripts.Count; index++) {
								depotInstallScripts.Children.Add(new KeyValue(index.ToString(CultureInfo.InvariantCulture), installScripts[index]));
							}
						}
					}

					ctx.RemoveTask(task);
				}

				return (installedDepots, installScriptsKv);
			});

	private static Dictionary<uint, ManifestInfo> SelectDepots(string depotsPath, AppInfo selectedApp) {
		Term.MarkupLine($"[cyan]Selected App:[/] {selectedApp}");
		var choices = LoadDepots(depotsPath, selectedApp.KeyValue);

		var selectionChoices = new List<DepotSelector> { new(0) };
		selectionChoices.AddRange(choices.Keys.Select(x => new DepotSelector(x)));

		var selected = choices.ToDictionary(x => x.Key, y => y.Value[1]);

		while (true) {
			Term.Clear();
			Term.MarkupLine($"[cyan]Selected App:[/] {selectedApp}");

			var table = new Table()
						.Border(TableBorder.Rounded)
						.AddColumns(
							new TableColumn("[cyan]Depot Id[/]").Centered(),
							new TableColumn("[lime]Manifest Id[/]").Centered()
						)
						.Expand();

			foreach (var depot in choices.Keys) {
				table.AddRow(depot.ToString(), selected[depot].ToString());
			}

			Term.Write(table);

			var selection = Term.Prompt(new SelectionPrompt<DepotSelector>()
										.Title("Select a [blue]Depot[/] to change its [lime]manifest[/]:")
										.AddChoices(selectionChoices));

			if (selection.Id == 0) {
				break;
			}

			var targetDepot = choices[selection.Id];

			var prompt = new SelectionPrompt<ManifestInfo>().Title("Select Manifest");
			prompt.AddChoices(targetDepot);
			var newManifest = Term.Prompt(prompt);
			if (newManifest.Id != 0) {
				selected[selection.Id] = newManifest;
			}
		}

		return selected;
	}

	private static Dictionary<uint, List<ManifestInfo>> LoadDepots(string depotsPath, KeyValue appInfo) =>
		Term.Progress()
			.Columns(
				new TaskDescriptionColumn(),
				new ProgressBarColumn(),
				new PercentageColumn(),
				new RemainingTimeColumn(),
				new SpinnerColumn())
			.AutoRefresh(true)
			.AutoClear(true)
			.Start(ctx => {
				var loadedManifests = new Dictionary<uint, List<ManifestInfo>>();
				var depotOuterTask = ctx.AddTask("[green]Loading Depot Info[/]", maxValue: appInfo["depots"].Children.Count);

				foreach (var depot in appInfo["depots"].Children) {
					var manifests = depot["manifests"];
					depotOuterTask.Increment(1);

					if (manifests.Children.Count == 0) {
						continue;
					}

					if (string.IsNullOrEmpty(depot.Name)) {
						continue;
					}

					var manifestsPath = Path.Combine(depotsPath, depot.Name, "manifest");
					var depotKeyPath = Path.Combine(depotsPath, depot.Name + ".depotkey");
					if (!Directory.Exists(manifestsPath) || !File.Exists(depotKeyPath)) {
						continue;
					}

					var depotKey = File.ReadAllBytes(depotKeyPath);
					if (depotKey.Length != 32) {
						continue;
					}

					var depotId = uint.Parse(depot.Name);
					var depotManifests = new List<ManifestInfo>();
					var manifestFiles = Directory.GetFiles(manifestsPath, "*", SearchOption.TopDirectoryOnly);
					var depotTask = ctx.AddTask($"[lime]Depot {depot.Name}[/]", maxValue: manifestFiles.Length);

					foreach (var manifestPath in manifestFiles) {
						depotTask.Increment(1);

						var manifest = DepotManifest.LoadFromFile(manifestPath);
						if (manifest == null || !manifest.DecryptFilenames(depotKey)) {
							continue;
						}

						depotManifests.Add(new ManifestInfo(manifest.ManifestGID, new DateTimeOffset(manifest.CreationTime), manifest));
					}

					depotManifests = loadedManifests[depotId] = depotManifests.OrderByDescending(x => x.Date).ToList();
					depotManifests.Insert(0, new ManifestInfo(0, DateTimeOffset.MinValue, null!));

					ctx.RemoveTask(depotTask);
				}

				return loadedManifests;
			});

	private static AppInfo SelectApp(List<AppInfo> apps) {
		var prompt = new SelectionPrompt<AppInfo>().Title("Select App");
		prompt.AddChoices(apps);
		return Term.Prompt(prompt);
	}

	private static List<AppInfo> LoadApps(string depotsPath) =>
		Term.Progress()
			.Columns(
				new TaskDescriptionColumn(),
				new ProgressBarColumn(),
				new PercentageColumn(),
				new RemainingTimeColumn(),
				new SpinnerColumn())
			.AutoRefresh(true)
			.AutoClear(true)
			.Start(ctx => {
				var entries = new List<AppInfo>();
				var vdfFiles = Directory.GetFiles(depotsPath, "*.vdf", SearchOption.TopDirectoryOnly);
				var loadTask = ctx.AddTask("[green]Loading App Info[/]", maxValue: vdfFiles.Length);

				foreach (var vdf in vdfFiles) {
					loadTask.Increment(1);
					var kv = KeyValue.LoadAsText(vdf) ?? KeyValue.Invalid;
					var type = kv["common"]["type"].AsString()?.ToLower();
					var appId = kv["appid"].AsUnsignedInteger();

					if (type is not "game" || appId == 0) {
						continue;
					}

					var name = kv["common"]["name"].AsString();

					if (string.IsNullOrEmpty(name)) {
						name = $"SteamApp{appId}";
					}

					var installDir = kv["config"]["installdir"].AsString() ?? name;

					entries.Add(new AppInfo(appId, name, installDir, kv));
				}

				return entries;
			});

	[DoesNotReturn]
	private static void ShowError(string message, int exitCode = 1) {
		Term.MarkupLine($"[bold red]Error:[/] {message}");
		Term.MarkupLine("Press any key to exit...");
		Console.ReadKey();
		Environment.Exit(exitCode);
	}
}
