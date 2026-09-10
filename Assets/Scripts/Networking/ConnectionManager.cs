using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using UnityEngine;

namespace ShootingGallery.Networking
{
    /// <summary>
    /// The sole owner of transport-level connection setup. Menu and gameplay code call only
    /// StartHost/StartClient/Shutdown here and never touch UnityTransport directly, so that
    /// swapping in Unity Relay/Lobby later for real NAT-traversed internet play only requires
    /// changes inside this one file. Lives in the Bootstrap scene alongside the NetworkManager
    /// and is marked DontDestroyOnLoad so it survives the Menu -> Arena scene transition.
    /// </summary>
    public class ConnectionManager : MonoBehaviour
    {
        public static ConnectionManager Instance { get; private set; }

        public const ushort DefaultPort = 7777;

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }

            Instance = this;
            DontDestroyOnLoad(gameObject);
        }

        /// <summary>Starts a host listening on all local interfaces on the given port.</summary>
        public bool StartHost(ushort port = DefaultPort)
        {
            UnityTransport transport = NetworkManager.Singleton.GetComponent<UnityTransport>();
            transport.SetConnectionData("0.0.0.0", port, "0.0.0.0");
            return NetworkManager.Singleton.StartHost();
        }

        /// <summary>Starts a client connecting to the given host address and port.</summary>
        public bool StartClient(string hostAddress, ushort port = DefaultPort)
        {
            UnityTransport transport = NetworkManager.Singleton.GetComponent<UnityTransport>();
            transport.SetConnectionData(hostAddress, port);
            return NetworkManager.Singleton.StartClient();
        }

        public void Shutdown()
        {
            if (NetworkManager.Singleton != null)
            {
                NetworkManager.Singleton.Shutdown();
            }
        }
    }
}
