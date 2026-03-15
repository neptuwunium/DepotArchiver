// SPDX-FileCopyrightText: 2025 Legiayayana
//
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text;
using Spectre.Console;

namespace DepotACF;

public static class PathCompletionPrompt {
	public static string? Display(string title, string message, bool validate) {
		Program.Term.MarkupLine($"[grey]{message}[/]");
		Program.Term.Markup($"[cyan]{title}:[/] ");

		var buffer = new StringBuilder();
		string? lastBuffer = default;
		var lastIndex = -1;

		while (true) {
			var key = Console.ReadKey(true);

			// ReSharper disable once SwitchStatementHandlesSomeKnownEnumValuesWithDefault
			switch (key.Key) {
				case ConsoleKey.Enter: {
					Console.WriteLine();

					var text = buffer.ToString();
					if (!validate || Directory.Exists(text)) {
						return text;
					}

					Program.Term.MarkupLine("[red]Invalid path, directory not found.[/]");
					Program.Term.Markup($"[cyan]{title}:[/] ");
					buffer.Clear();
					break;
				}
				case ConsoleKey.Backspace when buffer.Length > 0:
					buffer = buffer.Remove(buffer.Length - 1, 1);
					Console.Write("\b \b");
					break;
				case ConsoleKey.Tab: {
					var text = buffer.ToString();
					var (completion, index) = AutoComplete(lastBuffer ?? text, lastIndex);

					Clear(buffer.Length);
					lastBuffer ??= text;
					lastIndex = index;
					buffer.Clear();
					buffer.Append(completion);
					Console.Write(buffer);
					break;
				}
				case ConsoleKey.Escape:
					Console.WriteLine();
					return default;
				default: {
					if (!char.IsControl(key.KeyChar)) {
						lastBuffer = null;
						buffer.Append(key.KeyChar);
						Console.Write(key.KeyChar);
					}

					break;
				}
			}
		}
	}

	private static void Clear(int length) {
		var backspace = new string('\b', length);
		Console.Write(backspace + new string(' ', length) + backspace);
	}

	private static (string, int) AutoComplete(string input, int index) {
		var dir = Path.GetDirectoryName(input);
		var file = Path.GetFileName(input);
		dir = string.IsNullOrEmpty(dir) ? Directory.GetCurrentDirectory() : dir;

		if (!Directory.Exists(dir)) {
			return (input, -1);
		}

		var matches = Directory.GetDirectories(dir, file + "*", SearchOption.TopDirectoryOnly);
		if (matches.Length == 0) {
			return (input, -1);
		}

		index = (index + 1) % matches.Length;
		return (matches.Order().ElementAt(index), index);
	}
}
