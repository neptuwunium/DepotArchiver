// SPDX-FileCopyrightText: 2025 Legiayayana
//
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using DragonLib.CommandLine;

namespace DepotUnarchiver;

internal record ProgramFlags : CommandLineFlags {
	public static ProgramFlags Instance { get; set; } = CommandLineFlagsParser.ParseFlags<ProgramFlags>();

	[Flag("output", Help = "the directory to save files in")]
	public string TargetDirectory { get; set; } = "game";

	[Flag("depots", Help = "the directory chunks are saved in")]
	public string DepotDirectory { get; set; } = "depots";

	[Flag("list", Help = "list files in the manifest")]
	public bool List { get; set; }

	[Flag("filter", Help = "only export files that match these regex filters", FileListPrefix = '@')]
	public List<Regex> Filter { get; set; } = [];

	[Flag("depot-id", Positional = 0, Help = "the depot id to unarchive", IsRequired = true)]
	public string DepotId { get; set; } = string.Empty;

	[Flag("manifest-id", Positional = 1, Help = "the manifest id to unarchive")]
	public HashSet<string> ManifestIds { get; set; } = [];

	[Flag("threads", Help = "number of threads to spawn")]
	public int Threads { get; set; } = Environment.ProcessorCount;

	[Flag("append-manifest-id", Help = "Append the manifest id to the file path")]
	public bool AppendManifest { get; set; }

	[Flag("append-depot-id", Help = "Append the depot id to the file path")]
	public bool AppendDepot { get; set; }

	[Flag("no-clobber", Aliases = ["n"], Help = "Do not overwrite files that already exist")]
	public bool NoClobber { get; set; }

	private static void PrintHelp(Dictionary<PropertyInfo, (FlagAttribute Flag, Type FlagType)> flags, object instance, CommandLineOptions options, bool helpInvoked) {
		CommandLineFlagsParser.PrintHelp(flags, instance, options, helpInvoked);
		Console.WriteLine("Id Format:");
		Console.WriteLine("\tBoth depot-id and manifest-id can be depotId:manifestId.");
		Console.WriteLine("\tThe first id argument will always be depotId.");
		Console.WriteLine("\tDepot ID can be an App ID, which will pull in all depots for the app if a vdf is cached.");
		Console.WriteLine("\tIf there is no :, every subsequent id will be considered a manifest id for the last used depot id.");
		Console.WriteLine("\texample: 440 7561350075549843378 441:6322590814356637464 2849445313493516261 220:0");
		Console.WriteLine("\twill process the following manifests:");
		Console.WriteLine("\tdepot 440, manifest 7561350075549843378");
		Console.WriteLine("\tdepot 441, manifest 6322590814356637464");
		Console.WriteLine("\tdepot 441, manifest 2849445313493516261");
		Console.WriteLine("\tdepot 233, manifest 1358845133936836982");
	}
}
