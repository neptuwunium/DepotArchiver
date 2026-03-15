// SPDX-FileCopyrightText: 2025 Legiayayana
//
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text.RegularExpressions;

namespace DepotCommon;

public interface IUnarchiveOptions {
	bool AppendManifest { get; }
	bool Validate { get; }
	bool Time { get; }
	List<Regex> Filter { get; }
	int Threads { get; }
	bool NoClobber { get; }
	bool List { get; }
}
