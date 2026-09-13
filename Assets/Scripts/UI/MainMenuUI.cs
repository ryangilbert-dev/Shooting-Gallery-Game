using System;
using System.Collections;
using ShootingGallery.Networking;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace ShootingGallery.UI
{
    /// <summary>Host/Join menu, built on Unity Lobby + Relay together so friends can connect over
    /// the real internet with just a short room code - no IP address, no port forwarding, and
    /// (thanks to Lobby) no race against Relay's 60-second lone-host timeout while a code gets
    /// shared and typed in (see ConnectionManager's class-level "Why Lobby" note). A third
    /// "Practice Solo" option hosts a plain local session with no internet-facing setup at all,
    /// for testing alone against the practice dummy. Talks only to ConnectionManager, never to
    /// UnityTransport or the Relay/Lobby/Authentication services directly.</summary>
    public class MainMenuUI : MonoBehaviour
    {
        [SerializeField] private InputField joinCodeField;
        [SerializeField] private Button hostButton;
        [SerializeField] private Button joinButton;
        [SerializeField] private Button practiceSoloButton;
        [SerializeField] private Text statusText;
        [SerializeField] private Text roomCodeDisplayText;
        [SerializeField] private string arenaSceneName = "Arena";

        // How long to wait, after the Relay-level join call succeeds, for Netcode to actually
        // finish connecting to the host before giving up and telling the player it failed. The
        // Relay call itself only proves the join code resolved to a live allocation - it says
        // nothing about whether the host's Netcode session is still there to accept the
        // connection, which is exactly the "stuck on waiting for host forever" gap this closes.
        [SerializeField] private float joinTimeoutSeconds = 15f;

        private Coroutine joinTimeoutCoroutine;
        private bool joinResolved;

        private void Awake()
        {
            hostButton.onClick.AddListener(OnHostClicked);
            joinButton.onClick.AddListener(OnJoinClicked);
            practiceSoloButton.onClick.AddListener(OnPracticeSoloClicked);
        }

        private void OnDestroy()
        {
            // Covers both the success path (this scene unloads once Netcode scene-syncs us into
            // Arena) and any other early teardown - without this, a callback could still fire into
            // a MonoBehaviour whose own GameObject is already gone.
            CleanupJoinWatch();
        }

        private async void OnHostClicked()
        {
            DisableLocalAudioListener();
            SetInteractable(false);
            SetStatus("Signing in...");

            try
            {
                // Lobby-backed hosting (see ConnectionManager's class-level "Why Lobby" note) -
                // this is what actually fixes the "friend didn't type the code in time" failure:
                // the code shown/shared below is a Lobby code, kept alive indefinitely by a
                // heartbeat timer rather than racing Relay's 60-second lone-host cutoff, and the
                // real Relay connection only ever gets created the instant a player has already
                // joined that lobby and is ready for it. Critically, that means this whole call
                // doesn't return until a second player has already joined - so the code has to be
                // surfaced via onRoomCodeReady the moment it actually exists, not from the return
                // value, or it would never appear on screen at all during the wait (exactly what
                // was being seen: stuck on "Waiting for a player to join..." with no code visible
                // or shareable anywhere).
                await ConnectionManager.Instance.StartHostWithLobbyAsync(SetStatus, HandleRoomCodeReady);
                SetStatus("Hosting - loading arena...");
                NetworkManager.Singleton.SceneManager.LoadScene(arenaSceneName, LoadSceneMode.Single);
            }
            catch (Exception e)
            {
                Debug.LogError("[MainMenuUI] Failed to start host: " + e);
                SetStatus("Failed to start host - " + e.Message);
                SetInteractable(true);
            }
        }

        /// <summary>The old "just click Host and go" shortcut, now specifically for testing
        /// alone. The Lobby-backed Host flow above deliberately waits for a real second player
        /// before ever loading Arena, which means it can't double as a quick way to reach the
        /// practice dummy solo anymore - this restores that by hosting a plain direct-IP session
        /// with no Relay/Lobby round trip at all, since nobody else is going to connect to
        /// it.</summary>
        private void OnPracticeSoloClicked()
        {
            DisableLocalAudioListener();
            SetInteractable(false);
            SetStatus("Starting solo practice...");

            try
            {
                ConnectionManager.Instance.StartHost();
                NetworkManager.Singleton.SceneManager.LoadScene(arenaSceneName, LoadSceneMode.Single);
            }
            catch (Exception e)
            {
                Debug.LogError("[MainMenuUI] Failed to start solo practice: " + e);
                SetStatus("Failed to start - " + e.Message);
                SetInteractable(true);
            }
        }

        private async void OnJoinClicked()
        {
            string code = joinCodeField.text.Trim().ToUpperInvariant();
            if (string.IsNullOrEmpty(code))
            {
                SetStatus("Enter a room code first.");
                return;
            }

            DisableLocalAudioListener();
            SetInteractable(false);
            SetStatus($"Joining {code}...");

            try
            {
                // Lobby-backed joining (see ConnectionManager's class-level "Why Lobby" note) -
                // this call itself waits for the host to actually open the Relay connection before
                // ever calling StartClientWithRelayAsync internally, so by the time we reach the
                // status line below, Netcode's own handshake is the only thing left to complete.
                await ConnectionManager.Instance.StartClientWithLobbyAsync(code, SetStatus);
                SetStatus("Connected to Relay - waiting for host...");
                BeginWaitingForConnection();
            }
            catch (Exception e)
            {
                Debug.LogError("[MainMenuUI] Failed to join: " + e);
                SetStatus("Failed to join - " + e.Message);
                SetInteractable(true);
            }
        }

        /// <summary>A successful StartClientWithRelayAsync only proves the join code resolved to
        /// a live Relay allocation - it says nothing about whether Netcode's own handshake with
        /// the host actually completes after that. Without this, a handshake that silently never
        /// finishes (host's session already gone, a version/prefab mismatch, etc.) just left the
        /// status text frozen on "waiting for host" forever with no feedback at all - this is what
        /// was actually being seen, not the practice dummy taking a slot (that only ever affects
        /// MatchManager's lane bookkeeping, which doesn't run until after this succeeds).</summary>
        private void BeginWaitingForConnection()
        {
            joinResolved = false;
            NetworkManager.Singleton.OnClientConnectedCallback += HandleConnectedWhileJoining;
            NetworkManager.Singleton.OnClientDisconnectCallback += HandleDisconnectedWhileJoining;
            joinTimeoutCoroutine = StartCoroutine(JoinTimeoutCountdown());
        }

        private void HandleConnectedWhileJoining(ulong clientId)
        {
            if (clientId != NetworkManager.Singleton.LocalClientId)
            {
                return; // Some other client connecting to the same host - not about us.
            }

            // Success - from here Netcode's own scene sync takes over and unloads this scene
            // (see OnDestroy), so there's no further status text to set.
            joinResolved = true;
            CleanupJoinWatch();
        }

        private void HandleDisconnectedWhileJoining(ulong clientId)
        {
            if (joinResolved || clientId != NetworkManager.Singleton.LocalClientId)
            {
                return;
            }

            joinResolved = true;
            CleanupJoinWatch();
            SetStatus("Failed to join - disconnected before connecting (room may be full, or " +
                       "the host isn't running anymore).");
            SetInteractable(true);
        }

        private IEnumerator JoinTimeoutCountdown()
        {
            yield return new WaitForSeconds(joinTimeoutSeconds);

            if (!joinResolved)
            {
                joinResolved = true;
                CleanupJoinWatch();
                SetStatus("Failed to join - timed out waiting for the host. Double-check the " +
                           "room code and that they're still hosting.");
                SetInteractable(true);
                NetworkManager.Singleton.Shutdown();
            }
        }

        private void CleanupJoinWatch()
        {
            if (NetworkManager.Singleton != null)
            {
                NetworkManager.Singleton.OnClientConnectedCallback -= HandleConnectedWhileJoining;
                NetworkManager.Singleton.OnClientDisconnectCallback -= HandleDisconnectedWhileJoining;
            }

            if (joinTimeoutCoroutine != null)
            {
                StopCoroutine(joinTimeoutCoroutine);
                joinTimeoutCoroutine = null;
            }
        }

        /// <summary>Disables this scene's own AudioListener (on MenuCamera) right as we start
        /// leaving it, rather than waiting for the Arena scene load to unload it. The "There are 2
        /// audio listeners in the scene" warning was showing up for the first few frames of
        /// hosting/joining - a real (if harmless) transient overlap between this listener and the
        /// newly-spawned player's own, not an ongoing bug - since Netcode's scene sync takes a few
        /// frames to actually unload this scene, and the player's AudioListener enables itself
        /// (PlayerController) as soon as it spawns, before that unload completes.</summary>
        private void DisableLocalAudioListener()
        {
            AudioListener menuListener = FindFirstObjectByType<AudioListener>();
            if (menuListener != null)
            {
                menuListener.enabled = false;
            }
        }

        private void SetInteractable(bool interactable)
        {
            hostButton.interactable = interactable;
            joinButton.interactable = interactable;
            practiceSoloButton.interactable = interactable;
        }

        private void SetStatus(string message)
        {
            if (statusText != null)
            {
                statusText.text = message;
            }
        }

        /// <summary>Called by ConnectionManager the instant the room code actually exists -
        /// which, for the Lobby-backed host flow, is well before StartHostWithLobbyAsync itself
        /// returns (it doesn't return until someone's already joined). Displaying it here rather
        /// than after the await is what makes it visible/shareable during the wait at all.
        /// </summary>
        private void HandleRoomCodeReady(string code)
        {
            SetRoomCode(code);

            // Convenience for pasting into chat/voice text - not a race against any timeout, since
            // the Lobby code doesn't expire on its own while ConnectionManager keeps heartbeating
            // it.
            GUIUtility.systemCopyBuffer = code;
        }

        private void SetRoomCode(string code)
        {
            if (roomCodeDisplayText != null)
            {
                // Unlike the old raw-Relay code, this Lobby code doesn't race a 60-second cutoff -
                // ConnectionManager keeps heartbeating the lobby alive for as long as this screen
                // (and then RoomCodeHUD, once in the bar) is waiting for someone to join. No need
                // to convey urgency here, just that it's already on the clipboard.
                roomCodeDisplayText.text = "Room Code: " + code + "\n(copied to clipboard)";
            }
        }
    }
}
