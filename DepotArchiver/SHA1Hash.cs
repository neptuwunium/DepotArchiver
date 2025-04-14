// SPDX-FileCopyrightText: 2025 Legiayayana
//
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace DepotArchiver;

[StructLayout(LayoutKind.Sequential, Pack = 1), InlineArray(20)]
internal struct SHA1Hash : IEquatable<SHA1Hash> {
	public byte Value;

	public bool Equals(SHA1Hash other) => ((Span<byte>) this).SequenceEqual(other);

	public override bool Equals(object? obj) => obj is SHA1Hash other && Equals(other);

	public override int GetHashCode() {
		var hashCode = new HashCode();
		hashCode.AddBytes(this);
		return hashCode.ToHashCode();
	}
}
