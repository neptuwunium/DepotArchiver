// SPDX-FileCopyrightText: 2025 Legiayayana
//
// SPDX-License-Identifier: GPL-2.0-or-later

// Largely derived from DepotDownloader

using System.Collections.Concurrent;
using System.Net.Http.Headers;
using Serilog;
using SteamKit2;
using SteamKit2.Authentication;
using SteamKit2.CDN;

namespace DepotArchiver.Steam;

internal sealed class SteamSession : IDisposable {
	internal SteamSession(SteamUser.LogOnDetails details) {
		Details = details;
		GotLicensesTCS = new TaskCompletionSource();

		var config = SteamConfiguration.Create(c => c.WithHttpClientFactory(() => {
			var client = new HttpClient();
			client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("DepotArchiver", "1.0.0"));
			return client;
		}));

		Client = new SteamClient(config);
		User = Client.GetHandler<SteamUser>() ?? throw new InvalidOperationException();
		Content = Client.GetHandler<SteamContent>() ?? throw new InvalidOperationException();
		Apps = Client.GetHandler<SteamApps>() ?? throw new InvalidOperationException();
		Callbacks = new CallbackManager(Client);
		Connections = new ConnectionPool(new Client(Client), Content);

		Callbacks.Subscribe<SteamClient.ConnectedCallback>(ConnectedCallback);
		Callbacks.Subscribe<SteamClient.DisconnectedCallback>(DisconnectedCallback);
		Callbacks.Subscribe<SteamUser.LoggedOnCallback>(LogOnCallback);
		Callbacks.Subscribe<SteamApps.LicenseListCallback>(LicenseListCallback);
	}

	internal Dictionary<uint, ulong> AppTokens { get; } = [];
	internal Dictionary<uint, ulong> PackageTokens { get; } = [];
	internal Dictionary<uint, byte[]> DepotKeys { get; } = [];
	internal ConcurrentDictionary<(uint, string), TaskCompletionSource<SteamContent.CDNAuthToken?>> AuthTokens { get; } = [];
	internal List<SteamApps.LicenseListCallback.License> Licenses { get; private set; } = [];

	internal SteamClient Client { get; set; }
	internal SteamUser User { get; set; }
	internal SteamContent Content { get; set; }
	internal SteamApps Apps { get; set; }
	internal CallbackManager Callbacks { get; set; }
	internal ConnectionPool Connections { get; set; }
	internal SteamUser.LogOnDetails Details { get; }

	internal bool IsLoggedOn { get; private set; }
	internal uint CellId { get; private set; }

	private bool Connecting { get; set; }
	private bool Aborted { get; set; }
	private bool ExpectingDisconnectRemote { get; set; }
	private bool DidDisconnect { get; set; }
	private bool IsConnectionRecovery { get; set; }
	private int ConnectionBackoff { get; set; }
	private AuthSession? AuthSession { get; set; }
	private TaskCompletionSource GotLicensesTCS { get; set; }
	public Task FullyLoggedInTask => GotLicensesTCS.Task;

	public void Dispose() {
		GotLicensesTCS.SetCanceled();
		Connections.Dispose();
		Disconnect();
	}

	public async Task<byte[]?> RequestDepotKey(uint depotId, uint appid = 0) {
		if (Aborted) {
			return null;
		}

		if (DepotKeys.TryGetValue(depotId, out var key)) {
			return key;
		}

		var resp = await Apps.GetDepotDecryptionKey(depotId, appid);

		Log.Information("Got depot key for {Depot} ({Result})", resp.DepotID, resp.Result);

		if (resp.Result != EResult.OK) {
			return null;
		}

		return DepotKeys[resp.DepotID] = resp.DepotKey;
	}

	public async Task<ulong> GetDepotManifestRequestCodeAsync(uint appId, uint depotId, ulong manifestId, string branch) {
		if (Aborted) {
			return 0;
		}

		var requestCode = await Content.GetManifestRequestCode(depotId, appId, manifestId, branch);

		if (requestCode == 0) {
			Log.Error("No manifest code was returned for depot {DepotId}", depotId);
		} else {
			Log.Debug("Got manifest request code for depot {DepotId}", depotId);
		}

		return requestCode;
	}

	public async Task<SteamContent.CDNAuthToken?> RequestAuthToken(uint appId, uint depotId, Server server) {
		if (server.Host == null || Aborted) {
			return null;
		}

		var cdnKey = (depotId, server.Host);

		SteamContent.CDNAuthToken? result;

		if (AuthTokens.TryGetValue(cdnKey, out var completion)) {
			result = await completion.Task;
			if (result == null || result.Expiration >= DateTime.Now) {
				AuthTokens.TryRemove(cdnKey, out _);
			}
		}

		completion = new TaskCompletionSource<SteamContent.CDNAuthToken?>();

		if (!AuthTokens.TryAdd(cdnKey, completion)) {
			// race condition?
			if (AuthTokens.TryGetValue(cdnKey, out completion)) {
				return await completion.Task;
			}

			return null;
		}

		Log.Debug("Requesting auth token for {DepotId}@{Host}", depotId, server.Host);

		var cdnAuth = await Content.GetCDNAuthToken(appId, depotId, server.Host);

		Log.Debug("Got CDN auth token for {Host} ({Result}, expires {Expiration})", server.Host, cdnAuth.Result, cdnAuth.Expiration);

		result = cdnAuth.Result != EResult.OK ? null : cdnAuth;
		completion.SetResult(result);
		return result;
	}

	private void ResetConnectionFlags() {
		ExpectingDisconnectRemote = false;
		DidDisconnect = false;
		IsConnectionRecovery = false;
	}

	public void Connect() {
		Aborted = false;
		Connecting = true;
		ConnectionBackoff = 0;
		AuthSession = null;

		ResetConnectionFlags();
		Client.Connect();
	}

	public void Abort(bool sendLogOff = true) => Disconnect(sendLogOff);

	public void Disconnect(bool sendLogOff = true) {
		if (sendLogOff) {
			User.LogOff();
		}

		Aborted = true;
		Connecting = false;
		IsConnectionRecovery = false;
		Client.Disconnect();

		while (!DidDisconnect) {
			Callbacks.RunWaitAllCallbacks(TimeSpan.FromMilliseconds(100));
		}
	}

	public void Reconnect() {
		IsConnectionRecovery = true;
		Client.Disconnect();
	}

	private async void ConnectedCallback(SteamClient.ConnectedCallback connected) {
		try {
			Log.Information("Connected...");

			Connecting = false;
			ConnectionBackoff = 0;

			if (Details.Username == null) {
				Log.Information("Logging anonymously into Steam3...");
				User.LogOnAnonymous();
			} else {
				Log.Information("Logging '{Username}' into Steam3...", Details.Username);

				if (AuthSession is null) {
					AuthSession = await Client.Authentication.BeginAuthSessionViaCredentialsAsync(new AuthSessionDetails {
						Username = Details.Username,
						Password = Details.Password,
						IsPersistentSession = Flags.Instance.RememberPassword,
						GuardData = ConfigStore.Instance.GuardData.GetValueOrDefault(Details.Username),
						Authenticator = new UserConsoleAuthenticator(),
					});
				} else {
					var result = await AuthSession.PollingWaitForResultAsync();

					Details.Username = result.AccountName;
					Details.Password = null;
					Details.AccessToken = result.RefreshToken;

					if (result.NewGuardData != null) {
						ConfigStore.Instance.GuardData[result.AccountName] = result.NewGuardData;
					} else {
						ConfigStore.Instance.GuardData.Remove(result.AccountName);
					}

					ConfigStore.Instance.LoginTokens[result.AccountName] = result.RefreshToken;
					ConfigStore.Instance.Save();

					AuthSession = null;
				}

				User.LogOn(Details);
			}
		} catch (TaskCanceledException) {
			// nothing
		} catch (Exception ex) {
			Log.Error(ex, "Failed to authenticate with Steam");
			Abort(false);
		}
	}

	private void DisconnectedCallback(SteamClient.DisconnectedCallback disconnected) {
		DidDisconnect = true;

		Log.Debug("Disconnected: IsConnectionRecovery = {IsConnectionRecovery}, UserInitiated = {UserInitiated}, ExpectingDisconnectRemote = {ExpectingDisconnectRemote}", IsConnectionRecovery, disconnected.UserInitiated, ExpectingDisconnectRemote);

		if (!IsConnectionRecovery && (disconnected.UserInitiated || ExpectingDisconnectRemote)) {
			Log.Information("Disconnected from Steam");
			Aborted = true;
		} else if (ConnectionBackoff >= 10) {
			Log.Information("Could not connect to Steam after 10 tries");
			Abort(false);
		} else if (!Aborted) {
			ConnectionBackoff += 1;

			if (Connecting) {
				Log.Information("Connection to Steam failed. Trying again (#{ConnectionBackoff})...", ConnectionBackoff);
			} else {
				Log.Information("Lost connection to Steam. Reconnecting");
			}

			Thread.Sleep(1000 * ConnectionBackoff);
			ResetConnectionFlags();
			Client.Connect();
		}
	}

	private async void LogOnCallback(SteamUser.LoggedOnCallback loggedOn) {
		try {
			var isSteamGuard = loggedOn.Result == EResult.AccountLogonDenied;
			var isTOTP = loggedOn.Result == EResult.AccountLoginDeniedNeedTwoFactor;
			var isAccessToken = Flags.Instance.RememberPassword && Details.AccessToken != null &&
				loggedOn.Result is EResult.InvalidPassword
					or EResult.InvalidSignature
					or EResult.AccessDenied
					or EResult.Expired
					or EResult.Revoked;

			if (isSteamGuard || isTOTP || isAccessToken) {
				ExpectingDisconnectRemote = true;
				Abort(false);

				if (!isAccessToken) {
					Log.Information("This account is protected by Steam Guard.");
				}

				if (isTOTP) {
					do {
						Log.Information("Please enter your 2 factor auth code from your authenticator app: ");
						Details.TwoFactorCode = Console.ReadLine();
					} while (string.IsNullOrEmpty(Details.TwoFactorCode));
				} else if (isAccessToken) {
					if (!string.IsNullOrEmpty(Details.Username)) {
						ConfigStore.Instance.LoginTokens.Remove(Details.Username);
						ConfigStore.Instance.Save();
					}

					Log.Information($"Access token was rejected ({loggedOn.Result}).");
					Abort(false);
					return;
				} else {
					do {
						Log.Information("Please enter the authentication code sent to your email address: ");
						Details.AuthCode = Console.ReadLine();
					} while (string.IsNullOrEmpty(Details.AuthCode));
				}

				Log.Information("Retrying Steam3 connection...");
				Connect();
				return;
			}

			// ReSharper disable once SwitchStatementMissingSomeEnumCasesNoDefault
			switch (loggedOn.Result) {
				case EResult.TryAnotherCM:
					Log.Information("Retrying Steam3 connection (TryAnotherCM)...");
					Reconnect();
					return;
				case EResult.ServiceUnavailable:
					Log.Information("Unable to login to Steam3: {Result}", loggedOn.Result);
					Abort(false);
					return;
			}

			if (loggedOn.Result != EResult.OK) {
				Log.Information("Unable to login to Steam3: {Result}", loggedOn.Result);
				Abort();
				return;
			}

			Log.Information("Logged in...");

			await Connections.UpdateServerList(loggedOn.CellID);

			IsLoggedOn = true;
			CellId = loggedOn.CellID;
		} catch (Exception ex) {
			Log.Error(ex, "Failed to authenticate with Steam");
			Abort(false);
		}
	}

	private void LicenseListCallback(SteamApps.LicenseListCallback licenseList) {
		if (licenseList.Result != EResult.OK) {
			Log.Error("Unable to get license list: {Result} ", licenseList.Result);
			Abort();

			return;
		}

		Log.Information("Got {Count} licenses for account!", licenseList.LicenseList.Count);
		Licenses.Clear();
		Licenses.AddRange(licenseList.LicenseList);

		foreach (var license in licenseList.LicenseList) {
			if (license.AccessToken > 0) {
				PackageTokens.TryAdd(license.PackageID, license.AccessToken);
			}
		}

		GotLicensesTCS.SetResult();
		GotLicensesTCS = new TaskCompletionSource();
	}
}
