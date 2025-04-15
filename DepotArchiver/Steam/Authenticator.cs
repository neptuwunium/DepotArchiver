// SPDX-FileCopyrightText: 2025 Legiayayana
//
// SPDX-License-Identifier: GPL-2.0-or-later

using Serilog;
using SteamKit2.Authentication;

namespace DepotArchiver.Steam;

internal class Authenticator : IAuthenticator {
	public Task<string> GetDeviceCodeAsync(bool previousCodeWasIncorrect) {
		if (previousCodeWasIncorrect) {
			Log.Error("The previous 2-factor auth code you have provided is incorrect");
		}

		Log.Warning("Please enter your 2-factor auth code from your authenticator app");
		while (Console.ReadLine() is { } code) {
			if (!string.IsNullOrWhiteSpace(code)) {
				return Task.FromResult(code);
			}
		}

		return Task.FromResult(string.Empty);
	}

	public Task<string> GetEmailCodeAsync(string email, bool previousCodeWasIncorrect) {
		if (previousCodeWasIncorrect) {
			Log.Error("The previous 2-factor auth code you have provided is incorrect");
		}

		Log.Warning("Please enter the auth code sent to the email for the account");
		while (Console.ReadLine() is { } code) {
			if (!string.IsNullOrWhiteSpace(code)) {
				return Task.FromResult(code);
			}
		}

		return Task.FromResult(string.Empty);
	}

	public Task<bool> AcceptDeviceConfirmationAsync() {
		Log.Warning("Use the Steam Mobile App to confirm your sign in");

		return Task.FromResult(true);
	}
}
