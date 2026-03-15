// SPDX-FileCopyrightText: 2025 Legiayayana
//
// SPDX-License-Identifier: GPL-2.0-or-later

using SteamKit2;

namespace DepotACF;

internal record AppInfo(uint Id, string Name, string InstallDir, KeyValue KeyValue) {
	private string Text { get; } = $"{Name} ({Id})";
	public override string ToString() => Text;
}
