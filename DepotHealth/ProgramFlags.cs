// SPDX-FileCopyrightText: 2025 Legiayayana
//
// SPDX-License-Identifier: GPL-2.0-or-later

using DragonLib.CommandLine;

namespace DepotHealth;

internal record ProgramFlags : CommandLineFlags {
	public static ProgramFlags Instance { get; set; } = CommandLineFlagsParser.ParseFlags<ProgramFlags>();

	[Flag("depots", Help = "the directory chunks are saved in")]
	public string DepotDirectory { get; set; } = "depots";

	[Flag("meta", Help = "display metadata from manifests (note this will sort manifests by date and may use more memory)")]
	public bool Meta { get; set; }

	[Flag("list", Help = "list files from manifests")]
	public bool List { get; set; }

	[Flag("only-info", Help = "only display metadata, calculate size and manifest files, don't verify chunks")]
	public bool OnlyInfo { get; set; }

	[Flag("only-size", Help = "only display metadata and calculate size, don't verify chunks")]
	public bool OnlySize { get; set; }

	[Flag("repair", Help = "attempt to repair chunks by redownloading them")]
	public bool Repair { get; set; }

	[Flag("threads", Help = "number of threads to spawn")]
	public int Threads { get; set; } = Environment.ProcessorCount;

	[Flag("json", Help = "output json data to stderr")]
	public bool OutputJson { get; set; }

	[Flag("quiet", Help = "do not output to stdout")]
	public bool Quiet { get; set; }
}
