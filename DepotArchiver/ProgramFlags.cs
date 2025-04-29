// SPDX-FileCopyrightText: 2025 Legiayayana
//
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Globalization;
using System.Reflection;
using DragonLib.CommandLine;

namespace DepotArchiver;

internal record ProgramFlags : CommandLineFlags {
	public static ProgramFlags Instance { get; set; } = CommandLineFlagsParser.ParseFlags<ProgramFlags>(CommandLineOptions.Default with { HelpDelegate = PrintHelp });

	private static void PrintHelp(Dictionary<PropertyInfo, (FlagAttribute Flag, Type FlagType)> flags, object instance, CommandLineOptions options, bool helpInvoked) {
		CommandLineFlagsParser.PrintHelp(flags, instance, options, helpInvoked);
		Console.WriteLine("CSV Format:");
		Console.WriteLine("\tLines starting with # are ignored");
		Console.WriteLine("\tappId,depotId,manifestId,branch");
		Console.WriteLine("\texample: 440,440,7561350075549843378,public");
		Console.WriteLine("If the depot id is exactly \"password\" then the syntax changes a bit:");
		Console.WriteLine("\tappId,password,branchPassword");
		Console.WriteLine("\texample: 440,password,super-secret-password");
	}

	[Flag("remember-password", Help = "remember password when logging in", Env = "DEPOTARCHIVER_REMEMBER_PASSWORD")]
	public bool RememberPassword { get; set; }

	[Flag("username", Help = "the username of the account to login to", Env = "DEPOTARCHIVER_USERNAME")]
	public string? Username { get; set; }

	[Flag("password", Help = "the password of the account to login to", Env = "DEPOTARCHIVER_PASSWORD")]
	public string? Password { get; set; }

	[Flag("token", Help = "the login token for the account", Env = "DEPOTARCHIVER_TOKEN")]
	public string? Token { get; set; }

	[Flag("plan", Positional = 0, Help = "path to csv plan file, this can also be an app id.", IsRequired = true)]
	public string ArchivePlanFile { get; set; } = null!;

	[Flag("depots", Help = "the directory to save chunks in")]
	public string TargetDirectory { get; set; } = "depots";

	[Flag("branch", Help = "the branch to download if no manifest ids are specified")]
	public List<string> Branches { get; set; } = [];

	[Flag("validate", Help = "validate all chunks for corruption")]
	public bool Validate { get; set; }

	[Flag("validate-new", Help = "validate new chunks for corruption")]
	public bool ValidateNew { get; set; }

	[Flag("no-app-info", Help = "do not fetch app info")]
	public bool NoAppInfo { get; set; }

	[Flag("no-depot-keys", Help = "do not fetch depot keys")]
	public bool NoDepotKeys { get; set; }

	[Flag("no-manifests", Help = "do not fetch manifests")]
	public bool NoManifests { get; set; }

	[Flag("no-chunks", Help = "do not fetch chunks")]
	public bool NoChunks { get; set; }

	[Flag("only-validate", Help = "only validate chunks, do not download")]
	public bool OnlyValidate { get; set; }

	[Flag("threads", Help = "number of download threads to spawn")]
	public int Threads { get; set; } = Environment.ProcessorCount;

	[Flag("login-id", Extra = NumberStyles.Integer | NumberStyles.AllowHexSpecifier, Help = "the unique login id for login session tracking")]
	public uint? LoginId { get; set; }
}
