// SPDX-FileCopyrightText: 2025 Legiayayana
//
// SPDX-License-Identifier: GPL-2.0-or-later

using SteamKit2;
using SteamKit2.CDN;

namespace DepotArchiver.Steam;

internal class ConnectionPool : IDisposable {
	private readonly List<Server> Servers = [];

	internal ConnectionPool(Client client, SteamContent content) {
		Content = content;
		Client = client;
	}

	private SteamContent Content { get; }
	internal Client Client { get; }
	internal Server? ProxyServer { get; private set; }
	private int NextServer { get; set; }

	public void Dispose() => Client.Dispose();

	internal async Task UpdateServerList(uint cellId) {
		var servers = await Content.GetServersForSteamPipe(cellId);

		ProxyServer = servers.FirstOrDefault(x => x.UseAsProxy);

		var orderedServers = servers
							 .Where(server => server.AllowedAppIds.Length == 0 && server.Type is "SteamCache" or "CDN")
							 .OrderBy(server => server.WeightedLoad);

		foreach (var server in orderedServers) {
			for (var i = 0; i < server.NumEntries; i++) {
				Servers.Add(server);
			}
		}

		if (Servers.Count == 0) {
			throw new Exception("Failed to retrieve any download servers.");
		}
	}

	internal Server GetConnection() => Servers[NextServer % Servers.Count];

	internal Server ExchangeBrokenConnection(Server server) {
		lock (Servers) {
			if (Servers[NextServer % Servers.Count] == server) {
				NextServer++;
			}

			return GetConnection();
		}
	}
}
