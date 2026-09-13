using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using Unity.Services.Authentication;
using Unity.Services.Core;
using Unity.Services.Lobbies;
using Unity.Services.Lobbies.Models;
using Unity.Services.Relay;
using Unity.Services.Relay.Models;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ShootingGallery.Networking
{
    /// <summary>
    /// The sole owner of transport-level connection setup. Menu and gameplay code call only
    /// StartHost/StartClient (direct IP, LAN-only), StartHostWithRelayAsync/
    /// StartClientWithRelayAsync (raw Unity Relay), or StartHostWithLobbyAsync/
    /// StartClientWithLobbyAsync (Lobby + Relay together - what MainMenuUI actually uses; see
    /// below) here and never touch UnityTransport or the Relay/Lobby services directly, so
    /// swapping/adding connection methods only ever touches this one file. Lives in the Bootstrap
    /// scene alongside the NetworkManager and is marked DontDestroyOnLoad so it survives the Menu
    /// -> Arena scene transition.
    ///
    /// Requires the com.unity.services.authentication and com.unity.services.multiplayer packages
    /// (see NOTES.md for install steps) plus this project linked to a Unity Gaming Services
    /// project (Project Settings > Services) - won't compile or run without both. The direct-IP
    /// methods have no such dependency and keep working standalone (WallDropDiagnostic relies on
    /// exactly that for its own single-machine automated test).
    ///
    /// <para><b>Why Lobby, on top of plain Relay:</b> a lone Relay host (bound, but no peer
    /// connected yet) gets torn down by Unity's own Relay servers after a hard-coded 60 seconds -
    /// confirmed Unity server behavior (see NOTES.md), not something any client-side setting can
    /// extend. That's an unreasonably tight race against a human reading a 6-character code out of
    /// a chat message and typing it in. The fix isn't to fight that timeout - it's to never expose
    /// it to a human in the first place: StartHostWithLobbyAsync creates a Lobby (kept alive
    /// indefinitely by an ordinary heartbeat timer in code - trivial to satisfy reliably, unlike a
    /// person's typing speed) and only creates the actual Relay allocation the instant a second
    /// lobby member has already shown up, ready to consume it immediately. The Lobby code is what
    /// actually gets shared/typed by humans now; the Relay code becomes an internal implementation
    /// detail exchanged automatically through Lobby data.</para>
    /// </summary>
    public class ConnectionManager : MonoBehaviour
    {
        public static ConnectionManager Instance { get; private set; }

        public const ushort DefaultPort = 7777;

        /// <summary>The room code from the most recent successful host flow (Lobby code from
        /// StartHostWithLobbyAsync, or a raw Relay code if StartHostWithRelayAsync was used
        /// directly) - null/empty if this session never hosted, or joined instead. Survives the
        /// Menu -> Arena scene transition since this whole object is DontDestroyOnLoad, so
        /// RoomCodeHUD can keep showing it once actually in the bar - the menu screen that
        /// originally displayed it is long gone by then.</summary>
        public string LastHostJoinCode { get; private set; }

        // SetHostRelayData/SetClientRelayData's final argument turned out to be a plain bool
        // (isSecure) in the installed package version, not the string connection-type identifier
        // ("dtls"/"udp") an older/different version apparently uses - found via an actual compile
        // error (CS1503, argument couldn't convert from string to bool), not assumed up front.
        // True = DTLS-encrypted traffic through the relay, matching the original intent.
        //
        // Set to false after a real two-machine test hit "Failed to establish connection with the
        // Relay server" right at the host's own bind step with this true (see NOTES.md) - a
        // well-documented Unity Relay failure with more than one real-world cause, and the DTLS
        // handshake some networks mishandle (while plain UDP through the same relay works fine)
        // is what this specific connection confirmed it was: switching to false fixed a real
        // connection on the very next test. Relay's core NAT-traversal function doesn't depend on
        // isSecure either way - this only trades away encryption of the relay traffic itself. If
        // this ever needs to go back to true (e.g. encryption becomes a real requirement), that
        // change needs its own re-test against this exact failure, not just a confident flip back.
        private const bool UseSecureRelayConnection = false;

        // --- Lobby (see the class-level "Why Lobby" note above) ---
        private const string LobbyName = "ShootingGalleryDuel";
        private const int LobbyMaxPlayers = 2;
        private const string RelayJoinCodeDataKey = "relayJoinCode";

        // Well under Lobby's own missed-heartbeat expiry, but no need to hammer the service either
        // - this only has to be frequent enough that a person's real-world sharing/joining pace
        // never catches up to it, and "every 15 seconds, forever, from a timer" clears that bar
        // with enormous margin no matter how long the human side takes.
        private const float LobbyHeartbeatIntervalSeconds = 15f;
        private const float LobbyPollIntervalSeconds = 1.5f;

        // Safety nets so a friend who never shows up (or a host who vanishes mid-handshake)
        // doesn't leave the other side waiting forever with no feedback - not a re-creation of the
        // Relay race this whole Lobby flow exists to avoid, just a generous outer bound.
        private const float HostWaitForPlayerTimeoutSeconds = 300f;
        private const float ClientWaitForRelayCodeTimeoutSeconds = 30f;

        private string hostLobbyId;
        private Coroutine lobbyHeartbeatCoroutine;

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
            Debug.LogWarning("[ConnectionManager] Transport failure (e.g. a Relay allocation expiring after a long session, " +
                              "or the initial Relay connection never establishing) - returning to the main menu so hosting/joining again is a fresh reconnect.");

            // Real failure mode hit in testing: this fires on the host's machine right after
            // StartHostWithLobbyAsync already published a working-looking relay code into its
            // lobby and returned "success" (NetworkManager.StartHost() only reports whether it was
            // *called*, not whether the connection actually established - the real failure/success
            // is only known a moment later, asynchronously, via this same event). Without this
            // cleanup, that lobby was left abandoned with a dead relay code still in it - no longer
            // heartbeated (already stopped right after publishing), but not deleted either, so it
            // could still look "current" to a joining player for a little while. A no-op if this
            // instance was never hosting (hostLobbyId only gets set by StartHostWithLobbyAsync).
            _ = TryDeleteHostLobbyAsync();

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

        /// <summary>The host flow MainMenuUI actually calls - see the class-level "Why Lobby"
        /// note. Creates a Lobby, then blocks until a second player actually joins that lobby
        /// before ever touching Relay - which can take an arbitrarily long time. onRoomCodeReady
        /// fires as soon as the code actually exists (right after the lobby is created), which is
        /// the only point that matters for a caller displaying/sharing it - waiting for this whole
        /// method to return instead (as an earlier version did) meant the code never appeared on
        /// screen at all during the wait, since that's exactly the period this method is still
        /// running. onStatusUpdate separately reports a short human-readable phrase at each
        /// stage.</summary>
        public async Task<string> StartHostWithLobbyAsync(Action<string> onStatusUpdate = null, Action<string> onRoomCodeReady = null)
        {
            await EnsureServicesSignedInAsync();

            onStatusUpdate?.Invoke("Creating room...");
            Lobby lobby = await LobbyService.Instance.CreateLobbyAsync(
                LobbyName, LobbyMaxPlayers, new CreateLobbyOptions { IsPrivate = true });
            hostLobbyId = lobby.Id;
            StartLobbyHeartbeat(lobby.Id);

            // The code is fully valid the moment the lobby exists - no reason to wait for anything
            // else before letting the caller show/share it.
            LastHostJoinCode = lobby.LobbyCode;
            onRoomCodeReady?.Invoke(lobby.LobbyCode);

            try
            {
                onStatusUpdate?.Invoke("Waiting for a player to join...");
                float waited = 0f;
                while (lobby.Players.Count < LobbyMaxPlayers)
                {
                    if (waited >= HostWaitForPlayerTimeoutSeconds)
                    {
                        throw new TimeoutException("Nobody joined the room in time.");
                    }

                    await Task.Delay(TimeSpan.FromSeconds(LobbyPollIntervalSeconds));
                    waited += LobbyPollIntervalSeconds;
                    lobby = await LobbyService.Instance.GetLobbyAsync(hostLobbyId);
                }

                // A real player is already sitting in the lobby right now, so the Relay allocation
                // created here has a peer ready to consume it near-instantly - nowhere close to the
                // 60-second "alone" window plain Relay hosting has to race against.
                onStatusUpdate?.Invoke("Player joined - opening connection...");
                string relayJoinCode = await StartHostWithRelayAsync();

                await LobbyService.Instance.UpdateLobbyAsync(hostLobbyId, new UpdateLobbyOptions
                {
                    Data = new Dictionary<string, DataObject>
                    {
                        [RelayJoinCodeDataKey] = new DataObject(DataObject.VisibilityOptions.Member, relayJoinCode)
                    }
                });

                // The lobby's own job is done the moment the waiting client can read the code
                // above - stop heartbeating so it expires on its own shortly after, rather than
                // deleting it outright here, which would risk racing that client's next poll (see
                // StartClientWithLobbyAsync) right as it tries to read what was just published.
                StopLobbyHeartbeat();

                return lobby.LobbyCode;
            }
            catch
            {
                StopLobbyHeartbeat();
                await TryDeleteHostLobbyAsync();
                throw;
            }
        }

        /// <summary>The join flow MainMenuUI actually calls - joins the host's Lobby by its
        /// (long-lived) code, then waits for the host to publish the real Relay join code into
        /// that lobby's data (see StartHostWithLobbyAsync) before handing off to the existing
        /// StartClientWithRelayAsync. onStatusUpdate mirrors the host side's per-stage
        /// updates.</summary>
        public async Task StartClientWithLobbyAsync(string lobbyCode, Action<string> onStatusUpdate = null)
        {
            await EnsureServicesSignedInAsync();

            onStatusUpdate?.Invoke("Joining room...");
            Lobby lobby;
            try
            {
                lobby = await LobbyService.Instance.JoinLobbyByCodeAsync(lobbyCode);
            }
            catch (LobbyServiceException e) when (e.Reason == LobbyExceptionReason.Conflict)
            {
                // Hit in real testing: this same (anonymous, persisted) identity had already
                // joined this lobby once - most likely from an earlier attempt in this same
                // session that didn't get all the way to a working game (e.g. the host's own
                // Relay connection failing right after this client had already joined the lobby,
                // prompting a retry). JoinLobbyByCodeAsync correctly refuses to join an identity
                // that's already a member rather than silently no-op'ing, so recover by finding
                // the lobby we're apparently already in instead of treating this as a hard
                // failure - a plain retry would otherwise be stuck failing this exact way forever.
                onStatusUpdate?.Invoke("Already in this room - resuming...");
                lobby = await FindAlreadyJoinedLobbyByCodeAsync(lobbyCode);
                if (lobby == null)
                {
                    throw; // Couldn't actually find it - surface the original conflict instead of masking it.
                }
            }

            onStatusUpdate?.Invoke("In the room - waiting for the host to open the connection...");
            string relayJoinCode = null;
            float waited = 0f;
            while (string.IsNullOrEmpty(relayJoinCode))
            {
                if (waited >= ClientWaitForRelayCodeTimeoutSeconds)
                {
                    throw new TimeoutException("The host never opened the connection.");
                }

                await Task.Delay(TimeSpan.FromSeconds(LobbyPollIntervalSeconds));
                waited += LobbyPollIntervalSeconds;
                lobby = await LobbyService.Instance.GetLobbyAsync(lobby.Id);
                if (lobby.Data != null && lobby.Data.TryGetValue(RelayJoinCodeDataKey, out DataObject relayData))
                {
                    relayJoinCode = relayData.Value;
                }
            }

            onStatusUpdate?.Invoke("Connecting...");
            await StartClientWithRelayAsync(relayJoinCode);
        }

        /// <summary>Looks through the current (anonymous) identity's already-joined lobbies for
        /// one matching the given code - used to recover from JoinLobbyByCodeAsync's 409 Conflict
        /// when we're already a member (see StartClientWithLobbyAsync). Returns null if none
        /// match, e.g. the conflict was actually about a different lobby entirely.</summary>
        private async Task<Lobby> FindAlreadyJoinedLobbyByCodeAsync(string lobbyCode)
        {
            // GetJoinedLobbiesAsync's own implementation returns the raw (possibly null) response
            // body directly rather than normalizing an empty/missing result to an empty list - a
            // bare foreach over that would be a real NullReferenceException, not a hypothetical
            // one, so guard it explicitly rather than trusting the SDK to always hand back a list.
            List<string> joinedLobbyIds = await LobbyService.Instance.GetJoinedLobbiesAsync();
            if (joinedLobbyIds == null)
            {
                return null;
            }

            foreach (string lobbyId in joinedLobbyIds)
            {
                Lobby candidate = await LobbyService.Instance.GetLobbyAsync(lobbyId);
                if (candidate != null && string.Equals(candidate.LobbyCode, lobbyCode, StringComparison.OrdinalIgnoreCase))
                {
                    return candidate;
                }
            }

            return null;
        }

        private void StartLobbyHeartbeat(string lobbyId)
        {
            StopLobbyHeartbeat();
            lobbyHeartbeatCoroutine = StartCoroutine(LobbyHeartbeatLoop(lobbyId));
        }

        private void StopLobbyHeartbeat()
        {
            if (lobbyHeartbeatCoroutine != null)
            {
                StopCoroutine(lobbyHeartbeatCoroutine);
                lobbyHeartbeatCoroutine = null;
            }
        }

        private IEnumerator LobbyHeartbeatLoop(string lobbyId)
        {
            var wait = new WaitForSecondsRealtime(LobbyHeartbeatIntervalSeconds);
            while (true)
            {
                yield return wait;

                // Fire-and-forget: a coroutine can't await a Task directly, and a missed/failed
                // ping just means the lobby ages a little closer to its own expiry, not an
                // immediate failure - logged rather than thrown so one flaky ping can't take down
                // the whole hosting flow.
                _ = SendHeartbeatSafeAsync(lobbyId);
            }
        }

        private async Task SendHeartbeatSafeAsync(string lobbyId)
        {
            try
            {
                await LobbyService.Instance.SendHeartbeatPingAsync(lobbyId);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[ConnectionManager] Lobby heartbeat failed (harmless unless it keeps happening - the lobby will just expire a bit early): " + e.Message);
            }
        }

        private async Task TryDeleteHostLobbyAsync()
        {
            if (string.IsNullOrEmpty(hostLobbyId))
            {
                return;
            }

            string lobbyToDelete = hostLobbyId;
            hostLobbyId = null;

            try
            {
                await LobbyService.Instance.DeleteLobbyAsync(lobbyToDelete);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[ConnectionManager] Failed to clean up an abandoned host lobby (harmless - it expires on its own once heartbeats stop): " + e.Message);
            }
        }

        public void Shutdown()
        {
            StopLobbyHeartbeat();

            if (NetworkManager.Singleton != null)
            {
                NetworkManager.Singleton.Shutdown();
            }
        }
    }
}
