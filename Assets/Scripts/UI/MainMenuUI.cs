using System;
using System.Collections;
using ShootingGallery.Networking;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace ShootingGallery.UI
{
    /// <summary>Host/Join menu, built on Unity Relay so friends can connect over the real
    /// internet with just a short room code - no IP address, no port forwarding. Talks only to
    /// ConnectionManager, never to UnityTransport or the Relay/Authentication services directly.
    /// </summary>
    public class MainMenuUI : MonoBehaviour
    {
        [SerializeField] private InputField joinCodeField;
        [SerializeField] private Button hostButton;
        [SerializeField] private Button joinButton;
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
                SetStatus("Creating room...");
                string joinCode = await ConnectionManager.Instance.StartHostWithRelayAsync();
                SetRoomCode(joinCode);

                // Unity Relay gives a lone host (bound, but no peer connected yet) a hard 60-second
                // window before tearing the allocation down - confirmed Relay server behavior, not
                // something any client-side setting can extend (see NOTES.md). The only real lever
                // we have is cutting down how long it takes a human to get the code from this
                // screen into a friend's Join field, so copy it to the clipboard immediately rather
                // than relying on them reading/retyping six characters correctly under time
                // pressure.
                GUIUtility.systemCopyBuffer = joinCode;
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
                await ConnectionManager.Instance.StartClientWithRelayAsync(code);
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
            SetStatus("Failed to join - disconnected before connecting (room may be full, the " +
                       "code may have expired, or the host isn't running anymore).");
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
        }

        private void SetStatus(string message)
        {
            if (statusText != null)
            {
                statusText.text = message;
            }
        }

        private void SetRoomCode(string code)
        {
            if (roomCodeDisplayText != null)
            {
                // The urgency here isn't decoration - a lone host has a hard, Unity-enforced 60
                // second window before Relay tears the allocation down (see the comment in
                // OnHostClicked / NOTES.md), so whoever's reading this needs to know to move fast
                // rather than casually alt-tabbing to find a friend to send it to.
                roomCodeDisplayText.text = "Room Code: " + code + "\n(copied - share it now, expires in ~60s if unused)";
            }
        }
    }
}
