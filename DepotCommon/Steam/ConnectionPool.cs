// SPDX-FileCopyrightText: 2025 Legiayayana
//
// SPDX-License-Identifier: GPL-2.0-or-later

using SteamKit2;
using SteamKit2.CDN;

namespace DepotCommon.Steam;

public sealed class ConnectionPool : IDisposable {
	private readonly List<Server> Servers = [];

	public ConnectionPool(Client client, SteamContent content) {
		Content = content;
		Client = client;
	}

	private SteamContent Content { get; }
	public Client Client { get; }
	public Server? ProxyServer { get; private set; }
	private int NextServer { get; set; }
	public int Attempts => Servers.Count;

	public void Dispose() => Client.Dispose();

	public async Task UpdateServerList(uint cellId) {
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

	public Server Connection => Servers[NextServer % Servers.Count];

	public Server ExchangeBrokenConnection(Server server) {
		lock (Servers) {
			if (Servers[NextServer % Servers.Count] == server) {
				NextServer++;
			}

			return Connection;
		}
	}
}
