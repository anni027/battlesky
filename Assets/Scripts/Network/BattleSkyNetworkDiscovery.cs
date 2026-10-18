using System;
using System.Net;
using UnityEngine;
using Mirror;
using Mirror.Discovery;

namespace BattleSky.Network
{
    [DisallowMultipleComponent]
    [AddComponentMenu("Network/Network Discovery")]
    public class BattleSkyNetworkDiscovery : NetworkDiscoveryBase<ServerRequest, ServerResponse>
    {
        public new event System.Action<ServerResponse> OnServerFound;

        protected override ServerResponse ProcessRequest(ServerRequest request, IPEndPoint endpoint)
        {
            try
            {
                return new ServerResponse
                {
                    serverId = ServerId,
                    uri = transport.ServerUri()
                };
            }
            catch (NotImplementedException)
            {
                Debug.LogError($"Transport {transport} does not support network discovery");
                throw;
            }
        }

        protected override ServerRequest GetRequest() => new ServerRequest();

        protected override void ProcessResponse(ServerResponse response, IPEndPoint endpoint)
        {
            response.EndPoint = endpoint;

            UriBuilder realUri = new UriBuilder(response.uri)
            {
                Host = response.EndPoint.Address.ToString()
            };
            response.uri = realUri.Uri;

            OnServerFound?.Invoke(response);
        }
    }
}