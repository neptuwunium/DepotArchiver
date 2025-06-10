// SPDX-FileCopyrightText: 2025 Legiayayana
//
// SPDX-License-Identifier: GPL-2.0-or-later

using DragonLib.CommandLine;

namespace DepotHealth;

internal record ProgramFlags : CommandLineFlags {
	public static ProgramFlags Instance { get; set; } = CommandLineFlagsParser.ParseFlags<ProgramFlags>();

	[Flag("depots", Help = "the directory chunks are saved in")]
	public string DepotDirectory { get; set; } = "depots";
}
