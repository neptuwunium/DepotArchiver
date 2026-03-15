// SPDX-FileCopyrightText: 2025 Legiayayana
//
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text.RegularExpressions;
using DepotCommon;
using DragonLib.CommandLine;

namespace DepotACF;

internal record ProgramFlags : CommandLineFlags, IUnarchiveOptions {
	public static ProgramFlags Instance { get; set; } = CommandLineFlagsParser.ParseFlags<ProgramFlags>();

	[Flag("depots", Positional = 0, Help = "the directory chunks are saved in", IsRequired = false)]
	public string? DepotDirectory { get; set; }

	[Flag("output", Positional = 1, Help = "the directory to install to", IsRequired = false)]
	public string? TargetDirectory { get; set; }

	[Flag("threads", Help = "number of threads to spawn")]
	public int Threads { get; set; } = Environment.ProcessorCount;

	[Flag("no-clobber", Aliases = ["n"], Help = "do not overwrite files that already exist")]
	public bool NoClobber { get; set; }

	[Flag("time", Aliases = ["t"], Help = "update file time to the manifest time")]
	public bool Time { get; set; }

	[Flag("validate", Help = "validate files after extraction")]
	public bool Validate { get; set; }

	public bool AppendManifest => false;
	public List<Regex> Filter { get; } = [];
	public bool List => false;
}
