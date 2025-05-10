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
		LoggedInTaskCompletionSource = new TaskCompletionSource();

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
	}

	internal Dictionary<uint, byte[]> DepotKeys { get; } = [];
	internal ConcurrentDictionary<(uint, string), TaskCompletionSource<SteamContent.CDNAuthToken?>> AuthTokens { get; } = [];
	internal List<SteamApps.LicenseListCallback.License> Licenses { get; } = [];

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
	private TaskCompletionSource LoggedInTaskCompletionSource { get; set; }
	public Task FullyLoggedInTask => LoggedInTaskCompletionSource.Task;

	public void Dispose() {
		if (Aborted) {
			return;
		}

		Disconnect();
	}

	public void TickCallbacks() {
		while (!Aborted) {
			Callbacks.RunWaitCallbacks(TimeSpan.FromMilliseconds(100));
		}
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
			Log.Error("No manifest code was returned for depot {DepotId} and manifest {ManifestId}", depotId, manifestId);
		} else {
			Log.Debug("Got manifest request code for depot {DepotId} and manifest {ManifestId}", depotId, manifestId);
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
		Connecting = true;
		ConnectionBackoff = 0;

		ResetConnectionFlags();
		Client.Connect();
	}

	public void Abort(bool sendLogOff = true) {
		ExpectingDisconnectRemote = true;
		Disconnect(sendLogOff);
	}

	public void Disconnect(bool sendLogOff = true) {
		if (sendLogOff) {
			User.LogOff();
		}

		Aborted = true;
		Connecting = false;
		IsConnectionRecovery = false;
		LoggedInTaskCompletionSource.SetCanceled();
		Client.Disconnect();
		Connections.Dispose();

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

				var authData = new AuthSessionDetails {
					DeviceFriendlyName = $"{Environment.MachineName} (Archival)",
					Username = Details.Username,
					Password = Details.Password,
					IsPersistentSession = ProgramFlags.Instance.RememberPassword,
					GuardData = ConfigStore.Instance.GuardData.GetValueOrDefault(Details.Username),
					Authenticator = new Authenticator(),
				};

				if (ProgramFlags.Instance.RememberPassword && ConfigStore.Instance.LoginTokens.TryGetValue(authData.Username, out var token)) {
					Details.AccessToken = token;
					Details.Password = null;
				}

				if (string.IsNullOrEmpty(Details.AccessToken)) {
					try {
						AuthSession = await Client.Authentication.BeginAuthSessionViaCredentialsAsync(authData);
						await RefreshSession();
					} catch (AuthenticationException ex) {
						Log.Error(ex, "Failed to authenticate with steam");
						Abort(false);
					}
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

	private async Task RefreshSession() {
		if (AuthSession == null) {
			return;
		}

		var result = await AuthSession.PollingWaitForResultAsync();

		Details.Username = result.AccountName;
		Details.Password = null;
		Details.AccessToken = result.RefreshToken;

		if (ProgramFlags.Instance.RememberPassword) {
			if (result.NewGuardData != null) {
				ConfigStore.Instance.GuardData[result.AccountName] = result.NewGuardData;
			} else {
				ConfigStore.Instance.GuardData.Remove(result.AccountName);
			}

			ConfigStore.Instance.LoginTokens[result.AccountName] = result.RefreshToken;
			ConfigStore.Instance.Save();
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
				if (!string.IsNullOrEmpty(Details.Username) && ProgramFlags.Instance.RememberPassword) {
					ConfigStore.Instance.LoginTokens.Remove(Details.Username);
					ConfigStore.Instance.Save();
				}

				Log.Information("Unable to login to Steam3: {Result}", loggedOn.Result);
				Abort();
				return;
			}

			Log.Information("Logged in...");

			await Connections.UpdateServerList(loggedOn.CellID);

			IsLoggedOn = true;
			CellId = loggedOn.CellID;
			LoggedInTaskCompletionSource.SetResult();
			LoggedInTaskCompletionSource = new TaskCompletionSource();
		} catch (Exception ex) {
			Log.Error(ex, "Failed to authenticate with Steam");
			Abort(false);
		}
	}
}
