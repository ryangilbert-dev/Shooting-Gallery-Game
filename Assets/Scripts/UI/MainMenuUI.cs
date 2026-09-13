using System;
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

        private void Awake()
        {
            hostButton.onClick.AddListener(OnHostClicked);
            joinButton.onClick.AddListener(OnJoinClicked);
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
                SetStatus("Connected - waiting for host...");
            }
            catch (Exception e)
            {
                Debug.LogError("[MainMenuUI] Failed to join: " + e);
                SetStatus("Failed to join - " + e.Message);
                SetInteractable(true);
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
                roomCodeDisplayText.text = "Room Code: " + code;
            }
        }
    }
}
