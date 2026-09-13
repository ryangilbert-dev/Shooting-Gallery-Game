using System.Threading.Tasks;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using Unity.Services.Authentication;
using Unity.Services.Core;
using Unity.Services.Relay;
using Unity.Services.Relay.Models;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ShootingGallery.Networking
{
    /// <summary>
    /// The sole owner of transport-level connection setup. Menu and gameplay code call only
    /// StartHost/StartClient (direct IP, LAN-only) or StartHostWithRelayAsync/
    /// StartClientWithRelayAsync (Unity Relay, works over the real internet - no port forwarding,
    /// no knowing anyone's IP) here and never touch UnityTransport directly, so swapping/adding
    /// connection methods only ever touches this one file. Lives in the Bootstrap scene alongside
    /// the NetworkManager and is marked DontDestroyOnLoad so it survives the Menu -> Arena scene
    /// transition.
    ///
    /// Requires the com.unity.services.authentication and com.unity.services.relay packages
    /// (see NOTES.md for install steps) plus this project linked to a Unity Gaming Services
    /// project (Project Settings > Services) - won't compile or run without both. The direct-IP
    /// methods have no such dependency and keep working standalone (WallDropDiagnostic relies on
    /// exactly that for its own single-machine automated test).
    /// </summary>
    public class ConnectionManager : MonoBehaviour
    {
        public static ConnectionManager Instance { get; private set; }

        public const ushort DefaultPort = 7777;

        /// <summary>The room code from the most recent StartHostWithRelayAsync call, if any -
        /// null/empty if this session never hosted over Relay (e.g. joined instead, or used the
        /// direct-IP path). Survives the Menu -> Arena scene transition since this whole object is
        /// DontDestroyOnLoad, so RoomCodeHUD can keep showing it once actually in the bar - the
        /// menu screen that originally displayed it is long gone by then.</summary>
        public string LastHostJoinCode { get; private set; }

        // SetHostRelayData/SetClientRelayData's final argument turned out to be a plain bool
        // (isSecure) in the installed package version, not the string connection-type identifier
        // ("dtls"/"udp") an older/different version apparently uses - found via an actual compile
        // error (CS1503, argument couldn't convert from string to bool), not assumed up front.
        // True = DTLS-encrypted traffic through the relay, matching the original intent.
        private const bool UseSecureRelayConnection = true;

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }

            Instance = this;
            DontDestroyOnLoad(gameObject);

            // Unity throttles an unfocused window's update rate hard by default (e.g. testing
            // host + client as two instances on one PC, or just alt-tabbing away while hosting) -
            // easily enough to starve the Relay connection's keep-alive and cause exactly the
            // kind of "why did it disconnect, nobody touched anything" transport failure this is
            // meant to prevent. Forced here in code rather than relying on the Player Settings
            // checkbox (Resolution and Presentation > Run In Background) being left on, since
            // that's a per-project setting someone could toggle off later without realizing why
            // it mattered.
            Application.runInBackground = true;
        }

        private void Start()
        {
            // Relay allocations have a limited lifetime and will eventually be torn down by
            // Unity's relay servers regardless of activity - confirmed in practice (a long play
            // session ended in "Failed to establish connection with the Relay server" /
            // "Transport failure! Relay allocation needs to be recreated"), not just a
            // hypothetical edge case. Without this handler that just kills the host and strands
            // everyone; with it, the game recovers to the main menu instead of staying stuck, and
            // hosting/joining again is a fresh Relay allocation (EnsureServicesSignedInAsync
            // doesn't need to re-sign-in for that, just create a new allocation).
            if (NetworkManager.Singleton != null)
            {
                NetworkManager.Singleton.OnTransportFailure += HandleTransportFailure;
            }
        }

        private void HandleTransportFailure()
        {
            Debug.LogWarning("[ConnectionManager] Transport failure (e.g. a Relay allocation expiring after a long session) - " +
                              "returning to the main menu so hosting/joining again is a fresh reconnect.");

            if (NetworkManager.Singleton != null)
            {
                NetworkManager.Singleton.Shutdown();
            }

            SceneManager.LoadScene("MainMenu", LoadSceneMode.Single);
        }

        /// <summary>Starts a host listening on all local interfaces on the given port - direct
        /// IP, LAN-only (or same-machine, like WallDropDiagnostic's use). No Unity Gaming
        /// Services dependency.</summary>
        public bool StartHost(ushort port = DefaultPort)
        {
            UnityTransport transport = NetworkManager.Singleton.GetComponent<UnityTransport>();
            transport.SetConnectionData("0.0.0.0", port, "0.0.0.0");
            return NetworkManager.Singleton.StartHost();
        }

        /// <summary>Starts a client connecting to the given host address and port - direct IP,
        /// LAN-only. No Unity Gaming Services dependency.</summary>
        public bool StartClient(string hostAddress, ushort port = DefaultPort)
        {
            UnityTransport transport = NetworkManager.Singleton.GetComponent<UnityTransport>();
            transport.SetConnectionData(hostAddress, port);
            return NetworkManager.Singleton.StartClient();
        }

        /// <summary>Signs into Unity Gaming Services anonymously - no real account needed per
        /// player, just enough identity for Relay to allocate/join rooms under. Safe to call more
        /// than once; no-ops if already initialized/signed in. Both Relay methods below call this
        /// themselves, so callers don't need to remember to.</summary>
        public async Task EnsureServicesSignedInAsync()
        {
            if (UnityServices.State != ServicesInitializationState.Initialized)
            {
                await UnityServices.InitializeAsync();
            }

            if (!AuthenticationService.Instance.IsSignedIn)
            {
                await AuthenticationService.Instance.SignInAnonymouslyAsync();
            }
        }

        /// <summary>Creates a Unity Relay allocation for up to maxOtherPlayers additional
        /// connections (1 is enough for this 1v1 game) and starts hosting through it. Relay
        /// handles NAT traversal itself, so the joining player never needs the host's IP or any
        /// port forwarding - just the short join code this returns, to be shared however (voice
        /// chat, text, whatever) and typed into StartClientWithRelayAsync.</summary>
        public async Task<string> StartHostWithRelayAsync(int maxOtherPlayers = 1)
        {
            await EnsureServicesSignedInAsync();

            Allocation allocation = await RelayService.Instance.CreateAllocationAsync(maxOtherPlayers);
            string joinCode = await RelayService.Instance.GetJoinCodeAsync(allocation.AllocationId);
            LastHostJoinCode = joinCode;

            UnityTransport transport = NetworkManager.Singleton.GetComponent<UnityTransport>();
            transport.SetHostRelayData(
                allocation.RelayServer.IpV4,
                (ushort)allocation.RelayServer.Port,
                allocation.AllocationIdBytes,
                allocation.Key,
                allocation.ConnectionData,
                UseSecureRelayConnection);

            NetworkManager.Singleton.StartHost();
            return joinCode;
        }

        /// <summary>Joins a host's Relay allocation using the short code they shared - Relay
        /// resolves it to the host's actual connection, so no IP address is ever needed.</summary>
        public async Task StartClientWithRelayAsync(string joinCode)
        {
            await EnsureServicesSignedInAsync();

            JoinAllocation allocation = await RelayService.Instance.JoinAllocationAsync(joinCode);

            UnityTransport transport = NetworkManager.Singleton.GetComponent<UnityTransport>();
            transport.SetClientRelayData(
                allocation.RelayServer.IpV4,
                (ushort)allocation.RelayServer.Port,
                allocation.AllocationIdBytes,
                allocation.Key,
                allocation.ConnectionData,
                allocation.HostConnectionData,
                UseSecureRelayConnection);

            NetworkManager.Singleton.StartClient();
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
