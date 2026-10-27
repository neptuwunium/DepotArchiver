// SPDX-FileCopyrightText: 2025 Legiayayana
//
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text.RegularExpressions;
using DepotCommon;
using Pluto.CommandLine;

namespace DepotACF;

internal partial record ProgramFlags : CommandLineFlags, IUnarchiveOptions {
	[Flag("depots", 0, Help = "the directory chunks are saved in", Required = false)]
	public string? DepotDirectory { get; set; }

	[Flag("output", 1, Help = "the directory to install to", Required = false)]
	public string? TargetDirectory { get; set; }

	[Flag("threads", Help = "number of threads to spawn")]
	public int Threads { get; set; } = Environment.ProcessorCount;

	[Flag("no-clobber", "n", Help = "do not overwrite files that already exist")]
	public bool NoClobber { get; set; }

	[Flag("time", "t", Help = "update file time to the manifest time")]
	public bool Time { get; set; }

	[Flag("validate", Help = "validate files after extraction")]
	public bool Validate { get; set; }

	public bool AppendManifest => false;
	public List<Regex> Filter { get; } = [];
	public bool List => false;
}
