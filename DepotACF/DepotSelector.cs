// SPDX-FileCopyrightText: 2025 Legiayayana
//
// SPDX-License-Identifier: GPL-2.0-or-later

namespace DepotACF;

internal record DepotSelector(uint Id) {
	private string Text { get; } = Id == 0 ? "Install" : Id.ToString();
	public override string ToString() => Text;
}
