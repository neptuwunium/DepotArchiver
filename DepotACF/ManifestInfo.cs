// SPDX-FileCopyrightText: 2025 Legiayayana
//
// SPDX-License-Identifier: GPL-2.0-or-later

using SteamKit2;

namespace DepotACF;

internal record ManifestInfo(ulong Id, DateTimeOffset Date, DepotManifest Manifest) {
	private string Text { get; } = Id == 0 ? "Cancel" : $"{Date.ToString()} ({Id})";
	public override string ToString() => Text;
}
