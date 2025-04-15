// SPDX-FileCopyrightText: 2025 Legiayayana
//
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Globalization;
using DragonLib.CommandLine;

namespace DepotUnarchiver;

internal record ProgramFlags : CommandLineFlags {
	public static ProgramFlags Instance { get; set; } = CommandLineFlagsParser.ParseFlags<ProgramFlags>();

	[Flag("output", Help = "the directory to save files in")]
	public string TargetDirectory { get; set; } = "game";

	[Flag("depots", Help = "the directory chunks are saved in")]
	public string DepotDirectory { get; set; } = "depots";

	[Flag("depot-id", Positional = 0, Help = "the depot id to unarchive", Extra = NumberStyles.Integer, IsRequired = true)]
	public uint DepotId { get; set; }

	[Flag("manifest-id", Positional = 1, Help = "the manifest id to unarchive", Extra = NumberStyles.Integer, IsRequired = true)]
	public ulong ManifestId { get; set; }

	[Flag("threads", Help = "number of threads to spawn")]
	public int Threads { get; set; } = Environment.ProcessorCount;
}
