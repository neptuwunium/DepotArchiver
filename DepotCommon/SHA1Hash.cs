// SPDX-FileCopyrightText: 2025 Legiayayana
//
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DepotCommon;

[StructLayout(LayoutKind.Sequential, Pack = 1)] [InlineArray(20)]
[JsonConverter(typeof(SHA1HashConverter))]
public struct SHA1Hash : IEquatable<SHA1Hash>, IComparable<SHA1Hash> {
	public byte Value;

	public int CompareTo(SHA1Hash other) => ((Span<byte>) this).SequenceCompareTo(other);
	public bool Equals(SHA1Hash other) => ((Span<byte>) this).SequenceEqual(other);

	public override bool Equals(object? obj) => obj is SHA1Hash other && Equals(other);

	public override int GetHashCode() {
		var hashCode = new HashCode();
		hashCode.AddBytes(this);
		return hashCode.ToHashCode();
	}

	public override string ToString() => Convert.ToHexString(this).ToLowerInvariant();

	public static bool operator ==(SHA1Hash left, SHA1Hash right) {
		return left.Equals(right);
	}

	public static bool operator !=(SHA1Hash left, SHA1Hash right) {
		return !(left == right);
	}
}

public class SHA1HashConverter : JsonConverter<SHA1Hash> {
	public override SHA1Hash Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) {
		var hash = reader.GetString();
		return hash == null || hash.Length < Unsafe.SizeOf<SHA1Hash>() << 1 ? default : MemoryMarshal.Read<SHA1Hash>(Convert.FromHexString(hash));
	}

	public override SHA1Hash ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => Read(ref reader, typeToConvert, options);
	public override void Write(Utf8JsonWriter writer, SHA1Hash value, JsonSerializerOptions options) => writer.WriteStringValue(value.ToString());
	public override void WriteAsPropertyName(Utf8JsonWriter writer, SHA1Hash value, JsonSerializerOptions options) => writer.WritePropertyName(value.ToString());
}
